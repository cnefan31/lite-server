using System.Net;
using System.Text;

namespace NanoServer.Http;

/// <summary>HttpListenerResponse 的写入扩展。写完后统一关闭响应流。</summary>
public static class ResponseExtensions
{
    /// <summary>
    /// 写出字符串响应并关闭输出流。
    /// 小于 4KB 的内容不显式 Flush —— HttpListener 关闭时会自动冲刷，省一次 syscall。
    /// </summary>
    public static async Task WriteAsync(
        this HttpListenerResponse response,
        string content,
        string? contentType = null,
        int statusCode = 200,
        CancellationToken ct = default)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(content ?? string.Empty);

        response.StatusCode = statusCode;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentType = contentType ?? ContentTypes.Plain;
        response.ContentLength64 = buffer.Length;

        try
        {
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (buffer.Length > 4096)
            {
                await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>写出 JSON 响应并关闭输出流。调用方负责序列化，库本身不绑定序列化器。</summary>
    public static Task WriteJsonAsync(this HttpListenerResponse response, string json, int statusCode = 200, CancellationToken ct = default)
        => response.WriteAsync(json, ContentTypes.Json, statusCode, ct);

    /// <summary>写出 204 No Content 并关闭。</summary>
    public static void WriteNoContent(this HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = 204;
            response.ContentLength64 = 0;
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>写出错误响应（纯文本），用于内部兜底。</summary>
    public static Task WriteErrorAsync(this HttpListenerResponse response, int statusCode, string message, CancellationToken ct = default)
        => response.WriteAsync(message, ContentTypes.Plain, statusCode, ct);

    /// <summary>添加或覆盖响应头。</summary>
    public static void SetHeader(this HttpListenerResponse response, string name, string value)
    {
        response.Headers[name] = value;
    }
}
