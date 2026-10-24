using System.Text.Json.Serialization;

namespace NanoServer.Sample.Push;

/// <summary>一条服务端推送的数据帧。</summary>
/// <param name="Seq">自增序号，前端可据此发现丢帧。</param>
/// <param name="Kind">帧类型：snapshot / notice。</param>
/// <param name="SentAt">服务端发送时间（ISO 8601）。</param>
/// <param name="OnlineClients">发送时刻的在线连接数。</param>
/// <param name="Payload">完整快照。</param>
public sealed record PushFrame(
    long Seq,
    string Kind,
    string SentAt,
    int OnlineClients,
    PushSnapshot Payload);

/// <summary>
/// 推送快照：系统配置、运行时性能、进程占用三部分。
/// <para>
/// 注意：跨平台差异真实存在。macOS 上 <c>PrivateMemorySize64</c>、
/// <c>PeakWorkingSet64</c>、<c>HandleCount</c> 均恒为 0，框架会把这些字段置 null
/// 而不是推 0，避免前端展示误导性数据。
/// </para>
/// </summary>
public sealed record PushSnapshot(
    SystemInfo System,
    PerfInfo Perf,
    ProcessInfo Process);

/// <summary>系统配置：进程生命周期内基本不变，仅在首帧与配置变更时变化。</summary>
public sealed record SystemInfo(
    string MachineName,
    string UserName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string Framework,
    string RuntimeMode,
    int ProcessorCount,
    string ProcessName,
    int ProcessId,
    string StartTime,
    string CurrentDirectory,
    string Culture);

/// <summary>运行时性能：GC 与线程池等会持续变化的指标。</summary>
public sealed record PerfInfo(
    long GcHeapKb,
    long GcCommittedKb,
    long GcFragmentedKb,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int Gen0Delta,
    int Gen1Delta,
    int Gen2Delta,
    double GcPauseTotalMs,
    double GcPauseDeltaMs,
    long TotalAllocatedKb,
    int ThreadPoolWorkers,
    int ThreadPoolPending,
    long ThreadPoolCompleted,
    double ProcessCpuPercent,
    double UptimeSec);

/// <summary>进程占用：内存、句柄、CPU 时间、磁盘可用空间。</summary>
public sealed record ProcessInfo(
    long WorkingSetMb,
    long? PrivateMb,
    long? PeakWorkingSetMb,
    long VirtualMb,
    int ManagedThreads,
    int? OsThreads,
    int? HandleCount,
    double CpuUserMs,
    double CpuPrivilegedMs,
    double CpuTotalMs,
    long? DiskTotalGb,
    long? DiskFreeGb,
    int UpNetworkInterfaces,
    string Message);

/// <summary>推送服务状态（HTTP 查询）。</summary>
public sealed record PushStatus(
    bool Running,
    int IntervalMs,
    long SentTotal,
    long LastSeq,
    int OnlineClients,
    string? LastSentAt,
    string[] Clients);

/// <summary>推送相关 DTO 的 source-generated JSON 上下文（AOT 必需）。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PushFrame))]
[JsonSerializable(typeof(PushSnapshot))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(PerfInfo))]
[JsonSerializable(typeof(ProcessInfo))]
[JsonSerializable(typeof(PushStatus))]
public partial class PushJsonContext : JsonSerializerContext;
