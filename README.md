# LiteServer

零依赖的轻量 .NET Web 能力库。基于 `System.Net.HttpListener`，不需要 IIS / Kestrel，
可嵌入任何 .NET 应用，也可作为独立服务运行。支持 **.NET 通用主机（Generic Host）** 与 **NativeAOT**。

```csharp
await using var server = new LiteWebServer();
server.MapGet("/hello", (req, res) => res.WriteAsync("Hello LiteServer"));
await server.StartAsync("127.0.0.1", 8090);
```

## 文档

| 文档 | 内容 |
|---|---|
| [docs/API.md](docs/API.md) | 全部公开 API：签名、参数、示例 |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | 全部配置参数：默认值、边界、调优建议 |
| [docs/OPTIMIZATION.md](docs/OPTIMIZATION.md) | 性能优化评估与实测数据 |
| [docs/SECURITY-AUDIT.md](docs/SECURITY-AUDIT.md) | 安全审计报告 |
| [docs/README.md](docs/README.md) | 文档索引（按场景查） |

## 来源说明

LiteServer 的能力边界参考了 **PicoServer**（作者 juziyu，https://picoserver.cn/）——
包括其公开文档、NuGet 包对外暴露的 API 形态，以及在实际项目中对它的行为实测结论。

**代码是独立重写的**：本仓库不含 PicoServer 的任何反编译产物或原始实现，全部源码为原创。
PicoServer 的名称与实现归原作者所有，本项目与其无隶属或授权关系。

下述设计取向构成了本项目的骨架（均为公开可见的架构方法，非代码搬运）：

| 取向 | 说明 |
|---|---|
| 连接表与心跳时间表分离 | WebSocket 用两个 ConcurrentDictionary 分别存 socket 与最后活跃时间，读取心跳不阻塞连接读写 |
| 接收缓冲池化 | `ArrayPool<byte>.Shared` 租借，长连接下不产生逐帧 GC |
| 心跳惰性启动 + 幂等 | 不启用心跳就零后台开销 |
| 路由两级结构 | 精准路径走字典直查，通配按段数分桶后线性比对 |
| 静态文件容纳校验 | `GetFullPath` + 前缀包含判定，能挡 `..%2F` 编码绕过 |
| ETag 指纹 | 由「修改时间刻度 + 文件长度」构成，无需读内容 |
| accept 循环退避 | 异常后先查 `IsListening`，避免故障时空转打满 CPU |

## 相对 PicoServer 的改进

在完整对齐其能力的前提下，这些是 LiteServer 重新设计、而非沿用其做法的地方：

- **同路径可注册多个方法**。常见做法是以路径为唯一键存处理器，第二个注册会被静默忽略；
  本项目的路由表以 (路径, 方法) 为组合键，GET/POST/PUT/DELETE 可共存。
- **鉴权支持三种 token 通道**：`Authorization` 头 / `?token=` / cookie。
  浏览器原生 `EventSource` 与 `WebSocket` 无法设置自定义请求头，只认请求头会让 SSE/WS 永远 401。
- **JWT 黑名单语义明确**：`Add(token, expireAtUnixSeconds)` 收绝对时间戳而非 TTL。
- **完整 Range 支持**：206 / 416 / `Content-Range` 齐备，视频可拖动进度条。
- **中间件顺序即执行顺序**，无隐式插队。
- **全库无反射、无动态代码**，NativeAOT 下零 IL 警告。

## 中间件顺序：静态文件与鉴权

默认是**鉴权在前、静态文件在后**（安全优先）：受保护站点不会被匿名拉走任何资源。
副作用是 dotfile 探测会先拿到 `401` 而不是 `403`——请求还没走到静态层就被挡了。

如果你的站点本来就是公开静态站（比如内置的测试页），把顺序翻过来：

```csharp
options.StaticFilesBeforeAuth = true;   // 静态先跑：dotfile 由静态层拒绝(403)，静态资源匿名可取
```

两种顺序下受鉴权保护的接口都不会被匿名访问，区别只在静态资源的可及性。

## 两种启动方式

### 方式一：通用主机（推荐用于生产）

与 DI、配置、日志、健康检查、优雅停机共用同一套 .NET 宿主。

