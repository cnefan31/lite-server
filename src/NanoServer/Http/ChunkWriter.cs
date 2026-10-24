using System.Net;
using System.Text;

namespace NanoServer.Http;

/// <summary>
/// 长连接分块推送。用于 SSE、日志流、音视频分片等需要服务端持续写出的场景。
/// <para>
/// 约定：调用方负责在业务结束时关闭响应。本类的每个写入方法在客户端断开时静默返回，
/// 不会抛异常打断业务循环——浏览器关页、切网都会造成这种断开，属常态。
/// </para>
/// </summary>
public static class ChunkWriter
{
    /// <summary>写入一段文本。</summary>
    public static async Task WriteTextAsync(this HttpListenerResponse response, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        await response.WriteBytesAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
    }

    /// <summary>写入原始字节。</summary>
    public static async Task WriteBytesAsync(this HttpListenerResponse response, byte[] data, CancellationToken ct = default)
    {
        if (data is null || data.Length == 0) return;

        try
        {
            if (!(response.OutputStream?.CanWrite ?? false)) return;
            if (!response.SendChunked) response.SendChunked = true;

            await response.OutputStream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
            // 客户端已断开：静默结束本次写入，由调用方的 finally 关闭响应。
        }
    }

    /// <summary>把来源流分块转发到响应，内存占用恒定为一个 buffer。</summary>
    public static async Task WriteStreamAsync(
        this HttpListenerResponse response,
        Stream source,
        int bufferSize = 8192,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (bufferSize <= 0) bufferSize = 8192;

        try
        {
            if (!(response.OutputStream?.CanWrite ?? false)) return;
            if (!response.SendChunked) response.SendChunked = true;

            byte[] buffer = new byte[bufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
            // 同上：传输中断不向上抛。
        }
    }
}
