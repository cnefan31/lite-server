# NanoServer 配置与调优手册

本手册列出**全部可配置参数**、默认值、取值边界与调优建议。
所有数值均为 macOS arm64 / .NET 10.0.5 实测结果，原始数据见 [OPTIMIZATION.md](OPTIMIZATION.md)。

> 记号说明：**默认值**为不显式设置时的行为；**实测影响**指该参数对吞吐/延迟/内存的实际测量效果。

---

## 一、宿主与监听（NanoServerOptions）

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `Host` | `string?` | `"127.0.0.1"` | 监听地址。`"+"` 表示所有网卡；`"0.0.0.0"` 等价于全部 IPv4 |
| `Port` | `int` | `8090` | 监听端口，取值 1–65535，越界抛 `ArgumentOutOfRangeException` |

**调优建议**

- 生产环境通常由反向代理（Nginx/Caddy）转发，此时监听 `127.0.0.1` 即可，不要暴露 `+`。
- Windows 下监听 `+` 需要管理员权限；仅本机访问请用 `127.0.0.1` 规避提权。
- 端口被占用时 `StartAsync` 会抛异常，建议在启动处捕获并给出明确提示。

```csharp
options.Host = "127.0.0.1";
options.Port = 8090;
```

---

## 二、WebSocket（NanoServerOptions + WebSocketOptions）

`NanoServerOptions` 只暴露最常用的三项，其余需直接设置 `server.WebSockets.Options`。

| 参数 | 所在类 | 类型 | 默认值 | 说明 |
|---|---|---|---|---|
| `EnableWebSocket` | `NanoServerOptions` | `bool` | `false` | 是否启用。与 HTTP 共用同一端口 |
| `WebSocketMaxConnections` | `NanoServerOptions` | `int` | `1000` | 最大并发连接，超出返回 503 |
| `EnableWebSocketHeartbeat` | `NanoServerOptions` | `bool` | `false` | 心跳超时检测（关闭时零后台开销） |
| `WebSocketHeartbeatTimeout` | `NanoServerOptions` | `TimeSpan` | 30 秒 | 多久没收到任何帧即判掉线 |
| `HeartbeatInterval` | `WebSocketOptions` | `TimeSpan` | 5 秒 | 心跳巡检间隔 |
| `PingText` | `WebSocketOptions` | `string` | `"ping"` | 收到该文本视为心跳，不转交业务 |
| `ReceiveBufferSize` | `WebSocketOptions` | `int` | `4096` | 单帧接收缓冲，从 `ArrayPool` 租借 |
| `MaxMessageBytes` | `WebSocketOptions` | `int` | 4 MB | 单条消息上限，超出即断开 |
| `MaxConnections` | `WebSocketOptions` | `int` | `1000` | 实际生效值（`NanoServerOptions` 会覆盖它） |

**调优建议**

| 场景 | 建议 |
|---|---|
| 纯 HTTP 服务 | 保持 `EnableWebSocket = false`，不产生任何 WebSocket 相关开销 |
| 长连接推送（如行情/日志） | 开启心跳，`HeartbeatTimeout` 设为客户端预期心跳间隔的 2–3 倍 |
| 大消息（如传图片） | 调大 `MaxMessageBytes` 与 `ReceiveBufferSize`，但注意每连接会占一份缓冲 |
| 连接数很多 | 降低 `ReceiveBufferSize`（如 1024），内存占用与连接数成正比 |

> **注意**：`HeartbeatTimeout` 若不开启 `EnableWebSocketHeartbeat` 则完全无效——心跳巡检是惰性启动的，不开启就不付任何后台成本。

```csharp
options.EnableWebSocket = true;
options.WebSocketMaxConnections = 2000;
options.EnableWebSocketHeartbeat = true;
options.WebSocketHeartbeatTimeout = TimeSpan.FromSeconds(60);

// 更细的控制直接设置 Hub 选项
server.WebSockets.Options.MaxMessageBytes = 8 * 1024 * 1024;
server.WebSockets.Options.ReceiveBufferSize = 8192;
```

---

## 三、鉴权

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `JwtSecret` | `string?` | `null` | HS256 密钥。设置后自动启用 JWT 鉴权 |
| `JwtClockSkew` | `TimeSpan` | 30 秒 | 允许的时钟偏移，避免多机时间不同步导致误判过期 |
| `BlacklistCleanupInterval` | `TimeSpan?` | `null` | 黑名单自动清理间隔；为 null 时只在读取时惰性清理 |
| `AnonymousPaths` | `List<string>` | 空 | 免鉴权路径，**精确匹配**，通配路由不进白名单 |