```csharp
using LiteServer;
using LiteServer.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLiteServer(
    options =>
    {
        options.Host = "127.0.0.1";
        options.Port = 8090;
        options.JwtSecret = "your-secret";
        options.EnableWebSocket = true;
        options.StaticFilePrefix = "/";
        options.StaticFileRoot = "wwwroot";
        options.AnonymousPaths.Add("/api/health");
    },
    server =>
    {
        server.MapGet("/api/health", (req, res) => res.WriteJsonAsync("""{"status":"ok"}"""));
    });

await builder.Build().RunAsync();
```

拿到容器中的实例即可注册路由、挂 WebSocket 回调：

```csharp
var server = host.Services.GetRequiredService<LiteWebServer>();
server.WebSockets.OnMessage = async (id, msg, reply) => await reply($"echo:{msg}");
```

### 方式二：独立模式

不引入通用主机，自建生命周期：

```csharp
await using var server = new LiteWebServer();
server.UseCors();
server.MapGet("/api/health", (req, res) => res.WriteAsync("ok"));
server.UseJwtAuth("your-secret");
server.UseStaticFiles("/", "wwwroot");

await server.StartAsync("127.0.0.1", 8090);
await Task.Delay(Timeout.Infinite);   // 或用 Ctrl+C 驱动
```

## 依赖注入与后台任务

`AddLiteServer` 会把根容器交给服务器，**每个请求创建独立作用域**，处理器内直接解析服务。
DI 相关扩展方法都在根命名空间 `LiteServer` 下，`using LiteServer;` 一行即可：

```csharp
using LiteServer;          // RequireService / GetService / Services 扩展

builder.Services.AddSingleton<SingletonCounter>();
builder.Services.AddScoped<ScopedStamp>();      // 每请求一个
builder.Services.AddTransient<TransientStamp>();// 每次解析都新建

builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobService>());  // 随主机启停

server.MapGet("/api/di", (req, res) =>
{
    var showcase = req.RequireService<DiShowcase>();   // 构造函数注入
    var again    = req.RequireService<ScopedStamp>();  // 同请求内复用同一实例
    ...
});
```

实现要点：

- 请求作用域由 `AsyncLocal<IServiceProvider>` 承载。`HttpListenerRequest` 在 .NET Core 上
  没有可挂载数据的 `Items` 集合，而路由处理器签名里也没有 `IServiceProvider`；
  `AsyncLocal` 按逻辑调用链传播，同请求多层 `await` 共享、并发请求互不串扰。
- 作用域在请求结束时释放，`IDisposable`/`IAsyncDisposable` 服务得以正常回收。
- `BackgroundService` 与 DI 全程不使用反射，NativeAOT 下零 IL 警告。
- **DI 是可选的**：不设 `ServiceProvider` 时库照常工作，只是处理器取不到服务；
  误调 `RequireService<T>()` 会抛 `InvalidOperationException` 而非静默返回 null。
  库本身只引用 `*.Abstractions` 包，不绑定任何容器实现。

示例提供了三个验证端点：

| 端点 | 验证内容 |
|---|---|
| `GET /api/di` | 三种生命周期语义、同请求 Scoped 复用、跨请求 Scoped 隔离、Singleton 共享 |
| `GET /api/di/log` | `ILogger<T>` 构造函数注入在 AOT 下可用 |
| `GET/POST/DELETE /api/jobs[/{id}]` | 后台任务创建、进度轮询、取消，由 `BackgroundService` 承载 |

## 服务端主动推送

示例包含一个基于 `BackgroundService` 的定时推送服务，向所有在线 WebSocket 连接广播
**完整系统快照**，分三块：

| 分组 | 内容 |
|---|---|
| 系统配置 | 主机名、用户、OS 与架构、运行时版本、编译模式（JIT/AOT）、逻辑核心、进程名与 PID、启动时间、工作目录、区域 |
| 运行时性能 | GC 堆 / 已提交 / 碎片、Gen0/1/2 累计与增量、GC 暂停累计与增量、累计分配量、线程池 worker/pending/completed、进程 CPU 使用率、运行时长 |
| 进程占用 | 工作集、私有内存、峰值工作集、虚拟内存、托管与 OS 线程、句柄数、用户态/内核态/总 CPU 时间、磁盘总量与可用、在线网卡数 |

