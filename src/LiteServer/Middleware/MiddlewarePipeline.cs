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
    // 与 RouteTable 同样采用「不可变快照」：注册期加锁替换引用，读取期零锁零分配。
    private readonly object _writeLock = new();
    private volatile MiddlewareDelegate[] _snapshot = Array.Empty<MiddlewareDelegate>();

    public void Use(MiddlewareDelegate middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        lock (_writeLock)
        {
            var next = new MiddlewareDelegate[_snapshot.Length + 1];
            Array.Copy(_snapshot, next, _snapshot.Length);
            next[^1] = middleware;
            _snapshot = next;
        }
    }

    public void Clear()
    {
        lock (_writeLock) _snapshot = Array.Empty<MiddlewareDelegate>();
    }

    public int Count => _snapshot.Length;

    /// <summary>
    /// 依次执行中间件。任一返回 false 即中止，调用方不应再写响应。
    /// </summary>
    public async Task<bool> InvokeAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct = default)
    {
        // 直接读 volatile 引用，没有锁、没有复制。
        MiddlewareDelegate[] snapshot = _snapshot;

        foreach (MiddlewareDelegate mw in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            bool proceed = await mw(request, response).ConfigureAwait(false);
            if (!proceed) return false;
        }
        return true;
    }
}
