using LiteServer.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LiteServer.Hosting;

/// <summary>把 LiteServer 注册进 .NET 依赖注入容器的扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 LiteServer 为后台托管服务。
    /// <para>
    /// 之后用 <c>host.Services.GetRequiredService&lt;LiteWebServer&gt;()</c> 取得实例注册路由，
    /// 或直接在 <paramref name="configureServer"/> 回调里完成全部配置。
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.Services.AddLiteServer(o =>
    /// {
    ///     o.Port = 8090;
    ///     o.JwtSecret = "secret";
    ///     o.AnonymousPaths.Add("/api/health");
    /// }, server =>
    /// {
    ///     server.MapGet("/api/health", (req, res) => res.WriteAsync("ok"));
    /// });
    /// await builder.Build().RunAsync();
    /// </code>
    /// </example>
    public static IServiceCollection AddLiteServer(
        this IServiceCollection services,
        Action<LiteServerOptions>? configureOptions = null,
        Action<LiteWebServer>? configureServer = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }

        services.TryAddSingleton<LiteWebServer>(sp =>
        {
            var server = new LiteWebServer();

            // 注入日志器：未注册 logging 时退回 NullLogger，零开销。
            server.Logger = sp.GetService<ILogger<LiteWebServer>>() ?? (ILogger)NullLogger.Instance;
            server.WebSockets.Logger = sp.GetService<ILogger<WebSockets.WebSocketHub>>() ?? (ILogger)NullLogger.Instance;

            var options = sp.GetService<IOptions<LiteServerOptions>>()?.Value;

            if (options is not null)
            {
                ApplyOptions(server, options);
                configureServer?.Invoke(server);
                ApplySecurityAndStatic(server, options);
            }
            else
            {
                configureServer?.Invoke(server);
            }

            return server;
        });

        // 把服务器持有的 WebSocketHub 暴露给容器：后台推送服务需要注入它来广播。
        services.TryAddSingleton<WebSockets.WebSocketHub>(sp =>
            sp.GetRequiredService<LiteWebServer>().WebSockets);

        services.TryAddSingleton<LiteServerHost>(sp =>
        {
            LiteWebServer server = sp.GetRequiredService<LiteWebServer>();
            // 把根容器交给服务器：此后每个请求都会创建独立作用域。
            server.ServiceProvider ??= sp;
            return new LiteServerHost(server, configure: null, logger: sp.GetService<ILogger<LiteServerHost>>());
        });

        services.AddHostedService(sp => sp.GetRequiredService<LiteServerHost>());

        return services;
    }

    /// <summary>把配置对象应用到服务器实例。</summary>
    private static void ApplyOptions(LiteWebServer server, LiteServerOptions options)
    {
        server.Options.Host = options.Host;
        server.Options.Port = options.Port;
        server.Options.EnableWebSocket = options.EnableWebSocket;
        server.Options.JwtSecret = options.JwtSecret;
        server.Options.JwtClockSkew = options.JwtClockSkew;
        server.Options.BlacklistCleanupInterval = options.BlacklistCleanupInterval;

        server.EnableWebSocket = options.EnableWebSocket;
        server.WebSockets.Options.MaxConnections = options.WebSocketMaxConnections;
        server.WebSockets.Options.EnableHeartbeat = options.EnableWebSocketHeartbeat;
        server.WebSockets.Options.HeartbeatTimeout = options.WebSocketHeartbeatTimeout;

        foreach (string path in options.AnonymousPaths)
        {
            server.AllowAnonymous(path);
        }

        if (options.EnableCors)
        {
            server.UseCors(options.CorsAllowedOrigin);
        }

    }

    /// <summary>
    /// 安装鉴权与静态文件中间件，顺序由 <see cref="LiteServerOptions.StaticFilesBeforeAuth"/> 决定。
    /// 两者都必须在 configureServer 注册的路由之前生效——路由不进中间件管线，而是最后一步分派。
    /// </summary>
    private static void ApplySecurityAndStatic(LiteWebServer server, LiteServerOptions options)
    {
        bool hasStatic = !string.IsNullOrEmpty(options.StaticFilePrefix) && !string.IsNullOrEmpty(options.StaticFileRoot);
        bool hasAuth = !string.IsNullOrEmpty(options.JwtSecret);

        // 顺序 A：静态 → 鉴权（公开静态站点）
        if (hasStatic && options.StaticFilesBeforeAuth)
        {
            server.UseStaticFiles(options.StaticFilePrefix!, options.StaticFileRoot!, options.StaticFileMaxAgeSeconds);
        }

        // 顺序 B：鉴权
        if (hasAuth)
        {
            server.UseJwtAuth(options.JwtSecret!, options.BlacklistCleanupInterval, options.JwtClockSkew, server.Logger);
        }

        // 顺序 C：静态（默认安全位置，排在鉴权之后）
        if (hasStatic && !options.StaticFilesBeforeAuth)
        {
            server.UseStaticFiles(options.StaticFilePrefix!, options.StaticFileRoot!, options.StaticFileMaxAgeSeconds, server.Logger);
        }
    }
}
