using LiteServer;
using LiteServer.Hosting;
using LiteServer.Http;
using LiteServer.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LiteServer.Sample.Di;
using LiteServer.Sample.Jobs;

namespace LiteServer.Sample;

/// <summary>
/// LiteServer 示例，演示两种启动方式。
/// <code>
/// dotnet run -- --standalone   独立模式（自建 Main 循环）
/// dotnet run                   通用主机模式（IHost + DI + 日志 + 优雅停机）
/// </code>
/// </summary>
public static class Program
{
    private const string ApiKey = "lite-sample-secret";
    private const string JwtSecret = "lite_sample_jwt_secret_2026";
    private const int Port = 8090;

    public static async Task<int> Main(string[] args)
    {
        bool standalone = args.Contains("--standalone");
        return standalone
            ? await RunStandaloneAsync().ConfigureAwait(false)
            : await RunGenericHostAsync(args).ConfigureAwait(false);
    }

    // ============ 方式一：通用主机 ============
    private static async Task<int> RunGenericHostAsync(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // ===== 依赖注入验证：三种生命周期各注册一个 =====
        builder.Services.AddSingleton<SingletonCounter>();   // 全进程共享
        builder.Services.AddScoped<ScopedStamp>();           // 每请求一个
        builder.Services.AddTransient<TransientStamp>();     // 每次解析都新建
        builder.Services.AddScoped<DiShowcase>();            // 构造函数注入三种生命周期

        // ===== 后台任务服务：随通用主机启动/停止 =====
        builder.Services.AddSingleton<JobService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobService>());

        builder.Services.AddLiteServer(
            options =>
            {
                options.Host = "127.0.0.1";
                options.Port = Port;
                options.JwtSecret = JwtSecret;
                options.JwtClockSkew = TimeSpan.FromSeconds(30);
                options.BlacklistCleanupInterval = TimeSpan.FromMinutes(5);
                options.EnableCors = true;
                options.EnableWebSocket = true;
                options.EnableWebSocketHeartbeat = true;
                options.StaticFilePrefix = "/";
                options.StaticFileRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                // 本示例是把测试页当公开静态站点用的：静态先于鉴权，
                // 这样 dotfile 探测由静态中间件直接拒绝（403），而不是被鉴权先挡成 401。
                options.StaticFilesBeforeAuth = true;
                options.AnonymousPaths.Add("/api/health");
                options.AnonymousPaths.Add("/api/runtime");
                options.AnonymousPaths.Add("/api/login");
                options.AnonymousPaths.Add("/");
                options.AnonymousPaths.Add("/index.html");
                options.AnonymousPaths.Add("/api/di");
                options.AnonymousPaths.Add("/api/di/log");
                options.AnonymousPaths.Add("/api/jobs");
            },
            server =>
            {
                RegisterRoutes(server);
                JobEndpoints.Map(server);   // 后台任务 + DI 探测端点
            });

        using IHost host = builder.Build();

        // 从容器里取出实例，注册 WebSocket 回调（需要用到 DI 里的服务时可在此注入）。
        var server = host.Services.GetRequiredService<LiteWebServer>();
        RegisterWebSocket(server);

