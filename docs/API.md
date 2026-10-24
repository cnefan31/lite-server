# NanoServer API 手册

按功能分组列出全部公开 API。签名与默认值均取自源码，`→` 表示返回值用于链式调用。

命名空间速查：

| 命名空间 | 内容 |
|---|---|
| `NanoServer` | `NanoWebServer`、`RequestScope` 及其扩展 |
| `NanoServer.Http` | 请求/响应/文件/Cookie 扩展 |
| `NanoServer.Routing` | `RequestHandler`、`RouteTable`、`RouteEntry` |
| `NanoServer.Middleware` | `MiddlewareDelegate`、管线、静态文件、鉴权 |
| `NanoServer.Security` | `Jwt`、`SsrfGuard`、`TokenBlacklist`、`RateLimiter` |
| `NanoServer.WebSockets` | `WebSocketHub`、`WebSocketOptions` |
| `NanoServer.Logging` | `NanoLogger`、`AccessLogOptions`、`NanoLogEvents` |
| `NanoServer.Hosting` | `NanoServerOptions`、`NanoServerHost`、`AddNanoServer` |

---

## 一、创建与启动

### 两种模式

```csharp
// 方式一：通用主机（推荐生产）
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNanoServer(o => { o.Port = 8090; }, server => { /* 注册路由 */ });
await builder.Build().RunAsync();

// 方式二：独立模式（无需 DI/宿主）
await using var server = new NanoWebServer();
server.MapGet("/", (req, res) => res.WriteAsync("ok"));
await server.StartAsync("127.0.0.1", 8090);
await Task.Delay(Timeout.Infinite);
```

### 启动 / 停止

| 方法 | 说明 |
|---|---|
| `Start(int port = 8090)` | 同步启动，监听 127.0.0.1 |
| `Start(string host, int port = 8090)` | 同步启动，指定主机 |
| `StartAsync(int port = 8090)` | 异步启动 |
| `StartAsync(string? host, int port = 8090)` | 异步启动，指定主机 |
| `StopAsync()` | 异步停止：取消 accept、关闭监听、断开所有 WS |
| `Stop()` | 同步停止 |
| `DisposeAsync()` | 释放资源。`NanoWebServer` 实现 `IAsyncDisposable`，建议 `await using` |

> 独立模式推荐 `await using var server = new NanoWebServer();` —— 作用域结束自动
> `DisposeAsync`，确保监听器、WebSocket 连接与黑名单清理任务全部释放。

### 状态属性

| 属性 | 类型 | 说明 |
|---|---|---|
| `IsRunning` | `bool` | 是否正在监听 |
| `Prefix` | `string?` | 实际监听前缀，如 `http://127.0.0.1:8090/` |
| `RouteCount` | `int` | 已注册路由数 |
| `Routes` | `IReadOnlyList<RouteEntry>` | 全部路由（调试用） |
| `WhiteList` | `IReadOnlySet<string>` | 当前免鉴权路径 |

### 静态工具

| 方法 | 说明 |
|---|---|
| `GetLocalAddresses()` | 枚举本机可用 IPv4 地址 |
| `UnixSeconds(long add = 0)` | Unix 秒时间戳（可加偏移） |
| `UnixMilliseconds(long add = 0)` | Unix 毫秒时间戳 |

---

## 二、路由注册

| 方法 | 说明 |
|---|---|
| `Map(path, handler, method = null)` | 通用注册；method 为 null 接受任意方法 |
| `MapGet / MapPost / MapPut / MapDelete / MapPatch` | 语义化快捷方法 |
| `ClearRoutes()` | 清空全部路由 |

处理器签名：`delegate Task RequestHandler(HttpListenerRequest request, HttpListenerResponse response)`

```csharp
server.MapGet("/api/health", (req, res) => res.WriteAsync("ok"));
server.MapPost("/api/tasks", CreateTask);
server.Map("/api/any", handler);            // 接受任意方法
server.MapGet("/api/items/*/detail", h);    // 单段通配
```

**匹配规则**

- 同一路径可注册多个方法，互不覆盖。
- 同 `(路径, 方法)` 重复注册 → 后注册覆盖前者。
- 精准路径优先于通配路径；通配按段数从多到少匹配。
- 路径命中但方法不符 → 自动 405 并带 `Allow` 头。

