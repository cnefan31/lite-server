using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteServer.Logging;
using LiteServer.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteServer.Sample.Push;

/// <summary>
/// 定时向所有 WebSocket 客户端推送系统快照的常驻服务。
/// <para>
/// 推送内容分三块：系统配置（基本不变）、运行时性能（GC/线程池）、进程占用（内存/CPU/磁盘）。
/// </para>
/// <para>
/// 设计要点：
/// ① 无在线客户端时只做一次 Count 判断，跳过采集与序列化——空闲近乎零开销；
/// ② 跨平台差异如实处理：macOS 上 PrivateMemorySize64 / PeakWorkingSet64 / HandleCount
///    恒为 0，这些字段转成 null 而非 0，避免前端画出误导曲线；
/// ③ CPU 使用率按「两次采样的处理器时间差 / 墙钟差 / 核数」计算，首次采样无基线故为 0；
/// ④ 单轮采集异常不退出循环，否则一次偶发错误会让推送永久停摆。
/// </para>
/// </summary>
public sealed class PushService : BackgroundService
{
    private const int MinIntervalMs = 200;
    private const int MaxIntervalMs = 60_000;
    private const int DefaultIntervalMs = 1000;

    private readonly WebSocketHub _hub;
    private readonly ILogger<PushService> _logger;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    // 进程创建时固定不变的信息，只采集一次。
    private readonly SystemInfo _staticSystemInfo;

    private int _intervalMs = DefaultIntervalMs;
    private long _seq;
    private long _sentTotal;
    private string? _lastSentAt;
    private volatile bool _running;

    // 增量基线
    private int _lastGen0, _lastGen1, _lastGen2;
    private double _lastPauseMs;
    private TimeSpan _lastCpuTime;
    private long _lastCpuSampleTicks;

