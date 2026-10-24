using System.Net;

namespace NanoServer;

/// <summary>
/// 当前请求的依赖注入作用域。
/// <para>
/// 为什么需要它：路由处理器的签名是 <c>(HttpListenerRequest, HttpListenerResponse)</c>，
/// 拿不到 <see cref="IServiceProvider"/>；而 <c>HttpListenerRequest</c> 在 .NET Core 上
/// 也没有可挂载自定义数据的 <c>Items</c> 集合。这里用 <see cref="AsyncLocal{T}"/> 承载
/// 当前逻辑调用链的作用域，异步传播语义与 ASP.NET Core 的 <c>HttpContext.RequestServices</c> 一致。
/// </para>
/// <para>
/// 注意：<see cref="AsyncLocal{T}"/> 按逻辑调用链隔离，同一条请求内多层 await 仍能取到同一个值，
/// 并发请求之间互不串扰。
/// </para>
/// </summary>
public static class RequestScope
{
    private static readonly AsyncLocal<IServiceProvider?> CurrentProvider = new();

    /// <summary>当前请求的服务提供者。未启用依赖注入时为 null。</summary>
    public static IServiceProvider? Current
    {
        get => CurrentProvider.Value;
        internal set => CurrentProvider.Value = value;
    }

    /// <summary>
    /// 从当前请求作用域解析服务。
    /// 未启用 DI 或服务未注册时返回 null。
    /// </summary>
    public static T? Resolve<T>() where T : class
        => Current?.GetService(typeof(T)) as T;

    /// <summary>从当前请求作用域解析必需服务。未注册则抛异常。</summary>
    public static T Require<T>() where T : class
        => Current?.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException(
               $"服务 {typeof(T).FullName} 未注册，或当前请求不在依赖注入作用域内。");
}

/// <summary>供处理器便捷取服务的扩展。</summary>
public static class RequestScopeExtensions
{
    /// <summary>取当前请求的服务提供者。</summary>
    public static IServiceProvider Services(this HttpListenerRequest request)
    {
        _ = request;
        return RequestScope.Current
               ?? throw new InvalidOperationException(
                   "当前请求没有依赖注入作用域。请用 AddNanoServer 注册（通用主机模式），或手动设置 NanoWebServer.ServiceProvider。");
    }

    /// <summary>取当前请求作用域内的服务；未注册返回 null。</summary>
    public static T? GetService<T>(this HttpListenerRequest request) where T : class
    {
        _ = request;
        return RequestScope.Resolve<T>();
    }

    /// <summary>取当前请求作用域内的必需服务。</summary>
    public static T RequireService<T>(this HttpListenerRequest request) where T : class
    {
        _ = request;
        return RequestScope.Require<T>();
    }
}
