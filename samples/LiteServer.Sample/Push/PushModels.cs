using System.Text.Json.Serialization;

namespace LiteServer.Sample.Push;

/// <summary>一条服务端推送的数据帧。</summary>
/// <param name="Seq">自增序号，前端可据此发现丢帧。</param>
/// <param name="Kind">帧类型：metrics / notice。</param>
/// <param name="SentAt">服务端发送时间（ISO 8601）。</param>
/// <param name="OnlineClients">发送时刻的在线连接数。</param>
/// <param name="Payload">业务负载，随 Kind 变化。</param>
public sealed record PushFrame(
    long Seq,
    string Kind,
    string SentAt,
    int OnlineClients,
    PushPayload Payload);

/// <summary>推送负载：把几个会变化的运行时指标打包，便于前端绘图。</summary>
public sealed record PushPayload(
    int ManagedThreads,
    long WorkingSetMB,
    long GcTotalMemoryMB,
    int GcGen0,
    int GcGen1,
    int GcGen2,
    double UptimeSec,
    string Message);

/// <summary>推送服务当前状态（供 HTTP 查询）。</summary>
public sealed record PushStatus(
    bool Running,
    int IntervalMs,
    long SentTotal,
    long LastSeq,
    int OnlineClients,
    string? LastSentAt,
    string[] Clients);

/// <summary>推送服务的 source-generated JSON 上下文（AOT 必需）。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PushFrame))]
[JsonSerializable(typeof(PushPayload))]
[JsonSerializable(typeof(PushStatus))]
public partial class PushJsonContext : JsonSerializerContext;