        Console.WriteLine($"启动方式：通用主机（IHost）  端口 {Port}");
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    // ============ 方式二：独立模式 ============
    private static async Task<int> RunStandaloneAsync()
    {
        // 独立模式自建一个最小容器：不引入通用主机，但 DI 能力照常可用。
        // 若你的场景不需要 DI，这段可以整段删掉——库本身不强制依赖容器。
        var services = new ServiceCollection();
        services.AddSingleton<SingletonCounter>();
        services.AddScoped<ScopedStamp>();
        services.AddTransient<TransientStamp>();
        services.AddScoped<DiShowcase>();
        services.AddSingleton<JobService>();
        services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        ServiceProvider provider = services.BuildServiceProvider();

        await using var server = new LiteWebServer();
        server.ServiceProvider = provider;   // 关键：交给服务器，之后每请求自动开作用域

        server.EnableWebSocket = true;
        server.WebSockets.Options.EnableHeartbeat = true;

        server.AllowAnonymous("/api/health")
              .AllowAnonymous("/api/runtime")
              .AllowAnonymous("/api/login")
              .AllowAnonymous("/")
              .AllowAnonymous("/index.html")
              .AllowAnonymous("/api/di")
              .AllowAnonymous("/api/di/log")
              .AllowAnonymous("/api/jobs");

        // 中间件顺序即执行顺序
        server.UseCors();
        server.Use((req, res) =>
        {
            Console.WriteLine($"[req] {req.HttpMethod} {req.Url?.AbsolutePath}");
            return Task.FromResult(true);
        });

        RegisterRoutes(server);
        JobEndpoints.Map(server);
        RegisterWebSocket(server);

        // 后台任务在独立模式下没有宿主来托管，这里手动随服务一起起停。
        var jobs = provider.GetRequiredService<JobService>();

        // 顺序要与通用主机模式保持一致：静态先于鉴权。
        server.UseStaticFiles("/", Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        server.UseJwtAuth(JwtSecret, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));

        await server.StartAsync("127.0.0.1", Port).ConfigureAwait(false);

        Console.WriteLine($"启动方式：独立模式  地址 {server.Prefix}");
        Console.WriteLine("按 Ctrl+C 停止…");

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.TrySetResult();
        await quit.Task.ConfigureAwait(false);

        // 独立模式下需要自己停后台服务并释放容器。
        await jobs.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    // ============ 共用的路由与 WebSocket ============
    private static void RegisterRoutes(LiteWebServer server)
    {
        // 白名单
        server.MapGet("/api/health", (req, res) =>
            res.WriteJsonAsync($"{{\"status\":\"ok\",\"runtime\":\"{Environment.Version}\"}}"));

        server.MapGet("/api/runtime", (req, res) =>
        {
            bool aot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;
            return res.WriteJsonAsync(
                $"{{\"isAot\":{aot.ToString().ToLowerInvariant()},\"cores\":{Environment.ProcessorCount}}}");
        });

        // 登录换 JWT
        server.MapPost("/api/login", async (req, res) =>
        {
            string body = await req.ReadBodyAsStringAsync().ConfigureAwait(false);

            string key;
            if (string.IsNullOrWhiteSpace(body))
            {
                key = req.GetQuery("apiKey") ?? string.Empty;
            }
            else
            {
                // 体非空但不是合法 JSON 对象 -> 400；不能默默当成空 key 再回 401，
                // 否则调用方无法区分「格式错」与「凭据错」。
                if (!TryReadApiKey(body, out string? parsed))
                {
                    await res.WriteErrorAsync(400, "invalid json body").ConfigureAwait(false);
                    return;
                }
                key = parsed ?? req.GetQuery("apiKey") ?? string.Empty;
            }

            if (key != ApiKey)
            {
                await res.WriteErrorAsync(401, "invalid apiKey").ConfigureAwait(false);
                return;
            }

            long exp = LiteWebServer.UnixSeconds(3600);
            string token = Jwt.Generate($"{{\"sub\":\"lite-client\",\"role\":\"user\",\"exp\":{exp}}}", JwtSecret);
            await res.WriteJsonAsync($"{{\"code\":1,\"token\":\"{token}\",\"exp\":{exp}}}").ConfigureAwait(false);
        });

        // 解码自身身份
        server.MapGet("/api/me", (req, res) =>
        {
            // 用 ExtractToken 而非 GetToken：前者额外支持 ?token= 与 cookie，
            // 否则浏览器直接打开该地址（无法带头）会拿不到 token。
            string? token = req.ExtractToken();
            if (string.IsNullOrEmpty(token)) return res.WriteErrorAsync(401, "missing token");

            string payload = Jwt.DecodePayload(token);
            return res.WriteJsonAsync($"{{\"code\":1,\"payload\":{payload}}}");
        });

        // 登出：拉黑
        server.MapPost("/api/logout", (req, res) =>
        {
            string? token = req.ExtractToken();
            if (string.IsNullOrEmpty(token)) return res.WriteErrorAsync(400, "missing token");

            string payload = Jwt.DecodePayload(token);
            long exp = Jwt.ReadLongClaim(payload, "exp") ?? LiteWebServer.UnixSeconds(3600);
            server.Blacklist.Add(token, exp);

            return res.WriteJsonAsync($"{{\"code\":1,\"revokedUntil\":{exp}}}");
        });

        // SSE
        server.MapGet("/api/events", async (req, res) =>
        {
            int count = req.GetQuery<int>("count");
            if (count <= 0 || count > 50) count = 5;

            res.ContentType = ContentTypes.EventStream;
            res.Headers["Cache-Control"] = "no-cache";
            res.SendChunked = true;

            try
            {
                for (int i = 1; i <= count; i++)
                {
                    await res.WriteTextAsync($"event: tick\nid: {i}\ndata: {{\"seq\":{i}}}\n\n").ConfigureAwait(false);
                    if (i < count) await Task.Delay(200).ConfigureAwait(false);
                }
                await res.WriteTextAsync("event: done\ndata: {\"closed\":true}\n\n").ConfigureAwait(false);
            }
            finally
            {
                res.Close();
            }
        });

        // 分块长连接
        server.MapGet("/api/chunk", async (req, res) =>
        {
            int count = req.GetQuery<int>("count");
            if (count <= 0 || count > 20) count = 3;

            res.ContentType = ContentTypes.Plain;
            res.SendChunked = true;
            try
            {
                for (int i = 1; i <= count; i++)
                {
                    await res.WriteTextAsync($"chunk {i}/{count}\r\n").ConfigureAwait(false);
                    if (i < count) await Task.Delay(150).ConfigureAwait(false);
                }
                await res.WriteTextAsync("end\r\n").ConfigureAwait(false);
            }
            finally
            {
                res.Close();
            }
        });

        // Cookie
        server.MapGet("/api/cookie", (req, res) =>
        {
            if (req.TryGetCookie("lite_sid", out string? sid) && !string.IsNullOrEmpty(sid))
            {
                return res.WriteJsonAsync($"{{\"code\":1,\"sid\":\"{sid}\",\"reused\":true}}");
            }

            sid = Guid.NewGuid().ToString("N");
            res.AppendCookie("lite_sid", sid, new CookieExtensions.CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                Expires = DateTimeOffset.UtcNow.AddHours(1),
            });
            return res.WriteJsonAsync($"{{\"code\":1,\"sid\":\"{sid}\",\"reused\":false}}");
        });

        // 文件上传
        server.MapPost("/api/upload", async (req, res) =>
        {
            string? name = req.Headers["filename"];
            if (string.IsNullOrWhiteSpace(name))
            {
                await res.WriteErrorAsync(400, "filename header required").ConfigureAwait(false);
                return;
            }
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            {
                await res.WriteErrorAsync(400, "invalid filename").ConfigureAwait(false);
                return;
            }

            string dir = Path.Combine(AppContext.BaseDirectory, "data");
            bool ok = await req.SaveFileAsync(Path.Combine(dir, name)).ConfigureAwait(false);
            if (!ok)
            {
                await res.WriteErrorAsync(500, "upload failed").ConfigureAwait(false);
                return;
            }
            await res.WriteJsonAsync($"{{\"code\":1,\"name\":\"{name}\"}}").ConfigureAwait(false);
        });

        // 文件下载（支持 Range）
        server.MapGet("/api/download", (req, res) =>
        {
            string name = req.GetQuery("name") ?? "sample.txt";
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            {
                return res.WriteErrorAsync(400, "invalid name");
            }
            return res.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "data", name), asAttachment: false, req);
        });

        // ============ 任务 CRUD（内存） ============
        server.MapGet("/api/tasks", (req, res) => res.WriteJsonAsync(TaskStore.ToJson()));

        server.MapPost("/api/tasks", async (req, res) =>
        {
            string raw = await req.ReadBodyAsStringAsync().ConfigureAwait(false);
            string title = string.Empty;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("title", out var t) &&
                        t.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        title = t.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    await res.WriteErrorAsync(400, "invalid json body").ConfigureAwait(false);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(title)) title = req.GetQuery("title") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                await res.WriteErrorAsync(400, "title required").ConfigureAwait(false);
                return;
            }
            if (title.Length > 200)
            {
                await res.WriteErrorAsync(400, "title too long (max 200)").ConfigureAwait(false);
                return;
            }

            var created = TaskStore.Add(title);
            await res.WriteJsonAsync($"{{\"id\":\"{created.Id}\",\"title\":{TaskStore.Quote(created.Title)},\"done\":false}}", 201)
                .ConfigureAwait(false);
        });

        // 只允许 DELETE：其余方法由路由层自动回 405 并带 Allow 头
        server.MapDelete("/api/tasks/*", (req, res) =>
        {
            string id = (req.Url?.AbsolutePath ?? string.Empty).Split('/').Last();
            return TaskStore.Remove(id)
                ? res.WriteJsonAsync($"{{\"code\":1,\"deleted\":\"{id}\"}}")
                : res.WriteErrorAsync(404, $"task {id} not found");
        });

        // 上游探活
        server.MapGet("/api/upstream", async (req, res) =>
        {
            string target = req.GetQuery("url") ?? string.Empty;
            if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                await res.WriteErrorAsync(400, "url must be absolute http(s)").ConfigureAwait(false);
                return;
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using HttpResponseMessage r = await http.GetAsync(uri).ConfigureAwait(false);
                await res.WriteJsonAsync($"{{\"code\":1,\"status\":{(int)r.StatusCode}}}").ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                await res.WriteErrorAsync(502, "upstream error").ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                await res.WriteErrorAsync(504, "upstream timeout").ConfigureAwait(false);
            }
        });
    }

    private static void RegisterWebSocket(LiteWebServer server)
    {
        server.WebSockets.OnConnectionChanged = (id, connected) =>
        {
            Console.WriteLine($"[ws] {id} {(connected ? "connected" : "disconnected")}");
            return Task.CompletedTask;
        };

        server.WebSockets.OnMessage = async (id, message, reply) =>
        {
            string escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            await reply($"{{\"echo\":\"{escaped}\",\"client\":\"{id}\"}}").ConfigureAwait(false);
        };
    }

    /// <summary>
    /// 从 JSON 体里取 apiKey。
    /// 返回 false 表示体不是合法 JSON 对象（调用方应回 400）。
    /// </summary>
    private static bool TryReadApiKey(string json, out string? apiKey)
    {
        apiKey = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("apiKey", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                apiKey = v.GetString();
            }
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
