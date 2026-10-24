using System.Text.Json;
using LiteServer.Http;
using LiteServer.Hosting;

namespace LiteServer.Sample.Push;

/// <summary>推送服务的控制端点。</summary>
public static class PushEndpoints
{
    public static void Map(LiteWebServer server)
    {
        // 查询推送状态
        server.MapGet("/api/push/status", (req, res) =>
        {
            PushService push = req.RequireService<PushService>();
            return res.WriteJsonAsync(
                JsonSerializer.Serialize(push.Snapshot(), PushJsonContext.Default.PushStatus));
        });

        // 调整推送间隔（毫秒）
        server.MapPost("/api/push/interval", async (req, res) =>
        {
            PushService push = req.RequireService<PushService>();

            int? ms = req.GetQuery<int>("ms");
            if (ms is null)
            {
                string raw = await req.ReadBodyAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("ms", out JsonElement v) && v.TryGetInt32(out int parsed))
                        {
                            ms = parsed;
                        }
                    }
                    catch (JsonException)
                    {
                        await res.WriteErrorAsync(400, "invalid json body").ConfigureAwait(false);
                        return;
                    }
                }
            }

            if (ms is null)
            {
                await res.WriteErrorAsync(400, "ms required").ConfigureAwait(false);
                return;
            }

            int applied = push.SetInterval(ms.Value);
            await res.WriteJsonAsync($"{{\"code\":1,\"intervalMs\":{applied}}}").ConfigureAwait(false);
        });

        // 立即推送一帧
        server.MapPost("/api/push/now", async (req, res) =>
        {
            PushService push = req.RequireService<PushService>();
            bool delivered = await push.PushOnceAsync("手动触发").ConfigureAwait(false);
            await res.WriteJsonAsync(
                $"{{\"code\":1,\"delivered\":{(delivered ? "true" : "false")},\"online\":{push.Snapshot().OnlineClients}}}")
                .ConfigureAwait(false);
        });
    }
}