    public PushService(WebSocketHub hub, ILogger<PushService> logger)
    {
        _hub = hub;
        _logger = logger;

        using Process proc = Process.GetCurrentProcess();
        _staticSystemInfo = new SystemInfo(
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            Framework: RuntimeInformation.FrameworkDescription,
            // 判真 AOT 不能用 RuntimeFeature：csproj 里 PublishAot=true 会让普通 build 的
            // runtimeconfig 也把 IsDynamicCodeSupported / IsDynamicCodeCompiled 都置 false。
            // 可靠判据：AOT 产物是同目录的原生可执行文件，不会生成 <AssemblyName>.runtimeconfig.json。
            RuntimeMode: DetectRuntimeMode(),
            ProcessorCount: Environment.ProcessorCount,
            // 用入口程序集名而非 ProcessName / ProcessPath：
            // 当以 `dotnet app.dll` 启动时，后两者都会给出宿主名 "dotnet"/"dotnet.dll"，
            // 只有入口程序集名在 JIT 与 AOT 两种模式下都指向真实应用。
            ProcessName: Assembly.GetEntryAssembly()?.GetName().Name
                         ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath)
                         ?? proc.ProcessName,
            ProcessId: proc.Id,
            StartTime: proc.StartTime.ToString("O"),
            CurrentDirectory: AppContext.BaseDirectory,
            Culture: System.Globalization.CultureInfo.CurrentCulture.Name is { Length: > 0 } c ? c : "(invariant)");
    }

    /// <summary>判定当前是 JIT 还是 NativeAOT。</summary>
    private static string DetectRuntimeMode()
    {
        string? name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(name)) return "NativeAOT";

        string cfg = Path.Combine(AppContext.BaseDirectory, name + ".runtimeconfig.json");
        return File.Exists(cfg) ? "JIT" : "NativeAOT";
    }

    public int IntervalMs => Volatile.Read(ref _intervalMs);

    public int SetInterval(int ms)
    {
        int clamped = Math.Clamp(ms, MinIntervalMs, MaxIntervalMs);
        Volatile.Write(ref _intervalMs, clamped);
        return clamped;
    }

    public long SentTotal => Interlocked.Read(ref _sentTotal);

    public long LastSeq => Interlocked.Read(ref _seq);

    public string? LastSentAt => Volatile.Read(ref _lastSentAt);

    public PushStatus Snapshot() => new(
        Running: _running,
        IntervalMs: IntervalMs,
        SentTotal: SentTotal,
        LastSeq: LastSeq,
        OnlineClients: _hub.Count,
        LastSentAt: LastSentAt,
        Clients: _hub.OnlineClients());

    /// <summary>立即推送一帧。返回是否真的发给了至少一个客户端。</summary>
    public async Task<bool> PushOnceAsync(string message = "手动触发")
    {
        if (_hub.Count == 0) return false;
        await PushFrameAsync(message).ConfigureAwait(false);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _running = true;
        LiteLogger.ServerStarted(_logger, $"推送服务（间隔 {DefaultIntervalMs}ms）", _staticSystemInfo.RuntimeMode);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_hub.Count == 0) continue;

            try
            {
                await PushFrameAsync("定时推送").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LiteLogger.UnhandledException(_logger, ex, "PUSH", "/ws");
            }
        }

        _running = false;
        LiteLogger.ServerStopped(_logger, "推送服务");
    }

    private async Task PushFrameAsync(string message)
    {
        long seq = Interlocked.Increment(ref _seq);

        var frame = new PushFrame(
            Seq: seq,
            Kind: "snapshot",
            SentAt: DateTimeOffset.UtcNow.ToString("O"),
            OnlineClients: _hub.Count,
            Payload: new PushSnapshot(_staticSystemInfo, CollectPerf(), CollectProcess(message)));

        await _hub.BroadcastAsync(JsonSerializer.Serialize(frame, PushJsonContext.Default.PushFrame))
            .ConfigureAwait(false);

        Interlocked.Increment(ref _sentTotal);
        Volatile.Write(ref _lastSentAt, frame.SentAt);
    }

    /// <summary>采集运行时性能指标，并计算增量。</summary>
    private PerfInfo CollectPerf()
    {
        GCMemoryInfo gci = GC.GetGCMemoryInfo();
        int g0 = GC.CollectionCount(0);
        int g1 = GC.CollectionCount(1);
        int g2 = GC.CollectionCount(2);
        double pauseTotal = GC.GetTotalPauseDuration().TotalMilliseconds;

        double pauseDelta = Math.Max(0, pauseTotal - _lastPauseMs);
        int d0 = g0 - _lastGen0;
        int d1 = g1 - _lastGen1;
        int d2 = g2 - _lastGen2;

        _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2; _lastPauseMs = pauseTotal;

        double cpuPercent = SampleCpuPercent(out _);

        return new PerfInfo(
            GcHeapKb: gci.HeapSizeBytes / 1024,
            GcCommittedKb: gci.TotalCommittedBytes / 1024,
            GcFragmentedKb: gci.FragmentedBytes / 1024,
            Gen0Collections: g0,
            Gen1Collections: g1,
            Gen2Collections: g2,
            Gen0Delta: d0,
            Gen1Delta: d1,
            Gen2Delta: d2,
            GcPauseTotalMs: Math.Round(pauseTotal, 1),
            GcPauseDeltaMs: Math.Round(pauseDelta, 1),
            TotalAllocatedKb: GC.GetTotalAllocatedBytes(precise: false) / 1024,
            ThreadPoolWorkers: ThreadPool.ThreadCount,
            ThreadPoolPending: (int)ThreadPool.PendingWorkItemCount,
            ThreadPoolCompleted: ThreadPool.CompletedWorkItemCount,
            ProcessCpuPercent: Math.Round(cpuPercent, 1),
            UptimeSec: Math.Round(_uptime.Elapsed.TotalSeconds, 1));
    }

    /// <summary>
    /// 计算进程 CPU 使用率（占单核百分比，可超过 100）。
    /// 用两次采样之间的处理器时间增量除以墙钟增量；首次调用无基线，返回 0。
    /// </summary>
    private double SampleCpuPercent(out Process proc)
    {
        proc = Process.GetCurrentProcess();
        proc.Refresh();

        long now = Stopwatch.GetTimestamp();
        TimeSpan cpu = proc.TotalProcessorTime;

        double percent = 0;
        if (_lastCpuSampleTicks != 0)
        {
            double wallMs = (now - _lastCpuSampleTicks) * 1000.0 / Stopwatch.Frequency;
            double cpuMs = (cpu - _lastCpuTime).TotalMilliseconds;
            if (wallMs > 0) percent = cpuMs / wallMs * 100.0;
        }

        _lastCpuTime = cpu;
        _lastCpuSampleTicks = now;
        return Math.Max(0, percent);
    }

    /// <summary>采集进程占用。跨平台无效的字段置 null。</summary>
    private ProcessInfo CollectProcess(string message)
    {
        Process proc = Process.GetCurrentProcess();
        proc.Refresh();

        // macOS 上这些属性恒为 0，视作「不可用」而非「真的是 0」。
        long? privateMb = proc.PrivateMemorySize64 > 0 ? proc.PrivateMemorySize64 / 1048576 : null;
        long? peakMb = proc.PeakWorkingSet64 > 0 ? proc.PeakWorkingSet64 / 1048576 : null;

        int? osThreads = null;
        int? handles = null;
        try
        {
            int t = proc.Threads.Count;
            if (t > 0) osThreads = t;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException) { }

        try
        {
            int h = proc.HandleCount;
            if (h > 0) handles = h;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException) { }

        long? diskTotalGb = null;
        long? diskFreeGb = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "/");
            if (drive.IsReady && drive.TotalSize > 0)
            {
                diskTotalGb = drive.TotalSize / 1073741824;
                diskFreeGb = drive.AvailableFreeSpace / 1073741824;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // 某些容器/受限环境下拿不到磁盘信息，留空即可。
        }

        int nicCount = 0;
        try
        {
            nicCount = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Count(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }

        return new ProcessInfo(
            WorkingSetMb: proc.WorkingSet64 / 1048576,
            PrivateMb: privateMb,
            PeakWorkingSetMb: peakMb,
            VirtualMb: proc.VirtualMemorySize64 / 1048576,
            ManagedThreads: ThreadPool.ThreadCount,
            OsThreads: osThreads,
            HandleCount: handles,
            CpuUserMs: Math.Round(proc.UserProcessorTime.TotalMilliseconds, 0),
            CpuPrivilegedMs: Math.Round(proc.PrivilegedProcessorTime.TotalMilliseconds, 0),
            CpuTotalMs: Math.Round(proc.TotalProcessorTime.TotalMilliseconds, 0),
            DiskTotalGb: diskTotalGb,
            DiskFreeGb: diskFreeGb,
            UpNetworkInterfaces: nicCount,
            Message: message);
    }

    /// <summary>停机时通知客户端，避免前端只看到静默断线。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_hub.Count > 0)
            {
                var bye = new PushFrame(
                    Seq: Interlocked.Increment(ref _seq),
                    Kind: "notice",
                    SentAt: DateTimeOffset.UtcNow.ToString("O"),
                    OnlineClients: _hub.Count,
                    Payload: new PushSnapshot(_staticSystemInfo, CollectPerf(), CollectProcess("服务正在停止")));
                await _hub.BroadcastAsync(
                    JsonSerializer.Serialize(bye, PushJsonContext.Default.PushFrame)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 停机通知失败不应阻塞停机。
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