**调优建议**

- `BlacklistCleanupInterval`：登出频繁的服务建议设 5–15 分钟；低频可留 null 靠惰性清理。
- `JwtClockSkew`：跨机房部署建议放宽到 60 秒；单机可设 0 以严格校验。
- `AnonymousPaths` 只对精确路径生效。`/api/health` 加入白名单后，`/api/health/x` 仍需鉴权。

> **安全语义澄清**：`TokenBlacklist.Add(token, expireAtUnixSeconds)` 第二参是**绝对 Unix 秒时间戳**，不是 TTL。
> 传 TTL（如 3600）会被当成 1970 年，条目立即失效。

```csharp
options.JwtSecret = "your-256-bit-secret";
options.JwtClockSkew = TimeSpan.FromSeconds(60);
options.BlacklistCleanupInterval = TimeSpan.FromMinutes(5);
options.AnonymousPaths.Add("/api/health");
```

### 三种鉴权方式的选择

| 方式 | 方法 | 比对的头 | 适用 |
|---|---|---|---|
| JWT | `UseJwtAuth(secret)` | `Authorization: Bearer <jwt>` | 推荐。可携带声明、可吊销 |
| Bearer Token | `UseBearerToken(token)` | 剥离 `Bearer ` 后比对 | 简单固定密钥 |
| 简单 Token | `UseSimpleToken(token)` | **原始头值**，不剥离前缀 | 需与旧客户端兼容时才用 |

---

## 四、静态文件

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `StaticFilePrefix` | `string?` | `null` | 对外 URL 前缀，如 `"/"` 或 `"/assets"` |
| `StaticFileRoot` | `string?` | `null` | 本地目录。与 Prefix 同时设置才生效 |
| `StaticFileMaxAgeSeconds` | `int` | `0` | 强缓存秒数。0 表示只用 ETag 协商 |
| `StaticFilesBeforeAuth` | `bool` | `false` | 静态中间件是否排在鉴权之前 |

**`StaticFilesBeforeAuth` 的取舍（重要）**

| 取值 | 行为 | 适用 |
|---|---|---|
| `false`（默认） | 先鉴权再查静态文件。受保护站点匿名拿不到任何资源；但 dotfile 探测会先得到 **401** 而非 403 | 私有站点、API 服务 |
| `true` | 先查静态文件。dotfile 由静态层直接拒绝（**403**），静态资源匿名可取 | 公开静态站、内置测试页 |

两种顺序下受鉴权保护的接口都不会被匿名访问，区别只在静态资源可及性与 dotfile 的响应码。

**缓存策略**

| `StaticFileMaxAgeSeconds` | 行为 | 适用 |
|---|---|---|
| `0` | 每次发 `Cache-Control: no-cache` + ETag，客户端每次协商（304 省传输） | 开发环境、频繁变更的资源 |
| `3600` | 强缓存 1 小时 | 生产环境、带指纹的构建产物 |
| `31536000` | 强缓存 1 年 | 文件名含哈希的静态资源（immutable） |

> **实测教训**：曾尝试给静态文件加「路径→文件元数据」缓存，微基准显示 `GetFullPath + FileInfo` 约 1.96 µs/次，
> 但端到端压测毫无收益（7256 → 7260 req/s），且引入 **ETag 陈旧** bug（文件改动后 ETag 不变，
> 客户端收到 304 持续读旧内容）。该改动已回退。**缓存协商头绝不能缓存元数据。**

```csharp
options.StaticFilePrefix = "/";
options.StaticFileRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
options.StaticFileMaxAgeSeconds = 3600;
options.StaticFilesBeforeAuth = false;   // 私有站点
```

---

## 五、CORS

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `EnableCors` | `bool` | `true` | 是否启用 CORS 中间件 |
| `CorsAllowedOrigin` | `string?` | `null` | 允许的来源。null 表示只回显**同源**请求 |

**安全语义**

- `null`（默认）：只回显同源请求（比对 scheme+host+port），跨源一律不加 CORS 头。
- 显式指定：只有该来源获得 CORS 头，其余不加。
- **`null` Origin 永不放行**（sandbox iframe / `file://` / 跨源重定向都可能是它）。

> 早期实现无参时原样回显任意 `Origin` 且强制 `Allow-Credentials: true`，实测 `evil.example` 与 `null`
> 都被放行——等于允许任何站点带凭证读取响应。已修复。

```csharp
options.EnableCors = true;
options.CorsAllowedOrigin = "https://app.example.com";   // 指定单一来源
```

---

