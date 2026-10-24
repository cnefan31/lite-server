using System.Collections.Specialized;
using System.Net;
using System.Text;

namespace NanoServer.Http;

/// <summary>HttpListenerRequest 的读取扩展。所有方法对异常输入返回安全默认值，不抛。</summary>
public static class RequestExtensions
{
    /// <summary>同步读取请求体为 UTF-8 文本。仅用于小体量场景；大文件请用 SaveFileAsync。</summary>
    public static string ReadBodyAsString(this HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }

    /// <summary>异步读取请求体为 UTF-8 文本，不阻塞线程池线程。</summary>
    public static async Task<string> ReadBodyAsStringAsync(this HttpListenerRequest request, CancellationToken ct = default)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取查询参数原始字符串；不存在返回 null。
    /// 同时兼容 `?a=1` 与 `?A=1`（按忽略大小写匹配）。
    /// </summary>
    public static string? GetQuery(this HttpListenerRequest request, string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        NameValueCollection q = request.QueryString;
        if (q.Count == 0) return null;

        string? direct = q[key];
        if (direct is not null) return direct;

        // QueryString 的默认比较器区分大小写，这里补一轮忽略大小写的查找。
        foreach (string? k in q.AllKeys)
        {
            if (k is not null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return q[k];
            }
        }
        return null;
    }

    /// <summary>
    /// 强类型读取查询参数。转换失败或缺失时返回 default。
    /// 支持 string/int/long/double/decimal/bool/DateTime/Guid/枚举。
    /// </summary>
    public static T? GetQuery<T>(this HttpListenerRequest request, string key)
    {
        string? raw = request.GetQuery(key);
        if (string.IsNullOrEmpty(raw)) return default;

        Type t = typeof(T);
        Type target = Nullable.GetUnderlyingType(t) ?? t;

        try
        {
            if (target == typeof(string)) return (T)(object)raw;
            if (target == typeof(int)) return (T)(object)int.Parse(raw);
            if (target == typeof(long)) return (T)(object)long.Parse(raw);
            if (target == typeof(double)) return (T)(object)double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(decimal)) return (T)(object)decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(bool))
            {
                // "1"/"0"/"true"/"false"/"yes"/"on" 都接受，比 bool.Parse 宽容。
                string v = raw.Trim().ToLowerInvariant();
                bool parsed = v is "1" or "true" or "yes" or "on";
                return (T)(object)parsed;
            }
            if (target == typeof(DateTime)) return (T)(object)DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (target == typeof(Guid)) return (T)(object)Guid.Parse(raw);
            if (target.IsEnum) return (T)Enum.Parse(target, raw, ignoreCase: true);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // 转换失败返回默认值，不向调用方抛。
        }
        return default;
    }

    /// <summary>解析查询串为字典（键忽略大小写，值取首个）。</summary>
    public static Dictionary<string, string> ParseQuery(this HttpListenerRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NameValueCollection q = request.QueryString;
        foreach (string? key in q.AllKeys)
        {
            if (key is null) continue;
            result[key] = q[key] ?? string.Empty;
        }
        return result;
    }

    /// <summary>
    /// 解析 application/x-www-form-urlencoded 表单体。
    /// 非表单类型或读取失败时返回空字典。
    /// </summary>
    public static Dictionary<string, string> ParseForm(this HttpListenerRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!request.HasEntityBody) return result;

        string? contentType = request.ContentType;
        if (contentType is null ||
            !contentType.StartsWith(ContentTypes.FormUrlEncoded, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        try
        {
            string body = request.ReadBodyAsString();
            foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = WebUtility.UrlDecode(pair[..eq]);
                string v = WebUtility.UrlDecode(pair[(eq + 1)..]);
                if (k.Length > 0) result[k] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 连接中断等：返回已解析部分。
        }
        return result;
    }

    /// <summary>
    /// 取请求头中的 Token。会剥离 `Bearer ` / `Basic ` 前缀。
    /// 无 Authorization 头时返回 null。
    /// </summary>
    public static string? GetToken(this HttpListenerRequest request)
    {
        string? raw = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(raw)) return null;

        if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return raw[7..].Trim();
        if (raw.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return raw[6..].Trim();
        return raw.Trim();
    }

    /// <summary>
    /// 按优先级提取 token：<c>Authorization</c> 头 → <c>?token=</c> 查询参数 → <c>lite_token</c> cookie。
    /// <para>
    /// 三个通道的意义：请求头是标准做法；查询参数与 cookie 是为浏览器场景准备的——
    /// 原生 <c>EventSource</c> 与 <c>WebSocket</c> 都无法设置自定义请求头。
    /// </para>
    /// </summary>
    public static string? ExtractToken(this HttpListenerRequest request)
    {
        string? fromHeader = request.GetToken();
        if (!string.IsNullOrEmpty(fromHeader)) return fromHeader;

        string? fromQuery = request.GetQuery("token");
        if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;

        if (request.TryGetCookie("lite_token", out string? fromCookie) && !string.IsNullOrEmpty(fromCookie))
        {
            return fromCookie;
        }
        return null;
    }

    /// <summary>取 Cookie 值。存在返回 true 并输出值，否则 false。</summary>
    public static bool TryGetCookie(this HttpListenerRequest request, string key, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;

        Cookie? cookie = request.Cookies[key];
        if (cookie is null) return false;
        value = cookie.Value;
        return true;
    }
}
