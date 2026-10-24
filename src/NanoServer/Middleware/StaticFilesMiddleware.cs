using System.Net;
using NanoServer.Routing;
using NanoServer.Http;
using NanoServer.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NanoServer.Middleware;

/// <summary>
/// 静态文件托管。
/// <para>
/// 关键防护：
/// ① 路径解析走 GetFullPath + 前缀包含判定，挡 `..` 与编码绕过；
/// ② 隐藏文件（以 '.' 开头）一律拒绝；
/// ③ ETag 由「修改时间刻度 + 文件长度」构成，变更即变，无需读内容；
/// ④ If-None-Match 命中返回 304，省掉整个文件传输。
/// </para>
/// 注意：文件**不存在**时不终结请求而是放行（return true），让后续路由有机会处理该路径。
/// </summary>
public sealed class StaticFilesMiddleware
{
    private readonly string _urlPrefix;
    private readonly string _rootFullPath;
    private readonly int _maxAgeSeconds;

    /// <summary>日志器。默认 NullLogger，零开销。</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <param name="urlPrefix">对外路由前缀，如 "/" 或 "/assets"。</param>
    /// <param name="rootFolder">本地文件夹。</param>
    /// <param name="maxAgeSeconds">强缓存秒数，0 表示只用 ETag 协商。</param>
    public StaticFilesMiddleware(string urlPrefix, string rootFolder, int maxAgeSeconds = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootFolder);

        _urlPrefix = RouteTable.NormalizePath(urlPrefix ?? "/");
        if (_urlPrefix != "/" && !_urlPrefix.EndsWith('/')) _urlPrefix += "/";

        string root = Path.GetFullPath(rootFolder);
        _rootFullPath = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        _maxAgeSeconds = Math.Max(0, maxAgeSeconds);
    }

    public Task<bool> InvokeAsync(HttpListenerRequest request, HttpListenerResponse response)
        => InvokeAsync(request, response, CancellationToken.None);

    public async Task<bool> InvokeAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        string path;
        try
        {
            // 先解码再解析，防止 `..%2F` 这类编码绕过。
            path = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/");
        }
        catch (UriFormatException)
        {
            response.StatusCode = 400;
            return false;
        }

        if (!MatchesPrefix(path)) return true;

        string relative = path.Length >= _urlPrefix.Length ? path[_urlPrefix.Length..] : string.Empty;
        relative = relative.TrimStart('/', '\\');

        // 目录式访问（空或以 / 结尾）自动找 index.html。
        if (relative.Length == 0 || relative.EndsWith('/') || relative.EndsWith('\\'))
        {
            relative += "index.html";
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_rootFullPath, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            response.StatusCode = 400;
            return false;
        }

        // 容纳校验：解析后的绝对路径必须仍在根目录内。
        if (!fullPath.StartsWith(_rootFullPath, StringComparison.Ordinal))
        {
            response.StatusCode = 403;
            return false;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists) return true;

        // 隐藏文件拒绝（.env / .git / .config 等）。
        if (info.Name.StartsWith('.'))
        {
            response.StatusCode = 403;
            return false;
        }

        string etag = BuildETag(info);
        response.Headers["ETag"] = etag;

        if (_maxAgeSeconds > 0)
        {
            response.Headers["Cache-Control"] = $"public, max-age={_maxAgeSeconds}";
        }
        else
        {
            // 不设强缓存，但仍允许协商缓存。
            response.Headers["Cache-Control"] = "no-cache";
        }

        string? ifNoneMatch = request.Headers["If-None-Match"];
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag, StringComparison.Ordinal))
        {
            response.StatusCode = 304;
            response.ContentLength64 = 0;
            response.Close();
            return false;
        }

        await response.SendFileAsync(fullPath, asAttachment: false, request, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// 前缀匹配。根前缀 "/" 匹配一切；
    /// 其他前缀要求按段边界匹配，避免 "/assets" 误吃 "/assets-private"。
    /// </summary>
    private bool MatchesPrefix(string path)
    {
        if (_urlPrefix == "/") return true;
        if (!path.StartsWith(_urlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // 允许精确等于去掉尾斜杠的前缀。
            return string.Equals(path, _urlPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>ETag 由修改时间与长度构成：任一变化即视为内容变化。</summary>
    private static string BuildETag(FileInfo info)
        => $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
}
