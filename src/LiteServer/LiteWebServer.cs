using System.Net;
using System.Text;
using LiteServer.Http;
using LiteServer.Middleware;
using LiteServer.Routing;
using LiteServer.Security;
using LiteServer.WebSockets;
using LiteServer.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteServer;

/// <summary>
/// 轻量 Web 服务器。基于 <see cref="HttpListener"/>，不依赖 IIS / Kestrel。
/// <para>
/// 请求处理顺序：CORS 预检 → 中间件管线（按注册顺序）→ WebSocket 升级 → 路由匹配 → 处理器。
/// 任一中间件返回 false 即终结请求。
/// </para>
/// </summary>
public sealed class LiteWebServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RouteTable _routes = new();
    private readonly MiddlewarePipeline _pipeline = new();
    private readonly HashSet<string> _whiteList = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _acceptCts;
    private Task? _acceptTask;
    private volatile bool _stopping;

    /// <summary>构造函数。</summary>
    public LiteWebServer() => WebSockets = new WebSocketHub(new WebSocketOptions());

    /// <summary>WebSocket 连接管理。启用后与 HTTP 共用同一端口。</summary>
    public WebSocketHub WebSockets { get; }

    /// <summary>JWT 吊销名单。配合 <see cref="AuthMiddleware.Jwt"/> 使用。</summary>
    public TokenBlacklist Blacklist { get; } = new();

    /// <summary>免鉴权路径集合（仅对精确路径生效）。</summary>
    public IReadOnlySet<string> WhiteList => _whiteList;

    /// <summary>是否正在监听。</summary>
    public bool IsRunning => _listener.IsListening && !_stopping;

    /// <summary>实际监听前缀，如 http://127.0.0.1:8090/。未启动时为 null。</summary>
    public string? Prefix { get; private set; }

    /// <summary>已注册路由数。</summary>
    public int RouteCount => _routes.Count;

    /// <summary>是否启用 WebSocket。</summary>
    public bool EnableWebSocket { get; set; }

    /// <summary>宿主配置。由通用主机集成层写入，直接自建服务时可忽略。</summary>
    public Hosting.LiteServerOptions Options { get; } = new();

    /// <summary>
    /// 库内部日志器。默认 <see cref="NullLogger.Instance"/>（零开销）；
    /// <c>AddLiteServer</c> 会从容器注入真实实例。
    /// </summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>访问日志配置。</summary>
    public Logging.AccessLogOptions AccessLog { get; } = new();

    /// <summary>
    /// 依赖注入根容器。设置后每个请求会创建独立作用域，
    /// 处理器内可用 <c>request.GetService&lt;T&gt;()</c> 解析作用域服务。
    /// 未设置时处理器无从取服务，但服务器照常工作。
    /// </summary>
    public IServiceProvider? ServiceProvider { get; set; }

    // ---------------- 路由 ----------------

    /// <summary>注册路由。<paramref name="method"/> 为 null 表示接受任意方法。</summary>
    public LiteWebServer Map(string path, RequestHandler handler, string? method = null)
    {
        _routes.Add(path, handler, method);
        return this;
    }

    public LiteWebServer MapGet(string path, RequestHandler handler) => Map(path, handler, "GET");

    public LiteWebServer MapPost(string path, RequestHandler handler) => Map(path, handler, "POST");

    public LiteWebServer MapPut(string path, RequestHandler handler) => Map(path, handler, "PUT");

    public LiteWebServer MapDelete(string path, RequestHandler handler) => Map(path, handler, "DELETE");

    public LiteWebServer MapPatch(string path, RequestHandler handler) => Map(path, handler, "PATCH");

    /// <summary>清空全部路由。</summary>
    public LiteWebServer ClearRoutes()
    {
        _routes.Clear();
        return this;
    }

    /// <summary>列出已注册路由。</summary>
    public IReadOnlyList<RouteEntry> Routes => _routes.Entries();

    // ---------------- 中间件 ----------------

    /// <summary>添加中间件。返回 true 放行，false 终结请求。</summary>
    public LiteWebServer Use(MiddlewareDelegate middleware)
    {
        _pipeline.Use(middleware);
        return this;
    }

    /// <summary>托管静态文件。<paramref name="urlPrefix"/> 为对外前缀，<paramref name="rootFolder"/> 为本地目录。</summary>
    public LiteWebServer UseStaticFiles(string urlPrefix, string rootFolder, int maxAgeSeconds = 0)
        => UseStaticFiles(urlPrefix, rootFolder, maxAgeSeconds, NullLogger.Instance);

    /// <summary>托管静态文件，并指定该中间件使用的日志器。</summary>
    public LiteWebServer UseStaticFiles(string urlPrefix, string rootFolder, int maxAgeSeconds, ILogger logger)
    {
        var mw = new StaticFilesMiddleware(urlPrefix, rootFolder, maxAgeSeconds) { Logger = logger };
        return Use(mw.InvokeAsync);
    }

    /// <summary>把路径加入免鉴权白名单。</summary>
    public LiteWebServer AllowAnonymous(string path)
    {
        _whiteList.Add(RouteTable.NormalizePath(path));
        return this;
    }

    /// <summary>启用 JWT 鉴权。</summary>
    public LiteWebServer UseJwtAuth(string secret, TimeSpan? cleanupInterval = null, TimeSpan? clockSkew = null)
        => UseJwtAuth(secret, cleanupInterval, clockSkew, NullLogger.Instance);

    /// <summary>启用 JWT 鉴权，并指定该中间件使用的日志器。</summary>
    public LiteWebServer UseJwtAuth(string secret, TimeSpan? cleanupInterval, TimeSpan? clockSkew, ILogger logger)
    {
        Use(AuthMiddleware.Jwt(secret, _whiteList, Blacklist, clockSkew, logger));
        if (cleanupInterval is { } interval && interval > TimeSpan.Zero)
        {
            Blacklist.StartAutoCleanup(interval);
        }
        return this;
    }

    /// <summary>启用简单 Token 鉴权（比对 Authorization 头原值）。</summary>
    public LiteWebServer UseSimpleToken(string token)
    {
        Use(AuthMiddleware.SimpleToken(token, _whiteList));
        return this;
    }

    /// <summary>启用 Bearer Token 鉴权（会剥离 Bearer 前缀）。</summary>
    public LiteWebServer UseBearerToken(string token)
    {
        Use(AuthMiddleware.BearerToken(token, _whiteList));
        return this;
    }

    /// <summary>
    /// 启用 CORS。为 null 时回显请求的 Origin（仅允许携带凭证的跨域请求）。
    /// </summary>
    public LiteWebServer UseCors(string? allowedOrigin = null)
    {
        Use((request, response) => HandleCorsAsync(request, response, allowedOrigin));
        return this;
    }

    private Task<bool> HandleCorsAsync(HttpListenerRequest request, HttpListenerResponse response, string? allowedOrigin)
    {
        string? origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            // 非跨域请求：直接放行，不添加 CORS 头。
            return Task.FromResult(true);
        }

        // 安全默认：未显式配置允许来源时，只回显「同源」请求。
        // 早期实现是把任意 Origin 原样回显并附 Allow-Credentials: true，
        // 这等于允许任何站点带凭证读取响应（实测 evil.example / null 都被放行），属严重配置缺陷。
        bool hasExplicitAllowList = !string.IsNullOrEmpty(allowedOrigin);

        if (!hasExplicitAllowList)
        {
            // 同源判定：Origin 的 scheme+host+port 必须与本服务一致。
            string? self = request.Url is null ? null : $"{request.Url.Scheme}://{request.Url.Authority}";
            if (self is null || !string.Equals(origin, self, StringComparison.OrdinalIgnoreCase))
            {
                // 跨源且未授权：不写任何 CORS 头，让浏览器拦截。
                LiteLogger.CorsRejected(Logger, origin);
                return Task.FromResult(true);
            }
        }
        else if (!string.Equals(allowedOrigin, origin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        // "null" Origin 意味着 sandbox iframe / file:// / 跨源重定向，
        // 这类来源不可信，永不放行（否则可被用来做凭证读取）。
        if (string.Equals(origin, "null", StringComparison.Ordinal))
        {
            return Task.FromResult(true);
        }

        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Access-Control-Allow-Credentials"] = "true";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, PATCH, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Requested-With";
        response.Headers["Access-Control-Max-Age"] = "86400";

        // 预检请求直接 204 终结，不进入路由。
        if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 204;
            response.ContentLength64 = 0;
            response.Close();
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>启用请求限流。key 由调用方给出（通常取客户端 IP）。</summary>
    public LiteWebServer UseRateLimit(RateLimiter limiter, Func<HttpListenerRequest, string>? keySelector = null)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        keySelector ??= req => req.RemoteEndPoint?.Address.ToString() ?? "unknown";

        return Use(async (request, response) =>
        {
            if (limiter.TryAcquire(keySelector(request))) return true;

            await response.WriteErrorAsync(429, "Too Many Requests").ConfigureAwait(false);
            return false;
        });
    }

    /// <summary>
    /// 设置处理器异常的观察者（可选）。异常本身由 <see cref="HandleRequestAsync"/> 统一转 500，
    /// 这里只是给调用方一个记录日志的钩子。
    /// </summary>
    public LiteWebServer OnUnhandledException(Action<Exception, HttpListenerRequest> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _exceptionObserver = observer;
        return this;
    }

    private Action<Exception, HttpListenerRequest>? _exceptionObserver;

    // ---------------- 生命周期 ----------------

    /// <summary>启动服务（同步包装）。</summary>
    public void Start(int port = 8090) => StartAsync(null, port).GetAwaiter().GetResult();

    /// <summary>启动服务并监听指定主机。<paramref name="host"/> 传 "+" 表示所有网卡。</summary>
    public void Start(string host, int port = 8090) => StartAsync(host, port).GetAwaiter().GetResult();

    /// <summary>异步启动。</summary>
    public Task StartAsync(int port = 8090) => StartAsync(null, port);

    /// <summary>异步启动并监听指定主机。</summary>
    public async Task StartAsync(string? host, int port = 8090)
    {
        if (IsRunning) throw new InvalidOperationException("服务已在运行。");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        string hostPart = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        Prefix = $"http://{hostPart}:{port}/";

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();

        _stopping = false;
        _acceptCts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_acceptCts.Token), CancellationToken.None);

        bool isAot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;
        LiteLogger.ServerStarted(Logger, Prefix, isAot ? "NativeAOT" : "JIT");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>停止服务。</summary>
    public async Task StopAsync()
    {
        if (!_listener.IsListening && _acceptTask is null) return;

        _stopping = true;

        _acceptCts?.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            _acceptTask = null;
        }

        _acceptCts?.Dispose();
        _acceptCts = null;

        await WebSockets.CloseAllAsync("Server stopped").ConfigureAwait(false);
        Blacklist.StopAutoCleanup();

        LiteLogger.ServerStopped(Logger, Prefix ?? "(unknown)");
    }

    /// <summary>同步停止。</summary>
    public void Stop() => StopAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 接受连接的循环。
    /// <para>
    /// 异常后检查 <c>IsListening</c>：若侦听器已停则退出，否则短暂退避防止空转打满 CPU。
    /// </para>
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_listener.IsListening) break;
                LiteLogger.AcceptLoopRetry(Logger, ex.Message);
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // 每个请求独立处理，互不阻塞；异常在 HandleRequestAsync 内部兜住。
            _ = HandleRequestAsync(context);
        }
    }

    /// <summary>处理单个请求：中间件 → WebSocket → 路由 → 兜底。</summary>
    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        // 每请求一个 DI 作用域：作用域服务（如 DbContext）在请求结束时释放。
        // AsyncLocal 按逻辑调用链传播，同请求内多层 await 共享，跨请求隔离。
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        IDisposable? scope = null;
        IServiceProvider? previous = RequestScope.Current;

        try
        {
            if (ServiceProvider is not null)
            {
                IServiceScope requestScope = ServiceProvider.CreateScope();
                scope = requestScope;
                RequestScope.Current = requestScope.ServiceProvider;
            }
            else
            {
                RequestScope.Current = null;
            }

            if (!await _pipeline.InvokeAsync(request, response).ConfigureAwait(false))
            {
                // 中间件已终结请求（可能已写响应）。若还没写过任何东西，兜底关闭。
                TryAbort(response);
                return;
            }

            if (request.IsWebSocketRequest)
            {
                if (!EnableWebSocket)
                {
                    response.StatusCode = 400;
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }
                await WebSockets.AcceptAsync(context).ConfigureAwait(false);
                return;
            }

            RouteMatch match = _routes.Match(request.Url?.AbsolutePath ?? "/", request.HttpMethod ?? "GET");

            if (match.IsMatched)
            {
                await match.Entry!.Handler(request, response).ConfigureAwait(false);
                return;
            }

            if (match.Kind == RouteMatchKind.MethodNotAllowed)
            {
                string allow = string.Join(", ", match.AllowedMethods);
                response.Headers["Allow"] = allow;
                LiteLogger.MethodNotAllowed(Logger, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/", allow);
                await response.WriteAsync(
                    $"Method Not Allowed. Allowed: {allow}",
                    ContentTypes.Plain,
                    405).ConfigureAwait(false);
                return;
            }

            LiteLogger.RouteNotFound(Logger, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/");
            await response.WriteAsync("Not Found", ContentTypes.Plain, 404).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LiteLogger.UnhandledException(Logger, ex, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/");

            if (_exceptionObserver is not null)
            {
                try { _exceptionObserver(ex, request); }
                catch (Exception observeEx) when (observeEx is not OutOfMemoryException) { /* 观察者自身出错不应影响响应 */ }
            }
            await WriteServerErrorAsync(response, ex).ConfigureAwait(false);
        }
        finally
        {
            RequestScope.Current = previous;
            scope?.Dispose();

            WriteAccessLog(request, response, startedAt);
        }
    }

    /// <summary>
    /// 写访问日志。放在 finally 中，确保异常路径也有记录。
    /// 状态码在 <c>response.Close()</c> 之后仍可读取（已实测验证），故此处拿到的是终值。
    /// </summary>
    private void WriteAccessLog(HttpListenerRequest request, HttpListenerResponse response, long startedAt)
    {
        if (!AccessLog.Enabled) return;

        string path = request.Url?.AbsolutePath ?? "/";
        if (AccessLog.ExcludedPaths.Contains(path)) return;

        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (AccessLog.SlowRequestThreshold > TimeSpan.Zero
            && elapsedMs < AccessLog.SlowRequestThreshold.TotalMilliseconds)
        {
            return;
        }

        // 采样：高 QPS 下按比例丢弃访问日志，降低同步写盘开销。
        if (!AccessLog.ShouldSample()) return;

        // 默认不记 query string：避免 ?token= 等敏感参数落进日志。
        if (AccessLog.IncludeQueryString && !string.IsNullOrEmpty(request.Url?.Query))
        {
            path += request.Url.Query;
        }

        int status = 0;
        try { status = response.StatusCode; }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }

        LiteLogger.RequestCompleted(Logger, request.HttpMethod ?? "-", path, status, Math.Round(elapsedMs, 2));
    }

    /// <summary>写 500。若响应已开始写出则只能放弃连接。</summary>
    private static async Task WriteServerErrorAsync(HttpListenerResponse response, Exception ex)
    {
        try
        {
            response.StatusCode = 500;
            byte[] body = Encoding.UTF8.GetBytes("Internal Server Error");
            response.ContentType = ContentTypes.Plain;
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            response.Close();
        }
        catch (Exception inner) when (inner is IOException or ObjectDisposedException or HttpListenerException or InvalidOperationException)
        {
            TryAbort(response);
        }
        _ = ex;
    }

    private static void TryAbort(HttpListenerResponse response)
    {
        try { response.Abort(); }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException) { }
    }

    /// <summary>枚举本机可用 IPv4 地址（调试时提示访问入口）。</summary>
    public static List<string> GetLocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        result.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // 某些受限环境拿不到网卡信息，返回已知部分即可。
        }
        return result;
    }

    /// <summary>Unix 秒时间戳。<paramref name="addSeconds"/> 用于便捷地算未来时间。</summary>
    public static long UnixSeconds(long addSeconds = 0)
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + addSeconds;

    /// <summary>Unix 毫秒时间戳。</summary>
    public static long UnixMilliseconds(long addMilliseconds = 0)
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + addMilliseconds;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await WebSockets.DisposeAsync().ConfigureAwait(false);
        Blacklist.Dispose();
        _listener.Close();
    }
}
