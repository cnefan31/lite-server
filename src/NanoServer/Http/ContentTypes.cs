namespace NanoServer.Http;

/// <summary>
/// 内置 MIME 类型解析。
/// 跨平台行为一致：不依赖操作系统注册表，同一扩展名在 Windows/Linux/macOS 上返回同一结果。
/// </summary>
public static class ContentTypes
{
    public const string Json = "application/json; charset=utf-8";
    public const string Xml = "application/xml; charset=utf-8";
    public const string Plain = "text/plain; charset=utf-8";
    public const string Html = "text/html; charset=utf-8";
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";
    public const string MultipartFormData = "multipart/form-data";
    public const string OctetStream = "application/octet-stream";
    public const string EventStream = "text/event-stream; charset=utf-8";

    /// <summary>文本类扩展名：追加 charset 才有意义，二进制类型不加。</summary>
    private static readonly HashSet<string> TextualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".xml", ".txt", ".md", ".csv",
        ".svg", ".m3u8", ".ts", ".map", ".webmanifest", ".ico",
    };

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文本与文档
        [".html"] = "text/html", [".htm"] = "text/html",
        [".css"] = "text/css", [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".json"] = "application/json", [".map"] = "application/json",
        [".xml"] = "application/xml", [".txt"] = "text/plain", [".md"] = "text/markdown",
        [".csv"] = "text/csv", [".webmanifest"] = "application/manifest+json",

        // 图片
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",

        // 字体
        [".woff"] = "font/woff", [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".eot"] = "application/vnd.ms-fontobject",

        // 音视频
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg",
        [".mov"] = "video/quicktime", [".m4v"] = "video/x-m4v",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4", [".flac"] = "audio/flac", [".aac"] = "audio/aac",
        // 流媒体播放列表与分片
        [".m3u8"] = "application/vnd.apple.mpegurl", [".ts"] = "video/mp2t",

        // 归档与二进制
        [".zip"] = "application/zip", [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar", [".7z"] = "application/x-7z-compressed",
        [".pdf"] = "application/pdf", [".wasm"] = "application/wasm",
        [".bin"] = "application/octet-stream", [".exe"] = "application/octet-stream",
    };

    /// <summary>
    /// 根据扩展名（含前导点）取 MIME。未知类型回退为安全的 application/octet-stream，
    /// 避免浏览器把未知内容当 HTML 渲染（XSS 面）。
    /// </summary>
    public static string Get(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return OctetStream;
        if (!ext.StartsWith('.')) ext = "." + ext;

        if (!Map.TryGetValue(ext, out string? mime)) return OctetStream;
        return TextualExtensions.Contains(ext) ? mime + "; charset=utf-8" : mime;
    }
}