### 跨平台字段的处理

macOS 上 `PrivateMemorySize64`、`PeakWorkingSet64`、`HandleCount` 恒为 0。
框架把这些字段置 `null` 而非 0（序列化时省略），前端显示「不可用」——
**不展示会误导的假数据**。实测 AOT 下该机器有 3 个字段不可用，前端均正确标注。

### 两个容易判错的地方

- **进程名**：以 `dotnet app.dll` 启动时，`Process.GetCurrentProcess().ProcessName` 与
  `Environment.ProcessPath` 都会给出宿主名 `dotnet`。只有入口程序集名在 JIT/AOT 下都指向真实应用。
- **编译模式**：不能用 `RuntimeFeature.IsDynamicCodeCompiled` 判断——csproj 里 `PublishAot=true`
  会让普通 build 的 runtimeconfig 也把它置 `false`，实测 JIT 运行却被误判为 NativeAOT。
  可靠判据是「同目录是否存在 `<AssemblyName>.runtimeconfig.json`」。

```csharp
public sealed class PushService : BackgroundService
{
    private readonly WebSocketHub _hub;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(IntervalMs, ct);
            if (_hub.Count == 0) continue;      // 无客户端时跳过组帧与序列化
            await _hub.BroadcastAsync(BuildFrameJson());
        }
    }
}

// 注册（需在 AddLiteServer 之后，见下方「停止顺序」）
builder.Services.AddSingleton<PushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PushService>());
```

`AddLiteServer` 会把服务器的 `WebSocketHub` 注册进容器，后台服务直接构造函数注入即可使用。

控制端点：

| 端点 | 说明 |
|---|---|
| `GET /api/push/status` | 运行状态、累计帧数、在线连接列表 |
| `POST /api/push/interval?ms=` | 调整推送间隔（200–60000 自动收敛） |
| `POST /api/push/now` | 立即推送一帧 |

### 停止顺序（容易踩的坑）

.NET 通用主机的 **HostedService 停止顺序与注册顺序相反**（后注册先停）。
若 `LiteServerHost` 后注册，它会先停止并关闭所有 WebSocket，
导致推送服务已无法发出「服务停止」通知——客户端只会看到连接静默断开。

实测验证：把 `AddLiteServer` 放在最前注册，`PushService` 注册在它之后，
停机时客户端就能收到 `{"kind":"notice","payload":{"message":"服务正在停止"}}`。

### 前端展示

页面第 09 区「服务端推送」：建立 WebSocket 后实时渲染指标卡片与帧日志，
可调间隔、手动触发一帧、查看服务端状态。前端还会检测帧序号跳变并统计丢帧数。

## 日志

库内所有关键路径都有结构化日志，基于 `Microsoft.Extensions.Logging` 抽象，
不绑定具体 provider，且全程无反射（NativeAOT 下零 IL 警告）。

| 事件 ID | 类别 | 内容 |
|---|---|---|
| 1001–1003 | 生命周期 | 启动（含 JIT/AOT 模式）、停止、监听重试 |
| 2001–2003 | 请求 | 访问日志（方法/路径/状态/耗时）、404、405 |
| 3001–3002 | 异常 | 未捕获异常（含堆栈）、响应已开始的降级 |
| 4001 | 鉴权 | 鉴权失败及具体原因（缺 token / 验签失败 / 已吊销） |
| 5001–5002 | 静态文件 | dotfile 拒绝、越权路径拒绝 |
| 6001–6005 | WebSocket | 连接建立/关闭、超限拒绝、消息过大、心跳超时 |
| 7001–7003 | 安全 | SSRF 拦截、限流触发、CORS 拒绝 |

### 接入方式

**通用主机模式**（自动注入，无需额外代码）：

```csharp
builder.Logging.AddSimpleConsole();
builder.Services.AddLiteServer(...);   // 自动把 ILogger<LiteWebServer> 注入库内部
```

**独立模式**（手动注入，不注入则降级为 `NullLogger`，零开销）：

```csharp
server.Logger = provider.GetRequiredService<ILogger<LiteWebServer>>();
server.WebSockets.Logger = provider.GetRequiredService<ILogger<LiteWebSocketHub>>();
```

