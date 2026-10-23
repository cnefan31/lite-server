using System.Net;

namespace LiteServer.Middleware;

/// <summary>
/// 中间件：返回 true 放行到下一个环节，返回 false 表示已自行终结请求。
/// </summary>
public delegate Task<bool> MiddlewareDelegate(HttpListenerRequest request, HttpListenerResponse response);

/// <summary>
/// 中间件管线。按注册顺序
/// <b>从前往后</b> 执行：先注册的是外层，可决定是否放行到内层。
/// <para>
/// 返回 true 表示继续，false 表示已自行终结请求。
/// </para>
/// </summary>
public sealed class MiddlewarePipeline
{
    private readonly List<MiddlewareDelegate> _middlewares = new();
    private readonly object _lock = new();

    public void Use(MiddlewareDelegate middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_lock) _middlewares.Add(middleware);
    }

    public void Clear()
    {
        lock (_lock) _middlewares.Clear();
    }

    public int Count
    {
        get { lock (_lock) return _middlewares.Count; }
    }

    /// <summary>
    /// 依次执行中间件。任一返回 false 即中止，调用方不应再写响应。
    /// </summary>
    public async Task<bool> InvokeAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct = default)
    {
        MiddlewareDelegate[] snapshot;
        lock (_lock) snapshot = _middlewares.ToArray();

        foreach (MiddlewareDelegate mw in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            bool proceed = await mw(request, response).ConfigureAwait(false);
            if (!proceed) return false;
        }
        return true;
    }
}
