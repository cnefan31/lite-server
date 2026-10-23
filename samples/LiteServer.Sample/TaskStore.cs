namespace LiteServer.Sample;

/// <summary>演示用的内存任务仓库。真实项目请替换为数据库。</summary>
internal static class TaskStore
{
    // 内部类型：调用方只需拿到 id/title，不必暴露完整实体
    internal sealed record Item(string Id, string Title, bool Done);

    private static readonly object Gate = new();
    private static readonly List<Item> Items =
    [
        new("t-1", "接入 LiteServer 路由", true),
        new("t-2", "验证 JWT 鉴权链路", true),
        new("t-3", "通用主机启动验证", false),
    ];
    private static int _seq = 100;

    public static Item Add(string title)
    {
        lock (Gate)
        {
            var item = new Item($"t-{Interlocked.Increment(ref _seq)}", title, false);
            Items.Add(item);
            return item;
        }
    }

    public static bool Remove(string id)
    {
        lock (Gate)
        {
            int idx = Items.FindIndex(x => x.Id == id);
            if (idx < 0) return false;
            Items.RemoveAt(idx);
            return true;
        }
    }

    public static string ToJson()
    {
        lock (Gate)
        {
            var parts = Items.Select(x =>
                $"{{\"id\":{Quote(x.Id)},\"title\":{Quote(x.Title)},\"done\":{(x.Done ? "true" : "false")}}}");
            return $"{{\"code\":1,\"total\":{Items.Count},\"items\":[{string.Join(",", parts)}]}}";
        }
    }

    /// <summary>JSON 字符串转义（示例用；生产请用序列化器）。</summary>
    public static string Quote(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
