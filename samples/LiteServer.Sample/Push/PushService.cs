using System.Diagnostics;
using System.Text.Json;
using LiteServer.Logging;
using LiteServer.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteServer.Sample.Push;

/// <summary>
/// 定时向所有 WebSocket 客户端推送运行时指标的常驻服务。
/// <para>
/// 设计要点：
/// ① 继承 <see cref="BackgroundService"/>，随通用主机启动与停止；
/// ② 无在线客户端时**跳过组帧与序列化**，只做一次 Count 判断 —— 空闲时几乎零开销；
/// ③ 单轮推送异常不得杀死整个循环，否则一次网络抖动就会永久停止推送；
/// ④ 推送间隔运行时可调（<see cref="SetInterval"/>），便于演示与压测。
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

    private int _intervalMs = DefaultIntervalMs;
    private long _seq;
    private long _sentTotal;
    private string? _lastSentAt;
    private int _lastGen0, _lastGen1, _lastGen2;
    private volatile bool _running;

    public PushService(WebSocketHub hub, ILogger<PushService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>当前推送间隔（毫秒）。</summary>
    public int IntervalMs => Volatile.Read(ref _intervalMs);

    /// <summary>调整推送间隔，返回实际生效值（已做上下限收敛）。</summary>
    public int SetInterval(int ms)
    {
        int clamped = Math.Clamp(ms, MinIntervalMs, MaxIntervalMs);
        Volatile.Write(ref _intervalMs, clamped);
        return clamped;
    }

    /// <summary>累计发送帧数。</summary>
    public long SentTotal => Interlocked.Read(ref _sentTotal);

    /// <summary>最近一帧的序号。</summary>
    public long LastSeq => Interlocked.Read(ref _seq);

    /// <summary>最近发送时间。</summary>
    public string? LastSentAt => Volatile.Read(ref _lastSentAt);

    /// <summary>取当前状态快照，供 HTTP 查询。</summary>
    public PushStatus Snapshot() => new(
        Running: _running,
        IntervalMs: IntervalMs,
        SentTotal: SentTotal,
        LastSeq: LastSeq,
        OnlineClients: _hub.Count,
        LastSentAt: LastSentAt,
        Clients: _hub.OnlineClients());

    /// <summary>
    /// 立即推送一帧（不等下一个周期）。用于前端「手动触发一次」。
    /// 返回是否真的发给了至少一个客户端。
    /// </summary>
    public async Task<bool> PushOnceAsync(string message = "手动触发")
    {
        if (_hub.Count == 0) return false;
        await PushFrameAsync(message).ConfigureAwait(false);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _running = true;
        LiteLogger.ServerStarted(_logger, $"推送服务（间隔 {DefaultIntervalMs}ms）", "BackgroundService");

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

            // 无在线连接：跳过组帧与序列化，几乎零成本。
            if (_hub.Count == 0) continue;

            try
            {
                await PushFrameAsync("定时推送").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单轮失败不能让推送循环退出。记录后继续下一轮。
                LiteLogger.UnhandledException(_logger, ex, "PUSH", "/ws");
            }
        }

        _running = false;
        LiteLogger.ServerStopped(_logger, "推送服务");
    }

    /// <summary>组帧、序列化、广播。</summary>
    private async Task PushFrameAsync(string message)
    {
        long seq = Interlocked.Increment(ref _seq);

        GC.Collect(0, GCCollectionMode.Optimized, blocking: false, compacting: false);

        int g0 = GC.CollectionCount(0);
        int g1 = GC.CollectionCount(1);
        int g2 = GC.CollectionCount(2);

        var payload = new PushPayload(
            ManagedThreads: ThreadPool.ThreadCount,
            WorkingSetMB: Environment.WorkingSet / (1024 * 1024),
            GcTotalMemoryMB: GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
            GcGen0: g0 - _lastGen0,
            GcGen1: g1 - _lastGen1,
            GcGen2: g2 - _lastGen2,
            UptimeSec: Math.Round(_uptime.Elapsed.TotalSeconds, 1),
            Message: message);

        _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2;

        var frame = new PushFrame(
            Seq: seq,
            Kind: "metrics",
            SentAt: DateTimeOffset.UtcNow.ToString("O"),
            OnlineClients: _hub.Count,
            Payload: payload);

        string json = JsonSerializer.Serialize(frame, PushJsonContext.Default.PushFrame);

        await _hub.BroadcastAsync(json).ConfigureAwait(false);

        Interlocked.Increment(ref _sentTotal);
        Volatile.Write(ref _lastSentAt, frame.SentAt);
    }

    /// <summary>停机时通知客户端，便于前端展示「服务已停」而不是静默断线。</summary>
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
                    Payload: new PushPayload(0, 0, 0, 0, 0, 0, Math.Round(_uptime.Elapsed.TotalSeconds, 1), "服务正在停止"));
                await _hub.BroadcastAsync(
                    JsonSerializer.Serialize(bye, PushJsonContext.Default.PushFrame)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 停机通知失败不应阻塞停机流程。
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