### 访问日志的安全默认值

| 选项 | 默认 | 原因 |
|---|---|---|
| `IncludeQueryString` | `false` | 防止 `?token=` 等敏感参数落进日志（已实测确认不泄露） |
| `ExcludedPaths` | `{"/api/health"}` | 探活与压测会把日志刷满 |
| `SlowRequestThreshold` | `0`（全记） | 需要降量时设为如 100ms，只记慢请求 |
| `SampleEvery` | `1`（全记） | 高 QPS 时设为 10–100 抽样 |

### 性能影响（实测）

同步写文件的访问日志有实际成本，本机测量（`/api/runtime`，2000 请求 / 并发 50）：

| 配置 | 吞吐 | 相对无日志 |
|---|---|---|
| 关闭访问日志 | ~32,500 req/s | — |
| 全量记录 | ~24,800 req/s | **-24%** |
| 采样 1/20 | ~31,800 req/s | -2% |

结论：**高 QPS 场景建议开采样或设慢请求阈值**，默认全记适合开发与低流量服务。
若日志量大，改用异步写入的 provider（如 Serilog Async）比调采样更有效。

## 性能

仓库带两个压测脚本（依赖 [hey](https://github.com/rakyll/hey)）：

```bash
./tools/bench.sh                        # 分路径压测，多轮取中位数
./tools/bench-ab.sh                     # JIT vs NativeAOT 对照
```

### 参考数据

macOS arm64（Apple Silicon，10 核），每轮 2000 请求、并发 50、3 轮取中位数：

| 路径 | AOT | JIT | 说明 |
|---|---|---|---|
| `GET /api/health` | ~48,700 req/s | ~32,900 req/s | 纯路由基线 |
| `GET` 未命中路由 | ~43,800 req/s | ~27,400 req/s | 路由查找失败路径 |
| `GET /tiny.txt` | ~27,500 req/s | ~20,300 req/s | 静态文件（含 stat + 文件流） |
| `GET /api/me` | ~43,900 req/s | ~27,600 req/s | 含 HMAC-SHA256 验签 |
| `GET /api/di` | ~41,400 req/s | ~24,500 req/s | 含 DI 作用域创建与构造注入 |
| `/api/health` 禁用 keepalive | ~19,700 req/s | ~18,600 req/s | 长连接贡献约 2.5 倍吞吐 |

单次测量的 P50 延迟约 1.7–2.2 ms，P99 约 4.5–6.3 ms。

### 路由匹配已优化到零分配

实测每次路由匹配的分配（优化前 → 优化后）：

| 路径类型 | 延迟 | 分配 |
|---|---|---|
| 精准命中 | 0.112 µs → **0.048 µs** | 72 B → **0 B** |
| 通配命中 | 0.386 µs → **0.255 µs** | 392 B → **0 B** |
| 未命中 | 0.143 µs → 0.139 µs | 192 B → **0 B** |

做法：`RouteMatch` 改 `readonly struct`；通配切段用 `stackalloc Span<Range>` 替代 `string.Split`；
路由表改为不可变数组快照（注册期加锁替换引用，读取期零锁零复制）。

无中间件的最小服务上，端到端中位数从 34.7k 提升到 **47.4k req/s（+37%）**。
并发正确性：3193 万次并发读 + 124 万次并发写，零异常零漏命中。

更多可优化方向与「不建议做」的清单见 [docs/OPTIMIZATION.md](docs/OPTIMIZATION.md)。

### 怎么读这些数字

- **AOT 比 JIT 快**：本次测量中位数高出约 18%–48%（不同轮次差异较大），
  主要来自消除 JIT 编译与启动期预热。
- **health 与 404 接近** → 路由查找本身开销很小，不是瓶颈。
- **static 约为 health 的 55%** → 差额主要在文件 `open`/`read` 与 `stat`，属正常成本。
- **JWT 与 health 接近** → HS256 验签在微秒级，不构成瓶颈。
- **关掉 keepalive 吞吐腰斩** → 瓶颈在 TCP 建连，而不是应用逻辑。

### 一个被否决的优化（记录在此以免重犯）

曾尝试给静态文件加「路径 → 文件元数据」缓存，期望省掉 `GetFullPath` + `new FileInfo`。
微基准显示这两步约 1.96 µs/次，看似可观，但**端到端压测中无可测收益**：

```
基线       7256 req/s   (并发 1，中位数)
优化后     7260 req/s   (同并发，中位数)
```

原因：这部分开销被 socket I/O 与线程调度掩盖了。更严重的是，缓存元数据会带来
**ETag 陈旧 bug**——文件从 4 字节改成 11 字节后 ETag 未变，客户端收到 304 而继续读旧内容。
该改动已整体回退。教训：**微基准的收益必须用端到端测量验证，且缓存协商头绝不能缓存。**

## 安全性

框架层已处理的攻击面：路径穿越（含编码/双重编码/反斜杠/UTF-8 过编码绕过）、
dotfile 拒绝、JWT 验签（`alg=none` 与弱密钥均被拒，定长比较防时序侧信道）、
token 黑名单、CRLF 头注入、WebSocket 握手鉴权与消息大小上限。

自带 SSRF 防护组件 `SsrfGuard`（用于按用户 URL 发起请求的场景）：

```csharp
using LiteServer.Security;

if (!SsrfGuard.IsSafe(userUrl, out SsrfGuard.Verdict verdict))
{
    return res.WriteErrorAsync(400, $"url rejected: {verdict}");
}
```

它会在**解析出 IP 之后**判定，拒绝回环 / 私有网段 / 链路本地（含云元数据 169.254.169.254）/
保留段，并处理十进制、八进制、十六进制 IP 与 IPv4 映射 IPv6 等变形。

```bash
./tools/security-test.sh    # 28 项安全回归断言
```

完整审计结果与已知局限见 [docs/SECURITY-AUDIT.md](docs/SECURITY-AUDIT.md)。

## 能力清单

| 类别 | 能力 |
|---|---|
| 路由 | 精准路由、`*` 单段通配、同路径多方法、任意方法路由、405 自动带 `Allow` 头 |
| 请求 | 查询参数强类型读取、表单解析、Body 读取（同步/异步）、Cookie 读取、Token 提取 |
| 响应 | 文本/JSON 写出、状态码、204、错误响应、CORS 预检 |
| 文件 | 下载（含 Range/206/416）、上传（流式落盘 + 进度回调）、流式发送、直播流 |
| 实时 | SSE、分块长连接、WebSocket（广播、定向、主动断开、心跳超时） |
| 安全 | JWT（HS256 签发/验签/解码）、Token 黑名单、简单 Token、Bearer Token、限流 |
| 中间件 | 自定义中间件、静态文件托管（ETag/304/隐藏文件拒绝/目录防穿越） |
| 宿主 | 通用主机集成、DI 注册、每请求作用域、NativeAOT |
| 后台 | `BackgroundService` 承载的后台任务、进度查询、取消 |

## 快速验证

```bash
# 通用主机模式
dotnet run --project samples/LiteServer.Sample

# 独立模式
dotnet run --project samples/LiteServer.Sample -- --standalone

# AOT 发布
dotnet publish samples/LiteServer.Sample -c Release -r osx-arm64 -o dist
```

## 目录结构

```
src/LiteServer/
  LiteWebServer.cs            服务器主体：路由/中间件/WS/生命周期
  Http/ContentTypes.cs        MIME 映射
  Http/RequestExtensions.cs   请求读取
  Http/ResponseExtensions.cs  响应写出
  Http/ChunkWriter.cs         分块推送
  Http/FileTransfer.cs        文件上传下载（Range）
  Http/CookieExtensions.cs    Cookie 读写
  Routing/RouteTable.cs       两级路由表
  Middleware/MiddlewarePipeline.cs
  Middleware/StaticFilesMiddleware.cs
  Middleware/AuthMiddleware.cs
  Security/Jwt.cs             HS256 JWT
  Security/TokenBlacklist.cs  吊销名单 + 限流
  WebSockets/WebSocketHub.cs  连接管理
  RequestScope.cs             每请求 DI 作用域（根命名空间）
  Hosting/                    通用主机集成
samples/LiteServer.Sample/    双模式示例
```

## 许可

本仓库为原创实现，采用 MIT 许可证。详见 [LICENSE](LICENSE)。
