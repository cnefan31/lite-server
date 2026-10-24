using System.Text.Json.Serialization;

namespace LiteServer.Sample.Jobs;

/// <summary>任务状态。</summary>
public enum JobState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Canceled = 4,
}

/// <summary>一个后台任务的可观测记录。</summary>
public sealed record JobRecord
{
    public required string Id { get; init; }

    /// <summary>任务名，便于区分。</summary>
    public required string Name { get; init; }

    public JobState State { get; set; }

    /// <summary>0-100。</summary>
    public int Progress { get; set; }

    /// <summary>当前步骤说明。</summary>
    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>失败原因。</summary>
    public string? Error { get; set; }

    /// <summary>执行耗时毫秒（结束后填充）。</summary>
    public long? DurationMs { get; set; }

    /// <summary>创建该任务的请求所解析到的作用域标识，用于证明每请求作用域隔离。</summary>
    public string? CreatedInScope { get; init; }
}

/// <summary>创建任务的请求体。</summary>
public sealed record CreateJobRequest(string? Name, int? Steps, int? StepDelayMs);

/// <summary>创建任务的响应。</summary>
public sealed record CreateJobResponse(string Id, string State, string Location);

/// <summary>任务列表响应。</summary>
public sealed record JobListResponse(int Total, JobRecord[] Items);

/// <summary>依赖注入探测结果。</summary>
public sealed record DiProbeResponse(
    string ScopeId,
    string SingletonId,
    string ScopedId,
    string TransientId,
    int SingletonResolveCount,
    bool ScopedIsFreshPerRequest,
    bool SingletonIsShared,
    bool TransientIsAlwaysNew,
    string[] Notes);

/// <summary>日志配置自检响应。</summary>
public sealed record LogInfoResponse(
    bool Enabled,
    bool IncludeQueryString,
    double SlowThresholdMs,
    string[] ExcludedPaths,
    string LoggerType,
    Dictionary<string, int> EventRanges);

/// <summary>
/// source-generated JSON 上下文。
/// <para>
/// 关键：NativeAOT 下反射式序列化不可用，必须用源生成器。
/// 这是本项目在 AOT 场景的正确做法示范。
/// </para>
/// </summary>
// PropertyNamingPolicy = CamelCase：出入参都用 camelCase，与前端/curl 习惯一致。
// 注意默认（不设该策略）时字段名严格区分大小写，{\"steps\":..} 会静默匹配不上 {"Steps\":..}。
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(JobRecord[]))]
[JsonSerializable(typeof(JobListResponse))]
[JsonSerializable(typeof(CreateJobRequest))]
[JsonSerializable(typeof(CreateJobResponse))]
[JsonSerializable(typeof(DiProbeResponse))]
[JsonSerializable(typeof(LogInfoResponse))]
public partial class JobJsonContext : JsonSerializerContext;
