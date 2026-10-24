using System.Net;

namespace NanoServer.Http;

/// <summary>文件下载与上传。下载支持 HTTP Range（视频拖动/断点续传），上传为流式落盘。</summary>
public static class FileTransfer
{
    private const int StreamBufferSize = 8192;

    /// <summary>
    /// 智能发送文件。
    /// <para>
    /// 传入 <paramref name="request"/> 且其带 `Range: bytes=...` 时，返回 206 分段响应并附带
    /// `Content-Range` / `Accept-Ranges`，浏览器的视频控件才能拖动进度条。
    /// 不传 request 则总是发送完整文件。
    /// </para>
    /// </summary>
    /// <param name="response">要写入的响应。</param>
    /// <param name="filePath">本地文件绝对或相对路径。</param>
    /// <param name="asAttachment">true 强制下载（Content-Disposition: attachment）；false 允许浏览器内联预览。</param>
    /// <param name="request">当前请求。传入后可识别 Range 头以支持分段响应。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task SendFileAsync(
        this HttpListenerResponse response,
        string filePath,
        bool asAttachment = false,
        HttpListenerRequest? request = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            await response.WriteErrorAsync(404, "Not Found", ct).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(filePath);
        long totalLength = info.Length;
        string contentType = ContentTypes.Get(info.Extension);

        // filename* 走 RFC 5987，中文文件名才不会乱码。
        string encodedName = Uri.EscapeDataString(info.Name);
        response.Headers["Content-Disposition"] =
            $"{(asAttachment ? "attachment" : "inline")}; filename=\"{encodedName}\"; filename*=UTF-8''{encodedName}";
        response.Headers["Accept-Ranges"] = "bytes";

        string? rangeHeader = request?.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            if (await TrySendRangeAsync(response, filePath, rangeHeader, totalLength, contentType, ct).ConfigureAwait(false))
            {
                return;
            }
            // Range 头无法解析时退回完整发送（RFC 7233 允许忽略非法 Range）。
        }

        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentLength64 = totalLength;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, useAsync: true);
            await CopyAsync(fs, response.OutputStream, totalLength, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>处理单区间 Range 请求。返回 false 表示该 Range 头不合法、需回退整体发送。</summary>
    private static async Task<bool> TrySendRangeAsync(
        HttpListenerResponse response,
        string filePath,
        string rangeHeader,
        long totalLength,
        string contentType,
        CancellationToken ct)
    {
        string spec = rangeHeader["bytes=".Length..].Trim();

        // 多区间（含逗号）不支持：按整体发送处理。单区间足以覆盖视频拖动。
        if (spec.Contains(',')) return false;

        int dash = spec.IndexOf('-');
        if (dash < 0) return false;

        string startText = spec[..dash].Trim();
        string endText = spec[(dash + 1)..].Trim();

        long start;
        long end;

        if (startText.Length == 0)
        {
            // "bytes=-500"：取末尾 500 字节。
            if (!long.TryParse(endText, out long suffix) || suffix <= 0) return false;
            suffix = Math.Min(suffix, totalLength);
            start = totalLength - suffix;
            end = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0) return false;
            if (endText.Length == 0)
            {
                end = totalLength - 1;
            }
            else if (!long.TryParse(endText, out end) || end < start)
            {
                return false;
            }
            if (start >= totalLength)
            {
                // 起止越界：416 Range Not Satisfiable，并在 Content-Range 告知真实长度。
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{totalLength}";
                response.ContentLength64 = 0;
                response.Close();
                return true;
            }
            if (end >= totalLength) end = totalLength - 1;
        }

        long length = end - start + 1;

        response.StatusCode = 206;
        response.ContentType = contentType;
        response.ContentLength64 = length;
        response.Headers["Content-Range"] = $"bytes {start}-{end}/{totalLength}";

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, useAsync: true);
            fs.Seek(start, SeekOrigin.Begin);
            await CopyAsync(fs, response.OutputStream, length, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
        return true;
    }

    /// <summary>精确转发指定字节数。</summary>
    private static async Task CopyAsync(Stream source, Stream target, long count, CancellationToken ct)
    {
        byte[] buffer = new byte[StreamBufferSize];
        while (count > 0)
        {
            int want = (int)Math.Min(buffer.Length, count);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            count -= read;
        }
    }

    /// <summary>发送任意流。isLive 为 true 时启用 Chunked 且不打长度（直播/无限流）。</summary>
    public static async Task SendStreamAsync(
        this HttpListenerResponse response,
        Stream source,
        string contentType,
        bool isLive = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            response.ContentType = contentType;
            if (isLive)
            {
                response.SendChunked = true;
                response.Headers["Cache-Control"] = "no-cache";
                await response.WriteStreamAsync(source, StreamBufferSize, ct).ConfigureAwait(false);
            }
            else
            {
                if (source.CanSeek) response.ContentLength64 = source.Length - source.Position;
                await CopyAsync(source, response.OutputStream, source.CanSeek ? source.Length - source.Position : long.MaxValue, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>
    /// 流式接收上传并落盘。全程不把文件读进内存。
    /// </summary>
    /// <param name="request">要读取的请求。</param>
    /// <param name="savePath">落盘路径，目录不存在会自动创建。</param>
    /// <param name="onProgress">进度回调：(已接收字节, 总字节)。总字节未知时（分块传输）为 -1。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功写盘返回 true。</returns>
    public static async Task<bool> SaveFileAsync(
        this HttpListenerRequest request,
        string savePath,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(savePath)) return false;

        string? dir = Path.GetDirectoryName(Path.GetFullPath(savePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        long total = request.ContentLength64;
        long received = 0;

        try
        {
            await using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true);
            byte[] buffer = new byte[StreamBufferSize];
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                onProgress?.Invoke(received, total);
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
            // 写盘失败或连接中断：删除半成品，避免留下损坏文件。
            TryDelete(savePath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