---

## 三、中间件

| 方法 | 说明 |
|---|---|
| `Use(MiddlewareDelegate)` | 添加中间件，按注册顺序执行 |
| `UseStaticFiles(urlPrefix, rootFolder, maxAgeSeconds)` | 托管静态文件 |
| `UseStaticFiles(urlPrefix, rootFolder, maxAgeSeconds, logger)` | 同上，指定日志器 |
| `UseCors(string? allowedOrigin = null)` | CORS，建议最先注册 |
| `UseJwtAuth(secret, cleanupInterval, clockSkew)` | JWT 鉴权 |
| `UseBearerToken(token)` | Bearer 鉴权（剥离前缀后比对） |
| `UseSimpleToken(token)` | 简单 Token（比对原始头值） |
| `UseRateLimit(limiter, keySelector)` | 限流 |
| `AllowAnonymous(path)` | 加入免鉴权白名单 |

中间件签名：`delegate Task<bool> MiddlewareDelegate(HttpListenerRequest, HttpListenerResponse)`
返回 `true` 放行，`false` 表示已自行终结请求。

```csharp
server.UseCors();                                   // 最先
server.Use(async (req, res) => {
    Console.WriteLine($"{req.HttpMethod} {req.Url?.AbsolutePath}");
    return true;
});
server.UseJwtAuth(secret, TimeSpan.FromMinutes(5));
server.UseStaticFiles("/", "wwwroot", maxAgeSeconds: 3600);
```

