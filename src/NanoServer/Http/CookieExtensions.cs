using System.Net;

namespace NanoServer.Http;

/// <summary>Cookie 读写与下发。</summary>
public static class CookieExtensions
{
    /// <summary>Cookie 选项。</summary>
    public sealed class CookieOptions
    {
        public string? Domain { get; set; }

        /// <summary>作用路径，默认根。</summary>
        public string Path { get; set; } = "/";

        /// <summary>绝对过期时间。为空表示会话 Cookie（关浏览器即失效）。</summary>
        public DateTimeOffset? Expires { get; set; }

        /// <summary>true 时 JS 读不到，可降低 XSS 窃取风险。</summary>
        public bool HttpOnly { get; set; }

        /// <summary>true 时仅 HTTPS 传输。</summary>
        public bool Secure { get; set; }

        /// <summary>SameSite 策略，默认 Lax。</summary>
        public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
    }

    /// <summary>SameSite 取值。</summary>
    public enum SameSiteMode
    {
        Unspecified = 0,
        None = 1,
        Lax = 2,
        Strict = 3,
    }

    /// <summary>
    /// 下发 Cookie。注意 HttpListenerResponse 的 SetCookie 只接受一个 Cookie，
    /// 多条需自行拼接 Set-Cookie 头——本方法在已存在 Set-Cookie 时改用追加方式。
    /// </summary>
    public static void AppendCookie(this HttpListenerResponse response, string key, string value, CookieOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        options ??= new CookieOptions();

        var cookie = new Cookie(key, value)
        {
            Path = options.Path,
            HttpOnly = options.HttpOnly,
            Secure = options.Secure,
        };
        if (!string.IsNullOrEmpty(options.Domain)) cookie.Domain = options.Domain;
        if (options.Expires.HasValue) cookie.Expires = options.Expires.Value.UtcDateTime;

        string header = BuildSetCookieHeader(cookie, options);

        // SetCookie 内部只保留最后一个；多条 Set-Cookie 需手工追加头部。
        if (string.IsNullOrEmpty(response.Headers["Set-Cookie"]))
        {
            response.Headers["Set-Cookie"] = header;
        }
        else
        {
            response.Headers["Set-Cookie"] += ", " + header;
        }
    }

    /// <summary>删除 Cookie：下发一个已过期的同名 Cookie。</summary>
    public static void DeleteCookie(this HttpListenerResponse response, string key, CookieOptions? options = null)
    {
        options ??= new CookieOptions();
        response.AppendCookie(key, string.Empty, new CookieOptions
        {
            Path = options.Path,
            Domain = options.Domain,
            HttpOnly = options.HttpOnly,
            Secure = options.Secure,
            Expires = DateTimeOffset.UnixEpoch,
        });
    }

    /// <summary>清除本次响应中的所有 Cookie。</summary>
    public static void ClearCookies(this HttpListenerResponse response)
    {
        response.Headers.Remove("Set-Cookie");
    }

    private static string BuildSetCookieHeader(Cookie cookie, CookieOptions options)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(cookie.Name).Append('=').Append(cookie.Value);

        if (!string.IsNullOrEmpty(cookie.Path)) sb.Append("; Path=").Append(cookie.Path);
        if (!string.IsNullOrEmpty(cookie.Domain)) sb.Append("; Domain=").Append(cookie.Domain);

        if (options.Expires.HasValue)
        {
            sb.Append("; Expires=").Append(options.Expires.Value.UtcDateTime.ToString("R"));
        }
        else if (cookie.Expires != DateTime.MinValue)
        {
            sb.Append("; Expires=").Append(cookie.Expires.ToUniversalTime().ToString("R"));
        }

        if (cookie.HttpOnly) sb.Append("; HttpOnly");
        if (cookie.Secure) sb.Append("; Secure");

        sb.Append(options.SameSite switch
        {
            SameSiteMode.None => "; SameSite=None",
            SameSiteMode.Strict => "; SameSite=Strict",
            SameSiteMode.Lax => "; SameSite=Lax",
            _ => string.Empty,
        });

        return sb.ToString();
    }
}
