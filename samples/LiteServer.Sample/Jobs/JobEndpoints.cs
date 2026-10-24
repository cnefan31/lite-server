using System.Text.Json;
using LiteServer;          // RequireService / GetService 扩展
using LiteServer.Http;
using LiteServer.Sample.Di;
using Microsoft.Extensions.Logging;

namespace LiteServer.Sample.Jobs;

/// <summary>
/// 后台任务的 HTTP 端点。
/// <para>
/// 每个处理器都通过 <c>request.RequireService&lt;T&gt;()</c> 从**当前请求作用域**取服务，
/// 而不是用静态字段——这正是依赖注入要验证的核心能力。
/// </para>
/// </summary>
public static class JobEndpoints
{
    public static void Map(LiteWebServer server)
    {
        // 创建任务
        server.MapPost("/api/jobs", async (req, res) =>
        {
            JobService jobs = req.RequireService<JobService>();
            ScopedStamp scoped = req.RequireService<ScopedStamp>();

            string raw = await req.ReadBodyAsStringAsync().ConfigureAwait(false);

            CreateJobRequest? body = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    body = JsonSerializer.Deserialize(raw, JobJsonContext.Default.CreateJobRequest);
                }
                catch (JsonException)
                {
                    await res.WriteErrorAsync(400, "invalid json body").ConfigureAwait(false);
                    return;
                }
            }

            // 也支持查询参数，方便 curl 快速触发
            string? name = body?.Name ?? req.GetQuery("name");
            int? steps = body?.Steps ?? req.GetQuery<int>("steps");
            int? delay = body?.StepDelayMs ?? req.GetQuery<int>("delayMs");

            JobRecord record = jobs.Create(name, steps, delay, scoped.InstanceId);

            string json = JsonSerializer.Serialize(
                new CreateJobResponse(record.Id, record.State.ToString(), $"/api/jobs/{record.Id}"),
                JobJsonContext.Default.CreateJobResponse);

            await res.WriteJsonAsync(json, 201).ConfigureAwait(false);
        });

        // 任务列表
        server.MapGet("/api/jobs", (req, res) =>
        {
            JobService jobs = req.RequireService<JobService>();
            int limit = req.GetQuery<int>("limit");
            JobRecord[] items = jobs.List(limit);
            string json = JsonSerializer.Serialize(new JobListResponse(items.Length, items), JobJsonContext.Default.JobListResponse);
            return res.WriteJsonAsync(json);
        });

        // 单个任务状态
        server.MapGet("/api/jobs/*", (req, res) =>
        {
            JobService jobs = req.RequireService<JobService>();
            string id = LastSegment(req.Url?.AbsolutePath);
            JobRecord? record = jobs.Get(id);
            if (record is null)
            {
                return res.WriteErrorAsync(404, $"job {id} not found");
            }
            return res.WriteJsonAsync(JsonSerializer.Serialize(record, JobJsonContext.Default.JobRecord));
        });

        // 取消任务
        server.MapDelete("/api/jobs/*", (req, res) =>
        {
            JobService jobs = req.RequireService<JobService>();
            string id = LastSegment(req.Url?.AbsolutePath);
            if (jobs.Get(id) is null)
            {
                return res.WriteErrorAsync(404, $"job {id} not found");
            }
            bool ok = jobs.Cancel(id);
            return res.WriteJsonAsync($"{{\"id\":\"{id}\",\"cancelRequested\":{ok.ToString().ToLowerInvariant()}}}");
        });

        // 依赖注入自证：一次性把三种生命周期与作用域隔离都验证出来
        server.MapGet("/api/di", (req, res) =>
        {
            DiShowcase showcase = req.RequireService<DiShowcase>();
            ScopedStamp scopedAgain = req.RequireService<ScopedStamp>();
            TransientStamp transientA = req.RequireService<TransientStamp>();
            TransientStamp transientB = req.RequireService<TransientStamp>();

            int hits = showcase.Singleton.Increment();

            bool scopedIsFresh = ReferenceEquals(showcase.Scoped, scopedAgain);
            bool transientIsNew = !ReferenceEquals(transientA, transientB)
                                  && !ReferenceEquals(transientA, showcase.Transient);

            var notes = new List<string>
            {
                scopedIsFresh
                    ? "同一请求内多次解析 Scoped 得到同一实例 ✓"
                    : "Scoped 在同一请求内出现多个实例 ✗",
                transientIsNew
                    ? "连续解析 Transient 得到不同实例 ✓"
                    : "Transient 被复用 ✗",
                $"Singleton 累计解析 {hits} 次，例：{showcase.Singleton.InstanceId}",
            };

            var payload = new DiProbeResponse(
                ScopeId: scopedAgain.InstanceId,
                SingletonId: showcase.Singleton.InstanceId,
                ScopedId: scopedAgain.InstanceId,
                TransientId: transientA.InstanceId,
                SingletonResolveCount: hits,
                ScopedIsFreshPerRequest: scopedIsFresh,
                SingletonIsShared: true,
                TransientIsAlwaysNew: transientIsNew,
                Notes: notes.ToArray());

            return res.WriteJsonAsync(JsonSerializer.Serialize(payload, JobJsonContext.Default.DiProbeResponse));
        });

        // 日志能力自检：把当前日志配置反馈给页面。
        // 注意必须用源生成序列化——AOT 下反射序列化被禁用。
        server.MapGet("/api/logs/info", (req, res) =>
        {
            LiteWebServer srv = req.RequireService<LiteWebServer>();
            Logging.AccessLogOptions opts = srv.AccessLog;

            var payload = new LogInfoResponse(
                Enabled: opts.Enabled,
                IncludeQueryString: opts.IncludeQueryString,
                SlowThresholdMs: opts.SlowRequestThreshold.TotalMilliseconds,
                ExcludedPaths: opts.ExcludedPaths.ToArray(),
                LoggerType: srv.Logger.GetType().Name,
                EventRanges: new Dictionary<string, int>
                {
                    ["ServerLifecycle"] = Logging.LiteLogEvents.ServerLifecycle,
                    ["RequestPipeline"] = Logging.LiteLogEvents.RequestPipeline,
                    ["Exception"] = Logging.LiteLogEvents.Exception,
                    ["Authentication"] = Logging.LiteLogEvents.Authentication,
                    ["StaticFiles"] = Logging.LiteLogEvents.StaticFiles,
                    ["WebSocket"] = Logging.LiteLogEvents.WebSocket,
                    ["Security"] = Logging.LiteLogEvents.Security,
                });

            return res.WriteJsonAsync(
                JsonSerializer.Serialize(payload, JobJsonContext.Default.LogInfoResponse));
        });

        // 用日志证明 DI 注入的 ILogger 在 AOT 下可用
        server.MapGet("/api/di/log", (req, res) =>
        {
            ILogger<JobService>? logger = req.GetService<ILogger<JobService>>();
            if (logger is null)
            {
                return res.WriteErrorAsync(500, "ILogger 未注入");
            }

            logger.LogInformation("依赖注入日志探测：{Scope} 请求了 /api/di/log", req.RequireService<ScopedStamp>().InstanceId);
            return res.WriteJsonAsync("{\"logged\":true}");
        });
    }

    private static string LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int i = path.TrimEnd('/').LastIndexOf('/');
        return i < 0 ? string.Empty : path.TrimEnd('/')[(i + 1)..];
    }
}