## 六、访问日志（AccessLogOptions）

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `Enabled` | `bool` | `true` | 是否记录访问日志 |
| `IncludeQueryString` | `bool` | `false` | **默认不含查询串**，防止 `?token=` 落日志 |
| `SlowRequestThreshold` | `TimeSpan` | `Zero` | 只记录慢于该阈值的请求；Zero 表示全记 |
| `ExcludedPaths` | `HashSet<string>` | `{"/api/health"}` | 不记日志的路径 |
| `SampleEvery` | `int` | `1` | 采样率：1=全记，10=每 10 条记 1 条 |

### 性能影响（实测）

同步写文件的访问日志有实际成本（`/api/runtime`，2000 请求 / 并发 50）：

| 配置 | 吞吐 | 相对无日志 |
|---|---|---|
| 关闭访问日志 | ~32,500 req/s | — |
| **全量记录** | ~24,800 req/s | **-24%** |
| 采样 1/20 | ~31,800 req/s | -2% |

**调优建议**

| QPS 量级 | 建议配置 |
|---|---|
| < 500 | 默认全量记录即可 |
| 500 – 5000 | `SampleEvery = 10` 或用 `SlowRequestThreshold = 100ms` |
| > 5000 | 关闭访问日志改由上游反代记录，或换异步写入的 provider（如 Serilog Async） |

> 若日志量大，**改异步 provider 比调采样更有效**——采样丢信息，异步只改写入时机。

```csharp
server.AccessLog.SampleEvery = 10;
server.AccessLog.SlowRequestThreshold = TimeSpan.FromMilliseconds(100);
server.AccessLog.IncludeQueryString = false;   // 保持默认，防 token 落盘
```

---

## 七、路由

路由表本身无配置项，但有若干**硬编码边界**：

| 常量 | 值 | 含义 |
|---|---|---|
| `MaxPathLength` | 2048 | 超过即判 404（防超长路径攻击） |
| `MaxWildcardSegments` | 16 | 通配路由最多 16 段 |

**注册语义**

- 同一 `(路径, 方法)` 重复注册：**后注册覆盖前者**。
- 同一路径可注册**多个不同方法**，互不覆盖（这是与常见实现的关键差异）。
- 方法为 `null` 表示该路径接受任意方法。
- 匹配顺序：精准路径优先 → 通配路径按段数从多到少。

**性能特性（已优化到零分配）**

| 路径类型 | 延迟 | 分配 |
|---|---|---|
| 精准命中 | 0.048 µs | **0 B/op** |
| 通配命中 | 0.255 µs | **0 B/op** |
| 未命中 | 0.139 µs | **0 B/op** |

> 因此**不需要给路由加缓存**——匹配已零分配且亚微秒级，加一层 `path→RouteEntry` 字典
> 反而引入字典查找（约 0.05 µs）与失效管理成本，净收益为负。

---

## 八、限流（RateLimiter）

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `capacity` | `int` | 100 | 窗口内允许的请求数 |
| `window` | `TimeSpan?` | 1 分钟 | 窗口长度 |

**语义**：每 key（默认取客户端 IP）维护独立令牌桶，窗口到期重置满额。

**调优建议**

| 场景 | 建议 |
|---|---|
| 全局限流 | `new RateLimiter(1000, TimeSpan.FromMinutes(1))` |
| 按 IP 粗粒度 | 默认 100/分钟 |
| 登录接口 | 单独 key（如 IP+路径），`capacity = 5` / 分钟 |

```csharp
var limiter = new RateLimiter(capacity: 200, window: TimeSpan.FromMinutes(1));
server.UseRateLimit(limiter, req => $"{req.RemoteEndPoint?.Address}|{req.Url?.AbsolutePath}");
```

---

## 九、SSRF 防护（SsrfGuard）

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `allowPrivateNetworks` | `bool` | `false` | 是否允许访问内网段。仅在探活自有集群时设为 true |

**拦截范围**（返回 `Verdict` 枚举说明原因）：

| 判定 | 拦截内容 |
|---|---|
| `Loopback` | 127.0.0.0/8、::1 |
| `Private` | 10/8、172.16/12、192.168/16、100.64/10（CGNAT）、fc00::/7 |
| `LinkLocal` | 169.254/16（含云元数据 169.254.169.254）、fe80::/10 |
| `Reserved` | 0.0.0.0/8、组播 224/4、保留 240/4、文档段 |
| `BadScheme` | 非 http/https |
| `Unresolvable` | DNS 解析失败 |

**关键设计**：在**解析出 IP 之后**判定，因此以下变形全部拦截：

