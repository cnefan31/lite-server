using Microsoft.Extensions.Logging;

namespace NanoServer.Logging;

/// <summary>
/// 库内部日志的轻量封装。
/// <para>
/// 设计取舍：
/// ① 依赖 <see cref="ILogger"/> 抽象而非具体实现 —— 使用方可接 Console / Serilog / 任意 provider；
///    未接入时降级为「不记录」，绝不因日志缺失影响请求处理；
/// ② 全程不使用反射，NativeAOT 下零 IL 警告；
/// ③ 用 <see cref="LoggerMessageAttribute"/> 源生成，避免装箱与字符串拼接开销
///    —— 访问日志是每请求一条，这点开销实打实。
/// </para>
/// </summary>
public static partial class NanoLogger
{
    // ---------------- 生命周期 ----------------

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "NanoServer 启动：{Prefix}（模式 {Mode}）")]
    public static partial void ServerStarted(ILogger logger, string prefix, string mode);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "NanoServer 停止：{Prefix}")]
    public static partial void ServerStopped(ILogger logger, string prefix);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "监听中断，10ms 后重试：{Reason}")]
    public static partial void AcceptLoopRetry(ILogger logger, string reason);

    // ---------------- 请求 ----------------

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "{Method} {Path} {Status} {ElapsedMs}ms")]
    public static partial void RequestCompleted(
        ILogger logger, string method, string path, int status, double elapsedMs);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
        Message = "路由未命中：{Method} {Path}")]
    public static partial void RouteNotFound(ILogger logger, string method, string path);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "方法不允许：{Method} {Path}（允许 {Allowed}）")]
    public static partial void MethodNotAllowed(ILogger logger, string method, string path, string allowed);

    // ---------------- 异常 ----------------

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
        Message = "请求处理异常：{Method} {Path}")]
    public static partial void UnhandledException(ILogger logger, Exception ex, string method, string path);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug,
        Message = "响应已开始写出，无法再写错误页：{Path}")]
    public static partial void CannotWriteErrorPage(ILogger logger, Exception ex, string path);

    // ---------------- 鉴权 ----------------

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "鉴权失败：{Method} {Path}（{Reason}）")]
    public static partial void AuthRejected(ILogger logger, string method, string path, string reason);

    // ---------------- 静态文件 ----------------

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "拒绝 dotfile 访问：{Path}")]
    public static partial void DotFileRejected(ILogger logger, string path);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "拒绝越权路径：{Path}")]
    public static partial void PathOutsideRootRejected(ILogger logger, string path);

    // ---------------- WebSocket ----------------

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information,
        Message = "WebSocket 连接建立：{ClientId}（在线 {Online}）")]
    public static partial void WebSocketConnected(ILogger logger, string clientId, int online);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information,
        Message = "WebSocket 连接关闭：{ClientId}（在线 {Online}）")]
    public static partial void WebSocketDisconnected(ILogger logger, string clientId, int online);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning,
        Message = "WebSocket 连接数超限（上限 {Max}），拒绝握手")]
    public static partial void WebSocketRejected(ILogger logger, int max);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning,
        Message = "WebSocket 消息过大：{ClientId}，已断开")]
    public static partial void WebSocketMessageTooLarge(ILogger logger, string clientId);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Debug,
        Message = "WebSocket 心跳超时：{ClientId}，已断开")]
    public static partial void WebSocketHeartbeatTimeout(ILogger logger, string clientId);

    // ---------------- 安全 ----------------

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning,
        Message = "SSRF 拦截：{Url}（{Verdict}）")]
    public static partial void SsrfBlocked(ILogger logger, string url, string verdict);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "限流触发：{Key}")]
    public static partial void RateLimited(ILogger logger, string key);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "CORS 拒绝未授权来源：{Origin}")]
    public static partial void CorsRejected(ILogger logger, string origin);
}

/// <summary>日志事件 ID 分段常量，便于使用方按需过滤。</summary>
public static class NanoLogEvents
{
    public const int ServerLifecycle = 1000;
    public const int RequestPipeline = 2000;
    public const int Exception = 3000;
    public const int Authentication = 4000;
    public const int StaticFiles = 5000;
    public const int WebSocket = 6000;
    public const int Security = 7000;
}

/// <summary>访问日志选项。</summary>
public sealed class AccessLogOptions
{
    /// <summary>是否启用访问日志。默认启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 记录路径时是否包含查询串。默认不含——避免 token 等敏感参数落日志。
    /// </summary>
    public bool IncludeQueryString { get; set; }

    /// <summary>只记录慢于该阈值的请求（<see cref="TimeSpan.Zero"/> 表示全部记录）。</summary>
    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.Zero;

    /// <summary>不记访问日志的路径。默认排除健康检查，避免探活/压测刷满日志。</summary>
    public HashSet<string> ExcludedPaths { get; } = new(StringComparer.OrdinalIgnoreCase) { "/api/health" };

    /// <summary>
    /// 采样率（1 = 全部记录，10 = 每 10 条记 1 条）。
    /// <para>
    /// 压测实测：同步写文件时，全量访问日志会带来约 24% 的吞吐损失
    /// （24.8k → 32.5k req/s）。高 QPS 场景建议设为 10–100，
    /// 或改用 <see cref="SlowRequestThreshold"/> 只记慢请求。
    /// </para>
    /// </summary>
    public int SampleEvery { get; set; } = 1;

    private long _counter;
    private readonly object _counterLock = new();

    /// <summary>按采样率判断本条是否应记录。</summary>
    internal bool ShouldSample()
    {
        if (SampleEvery <= 1) return true;

        lock (_counterLock)
        {
            _counter++;
            return _counter % SampleEvery == 0;
        }
    }
}