> **顺序即语义**：中间件按注册顺序执行。鉴权放在静态文件之前则静态资源受保护，
> 反之则匿名可取——见 [CONFIGURATION.md](CONFIGURATION.md#四静态文件)。

---

## 四、请求读取（`NanoServer.Http`）

### 查询参数

| 方法 | 返回 | 说明 |
|---|---|---|
| `GetQuery(key)` | `string?` | 原始值，缺失返回 null；大小写不敏感 |
| `GetQuery<T>(key)` | `T?` | 强类型转换，失败返回 `default` |
| `ParseQuery()` | `Dictionary<string,string>` | 全部查询参数 |

`GetQuery<T>` 支持：`string`/`int`/`long`/`double`/`decimal`/`bool`/`DateTime`/`Guid`/枚举。
`bool` 额外接受 `1/0/true/false/yes/on`。

```csharp
int page = req.GetQuery<int>("page");            // 缺失或非法 -> 0
bool vip  = req.GetQuery<bool>("vip");           // "1"/"yes"/"on" 都为 true
```

### 请求体

| 方法 | 说明 |
|---|---|
| `ReadBodyAsString()` | 同步读取为 UTF-8 文本 |
| `ReadBodyAsStringAsync(ct)` | 异步读取 |
| `ParseForm()` | 解析 `application/x-www-form-urlencoded`，非表单返回空字典 |

### Token 与 Cookie

| 方法 | 说明 |
|---|---|
| `GetToken()` | 读 `Authorization`，**剥离** `Bearer `/`Basic ` 前缀 |
| `ExtractToken()` | 按优先级取：头 → `?token=` → `lite_token` cookie |
| `TryGetCookie(key, out value)` | 读 Cookie，存在返回 true |

> 处理器内取 token 请用 `ExtractToken()`：浏览器 `EventSource`/`WebSocket` 无法设请求头，
> 只认 `GetToken()` 会让这两条通道永远 401。

```csharp
string? token = req.ExtractToken();
if (req.TryGetCookie("sid", out string? sid)) { /* ... */ }
```

---

## 五、响应写出

| 方法 | 说明 |
|---|---|
| `WriteAsync(content, contentType, statusCode, ct)` | 写文本并**自动关闭**响应 |
| `WriteJsonAsync(json, statusCode, ct)` | 写 JSON（调用方负责序列化） |
| `WriteErrorAsync(statusCode, message, ct)` | 写纯文本错误 |
| `WriteNoContent()` | 204 并关闭 |
| `SetHeader(name, value)` | 设置响应头 |

### 分块推送（长连接 / SSE）

| 方法 | 说明 |
|---|---|
| `WriteTextAsync(text, ct)` | 写一段文本，自动开 chunked |
| `WriteBytesAsync(data, ct)` | 写原始字节 |
| `WriteStreamAsync(source, bufferSize, ct)` | 转发流，内存恒定 |

> 这些方法在客户端断开时**静默返回**（不抛异常），调用方只需在 `finally` 中关闭响应。

```csharp
res.ContentType = ContentTypes.EventStream;
res.Headers["Cache-Control"] = "no-cache";
res.SendChunked = true;
try {
    for (int i = 0; i < 5; i++) {
        await res.WriteTextAsync($"data: {i}\n\n");
        await Task.Delay(1000);
    }
} finally { res.Close(); }
```

---

## 六、文件传输

| 方法 | 说明 |
|---|---|
| `SendFileAsync(path, asAttachment, request, ct)` | 智能发送，传入 request 支持 Range/206/416 |
| `SendStreamAsync(stream, contentType, isLive, ct)` | 发送任意流；isLive 启用 chunked 不缓存 |
| `SaveFileAsync(savePath, onProgress, ct)` | 流式接收上传，返回 `bool` |

```csharp
// 下载（支持拖动进度条）
server.MapGet("/download", (req, res) =>
    res.SendFileAsync(path, asAttachment: false, request: req));

// 上传
server.MapPost("/upload", async (req, res) => {
    bool ok = await req.SaveFileAsync(path, (cur, total) => Console.WriteLine($"{cur}/{total}"));
    if (ok) await res.WriteJsonAsync("{\"ok\":true}");
    else await res.WriteErrorAsync(500, "failed");
});
```

---

## 七、Cookie

| 方法 | 说明 |
|---|---|
| `AppendCookie(key, value, options)` | 下发 Cookie（支持多条） |
| `DeleteCookie(key, options)` | 下发过期 Cookie 删除 |
| `ClearCookies()` | 清除本次响应所有 Set-Cookie |

`CookieOptions` 属性：`Domain`、`Path`（默认 `/`）、`Expires`、`HttpOnly`、`Secure`、`SameSite`（默认 `Lax`）。

```csharp
res.AppendCookie("sid", id, new CookieExtensions.CookieOptions {
    Path = "/", HttpOnly = true, Secure = true,
    SameSite = CookieExtensions.SameSiteMode.Strict,
    Expires = DateTimeOffset.UtcNow.AddHours(1),
});
```

---

## 八、JWT

### 签发与校验（静态类 `Jwt`）

| 方法 | 说明 |
|---|---|
| `Generate(payloadJson, secret)` | 生成 HS256 token |
| `Verify(token, secret, clockSkew)` | 完整校验：结构 + 签名 + exp；用定长比较防时序侧信道 |
| `DecodePayload(token)` | 解码 payload 为 JSON 文本 |
| `ReadLongClaim(payloadJson, claim)` | 读整数型 claim（exp/iat） |
| `ReadStringClaim(payloadJson, claim)` | 读字符串型 claim（sub/role） |

```csharp
long exp = NanoWebServer.UnixSeconds(3600);
string token = Jwt.Generate($"{{\"sub\":\"u1\",\"exp\":{exp}}}", secret);
```

### 吊销名单 `TokenBlacklist`

| 方法 | 说明 |
|---|---|
| `Add(tokenId, expireAtUnixSeconds)` | 加入黑名单（**绝对时间戳，非 TTL**） |
| `Contains(tokenId)` | 是否在黑名单（顺带清理过期项） |
| `Remove(tokenId)` | 移出 |
| `CleanExpired()` | 清理全部过期项，返回数量 |
| `StartAutoCleanup(interval, maxEntries)` | 启动后台定时清理 |
| `StopAutoCleanup()` | 停止 |

> 黑名单只做**声明级**吊销，不校验签名。因此**必须先 `Jwt.Verify` 再查黑名单**，
> 否则伪造 token 也能走通吊销检查。内置 `UseJwtAuth` 已按此顺序实现。

---

## 九、SSRF 防护

| 方法 | 说明 |
|---|---|
| `IsSafe(url, out verdict, allowPrivateNetworks)` | 校验 URL，`verdict` 说明原因 |
| `IsSafe(url, out verdict)` | 同上，不返回解析后的 IP |
| `IsSafe(url, out verdict, out IPAddress[] addrs, allowPrivate)` | 同时返回解析结果 |

`Verdict` 枚举：`Allowed`、`BadScheme`、`Loopback`、`Private`、`LinkLocal`、`Reserved`、`Unresolvable`。

---

## 十、限流

`new RateLimiter(capacity = 100, window = 1 分钟)`，方法 `TryAcquire(key)` 返回 `bool`。

`server.UseRateLimit(limiter, req => key)` 已封装为中间件，超限自动返回 429。

---

## 十一、WebSocket

`server.WebSockets`（类型 `WebSocketHub`）

### 回调

| 属性 | 签名 | 说明 |
|---|---|---|
| `OnMessage` | `(clientId, message, reply) => Task` | 收到消息 |
| `OnConnectionChanged` | `(clientId, connected) => Task` | 连接建立/断开 |

两个回调的委托类型分别是 `OnWebSocketMessage` 与 `OnWebSocketConnectionChanged`，
也可用命名方法赋值：`server.WebSockets.OnMessage = HandleMessage;`

### 方法

| 方法 | 说明 |
|---|---|
| `SendAsync(clientId, message)` | 发给指定客户端 |
| `BroadcastAsync(message)` | 广播给所有在线客户端 |
| `CloseClientAsync(clientId, reason)` | 主动断开 |
| `CloseAllAsync(reason)` | 断开全部 |
| `OnlineClients()` | 返回在线客户端 id 数组 |
| `Count` | 当前连接数 |

```csharp
server.EnableWebSocket = true;
server.WebSockets.OnMessage = async (id, msg, reply) => await reply($"echo:{msg}");
server.WebSockets.OnConnectionChanged = (id, up) => {
    Console.WriteLine($"{id} {(up ? "上线" : "下线")}");
    return Task.CompletedTask;
};

// 后台定时推送
await server.WebSockets.BroadcastAsync("{\"kind\":\"tick\"}");
```

> WebSocket 握手同样走鉴权中间件。浏览器 WebSocket 无法设请求头，
> 故需支持 `?token=` —— 内置 `ExtractToken()` 已覆盖。

---

## 十二、依赖注入

| 方法 | 说明 |
|---|---|
| `req.Services` | 取当前请求的 `IServiceProvider` |
| `req.GetService<T>()` | 不解服务返回 null |
| `req.RequireService<T>()` | 未注册抛 `InvalidOperationException` |
| `RequestScope.Current` | 静态访问当前作用域 |

```csharp
server.MapGet("/api/di", (req, res) => {
    var svc = req.RequireService<MyService>();
    return res.WriteJsonAsync("{}");
});
```

---

## 十三、日志

### 记录日志

`server.Logger`（`ILogger`）默认 `NullLogger`。用内置源生成方法记录以保证零装箱：

```csharp
using NanoServer.Logging;

NanoLogger.ServerStarted(server.Logger, server.Prefix!, "JIT");
NanoLogger.AuthRejected(logger, "GET", "/api/x", "缺少 token");
NanoLogger.SsrfBlocked(logger, url, verdict.ToString());
```

### 全部日志方法（19 个）

| EventId | 方法 | 级别 | 消息模板 |
|---|---|---|---|
| 1001 | `ServerStarted(logger, prefix, mode)` | Info | NanoServer 启动：{Prefix}（模式 {Mode}） |
| 1002 | `ServerStopped(logger, prefix)` | Info | NanoServer 停止：{Prefix} |
| 1003 | `AcceptLoopRetry(logger, reason)` | Warn | 监听中断，10ms 后重试：{Reason} |
| 2001 | `RequestCompleted(logger, method, path, status, ms)` | Info | {Method} {Path} {Status} {ElapsedMs}ms |
| 2002 | `RouteNotFound(logger, method, path)` | Warn | 路由未命中：{Method} {Path} |
| 2003 | `MethodNotAllowed(logger, method, path, allowed)` | Warn | 方法不允许：{Method} {Path}（允许 {Allowed}） |
| 3001 | `UnhandledException(logger, ex, method, path)` | Error | 请求处理异常：{Method} {Path} |
| 3002 | `CannotWriteErrorPage(logger, ex, path)` | Debug | 响应已开始写出，无法再写错误页：{Path} |
| 4001 | `AuthRejected(logger, method, path, reason)` | Warn | 鉴权失败：{Method} {Path}（{Reason}） |
| 5001 | `DotFileRejected(logger, path)` | Warn | 拒绝 dotfile 访问：{Path} |
| 5002 | `PathOutsideRootRejected(logger, path)` | Warn | 拒绝越权路径：{Path} |
| 6001 | `WebSocketConnected(logger, clientId, online)` | Info | WebSocket 连接建立：{ClientId}（在线 {Online}） |
| 6002 | `WebSocketDisconnected(logger, clientId, online)` | Info | WebSocket 连接关闭：{ClientId}（在线 {Online}） |
| 6003 | `WebSocketRejected(logger, max)` | Warn | WebSocket 连接数超限（上限 {Max}），拒绝握手 |
| 6004 | `WebSocketMessageTooLarge(logger, clientId)` | Warn | WebSocket 消息过大：{ClientId}，已断开 |
| 6005 | `WebSocketHeartbeatTimeout(logger, clientId)` | Debug | WebSocket 心跳超时：{ClientId}，已断开 |
| 7001 | `SsrfBlocked(logger, url, verdict)` | Warn | SSRF 拦截：{Url}（{Verdict}） |
| 7002 | `RateLimited(logger, key)` | Warn | 限流触发：{Key} |
| 7003 | `CorsRejected(logger, origin)` | Warn | CORS 拒绝未授权来源：{Origin} |

### 事件 ID 分段（便于过滤）

按段落过滤示例：

```csharp
builder.Logging.AddFilter("NanoServer", LogLevel.Warning);
builder.Logging.AddFilter((category, level, eventId) =>
    eventId.Id >= NanoLogEvents.Security);   // 只看安全事件
```

---

## 十三·二、底层扩展（进阶）

以下 API 通常在标准用法中由框架自动调用，仅在自定义管线时需要直接使用。

| API | 说明 |
|---|---|
| `WebSocketHub.AcceptAsync(HttpListenerContext)` | 接管一次已升级的 WS 请求。自行判定 `req.IsWebSocketRequest` 后调用 |
| `StaticFilesMiddleware(urlPrefix, rootFolder, maxAge)` | 静态文件中间件实例，可 `.Logger = ...` 后 `Use(mw.InvokeAsync)` |
| `MiddlewarePipeline.InvokeAsync(req, res, ct)` | 手动执行整条管线（返回 false 表示已被终结） |
| `RouteTable.Match(path, method)` | 直接查询路由表，返回 `RouteMatch` 值类型 |
| `RouteMatch.Ok(entry)` / `NotFound()` / `MethodNotAllowed(allowed)` | 构造匹配结果（自定义路由逻辑时用） |

```csharp
// 自定义静态文件中间件并注入独立日志器
var mw = new StaticFilesMiddleware("/assets", "public", maxAgeSeconds: 86400)
{
    Logger = customLogger,
};
server.Use(mw.InvokeAsync);
```

> `Bucket`、`TryTake`、`Sum` 等为内部实现细节，不属于公开契约。

## 十四、异常处理

| 方法 | 说明 |
|---|---|
| `OnUnhandledException(observer)` | 注册异常观察者（记日志用） |

处理器抛出的异常由框架统一转 500，**不会裸断连接**。观察者自身抛错也不影响响应。

```csharp
server.OnUnhandledException((ex, req) =>
    server.Logger.LogError(ex, "处理 {Path} 失败", req.Url?.AbsolutePath));
```

---

## 十五、响应内容类型常量

`NanoServer.Http.ContentTypes`：

| 常量 | 值 |
|---|---|
| `Json` | `application/json; charset=utf-8` |
| `Xml` | `application/xml; charset=utf-8` |
| `Plain` | `text/plain; charset=utf-8` |
| `Html` | `text/html; charset=utf-8` |
| `FormUrlEncoded` | `application/x-www-form-urlencoded` |
| `MultipartFormData` | `multipart/form-data` |
| `OctetStream` | `application/octet-stream` |
| `EventStream` | `text/event-stream; charset=utf-8` |

`ContentTypes.Get(".png")` 按扩展名取 MIME；未知类型回退 `application/octet-stream`，
文本类扩展名自动追加 `charset=utf-8`。