```
127.0.0.1 · localhost · 2130706433（十进制）· 0177.0.0.1（八进制）
0x7f000001（十六进制）· 127.1（简写）· [::1] · [::ffff:127.0.0.1]
127.0.0.1.nip.io · localtest.me
```

```csharp
server.MapGet("/upstream", (req, res) =>
{
    string userUrl = req.GetQuery("url") ?? "";
    if (!SsrfGuard.IsSafe(userUrl, out SsrfGuard.Verdict verdict))
    {
        server.Logger.LogWarning("SSRF 拦截 {Url}：{Verdict}", userUrl, verdict);
        return res.WriteErrorAsync(400, $"url rejected: {verdict}");   // 非 async lambda：可直接 return Task
    }
    // ... 继续发起请求
    return res.WriteAsync("ok");
});
```

> 配合 `SocketsHttpHandler { AllowAutoRedirect = false }`，阻断「302 跳内网」绕过。
> 若业务确需跟随重定向，必须每一跳都重新做 SSRF 校验。

---

## 十、文件传输

### 下载 `SendFileAsync`

| 参数 | 默认值 | 说明 |
|---|---|---|
| `asAttachment` | `false` | true 强制下载；false 允许内联预览 |
| `request` | `null` | 传入后识别 `Range` 头，支持 206 分段 |

**Range 支持**：`bytes=0-99`、`bytes=100-`、`bytes=-100`（尾部）均支持；
越界返回 416 并带 `Content-Range: bytes */总长`；多区间不支持（按整体发送）。

### 上传 `SaveFileAsync`

| 参数 | 默认值 | 说明 |
|---|---|---|
| `onProgress` | `null` | 进度回调 `(已接收, 总字节)`；分块传输时总字节为 -1 |
| `savePath` | 必填 | 落盘路径，目录不存在自动创建 |

> **安全提示**：库**不限制上传大小**。实测无限制时单请求 100MB、10 路并发可落盘 500MB。
> 必须在业务层限制：

```csharp
server.MapPost("/upload", async (req, res) =>
{
    const long MaxUploadBytes = 32L * 1024 * 1024;

    if (req.ContentLength64 > MaxUploadBytes)
    {
        await res.WriteErrorAsync(413, "payload too large");
        return;
    }

    bool ok = await req.SaveFileAsync(path);
    if (!ok)
    {
        await res.WriteErrorAsync(500, "upload failed");
        return;
    }

    // 二次校验：chunked 传输时 ContentLength64 为 -1，须以实际落盘大小为准
    if (new FileInfo(path).Length > MaxUploadBytes)
    {
        File.Delete(path);
        await res.WriteErrorAsync(413, "payload too large");
        return;
    }

    await res.WriteJsonAsync("{\"ok\":true}");
});
```

---

## 十一、DI 作用域

| 参数 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `ServiceProvider` | `IServiceProvider?` | `null` | 设置后每请求创建独立作用域 |

**不设时行为**：库照常工作，处理器取不到服务；误调 `RequireService<T>()` 抛
`InvalidOperationException`（而非静默返回 null）。

**作用域生命周期**：请求开始 `CreateScope()`，请求结束 `Dispose()` ——
`IDisposable`/`IAsyncDisposable` 服务正常回收。

---

## 十二、通用配置组合

### 开发环境

```csharp
options.Host = "127.0.0.1";
options.EnableCors = true;
options.StaticFileMaxAgeSeconds = 0;        // 不缓存，改完即生效
options.StaticFilesBeforeAuth = true;       // 测试页公开
server.AccessLog.Enabled = true;            // 全量日志便于排查
```

### 生产 API 服务

```csharp
options.Host = "127.0.0.1";                 // 前置反代
options.JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");
options.JwtClockSkew = TimeSpan.FromSeconds(60);
options.BlacklistCleanupInterval = TimeSpan.FromMinutes(5);
options.CorsAllowedOrigin = "https://app.example.com";
options.EnableCors = true;
server.AccessLog.SampleEvery = 10;          // 降日志量
server.AccessLog.SlowRequestThreshold = TimeSpan.FromMilliseconds(100);
server.UseRateLimit(new RateLimiter(1000, TimeSpan.FromMinutes(1)));
```

### 高并发实时推送

```csharp
options.EnableWebSocket = true;
options.WebSocketMaxConnections = 5000;
options.EnableWebSocketHeartbeat = true;
options.WebSocketHeartbeatTimeout = TimeSpan.FromSeconds(60);
server.WebSockets.Options.ReceiveBufferSize = 2048;   // 连接多时省内存
server.AccessLog.ExcludedPaths.Add("/ws");            // WS 不加访问日志
```
