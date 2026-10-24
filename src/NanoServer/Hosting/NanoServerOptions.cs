namespace NanoServer.Hosting;

/// <summary>通用的 NanoServer 宿主配置。</summary>
public sealed class NanoServerOptions
{
    /// <summary>监听地址。null 或空串等价于 127.0.0.1；"+" 表示所有网卡。</summary>
    public string? Host { get; set; } = "127.0.0.1";

    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 8090;

    /// <summary>启用 WebSocket（与 HTTP 共用端口）。</summary>
    public bool EnableWebSocket { get; set; }

    /// <summary>WebSocket 最大并发连接数。</summary>
    public int WebSocketMaxConnections { get; set; } = 1000;

    /// <summary>启用 WebSocket 心跳超时检测。</summary>
    public bool EnableWebSocketHeartbeat { get; set; }

    /// <summary>心跳超时时间。</summary>
    public TimeSpan WebSocketHeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>JWT 密钥。设置后自动启用 JWT 鉴权。</summary>
    public string? JwtSecret { get; set; }

    /// <summary>JWT 允许的时钟偏移。</summary>
    public TimeSpan JwtClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>黑名单自动清理间隔。为 null 时不启动后台清理。</summary>
    public TimeSpan? BlacklistCleanupInterval { get; set; }

    /// <summary>静态文件：URL 前缀。</summary>
    public string? StaticFilePrefix { get; set; }

    /// <summary>静态文件：本地目录。设置 <see cref="StaticFilePrefix"/> 后生效。</summary>
    public string? StaticFileRoot { get; set; }

    /// <summary>静态文件强缓存秒数。</summary>
    public int StaticFileMaxAgeSeconds { get; set; }

    /// <summary>
    /// 静态文件中间件是否排在鉴权之前。
    /// <para>
    /// false（默认，安全优先）：先鉴权再查静态文件。受保护站点不会被匿名拉走任何资源，
    /// 但 dotfile 探测会先得到 401 而不是 403——因为请求还没走到静态层。
    /// </para>
    /// <para>
    /// true（公开静态站点）：先查静态文件再鉴权。dotfile 由静态中间件直接拒绝（403），
    /// 静态资源匿名可取。仅在静态内容本就公开时使用，并把对外路径加入 <see cref="AnonymousPaths"/>。
    /// </para>
    /// </summary>
    public bool StaticFilesBeforeAuth { get; set; }

    /// <summary>允许的跨域来源。null 表示回显请求 Origin。</summary>
    public string? CorsAllowedOrigin { get; set; }

    /// <summary>是否启用 CORS。</summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>免鉴权路径（精确匹配）。</summary>
    public List<string> AnonymousPaths { get; } = new();
}
