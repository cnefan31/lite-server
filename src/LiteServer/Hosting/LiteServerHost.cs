using System.Net;
using LiteServer.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiteServer.Hosting;

/// <summary>
/// 把 <see cref="LiteWebServer"/> 包装为 .NET 通用主机的 <see cref="IHostedService"/>。
/// <para>
/// 这样 LiteServer 就能和依赖注入、配置、日志、优雅停机等 .NET 基础设施共用同一套宿主，
/// 而不必自建 Main 循环。
/// </para>
/// </summary>
public sealed class LiteServerHost : IHostedService, IAsyncDisposable
{
    private readonly LiteWebServer _server;
    private readonly ILogger<LiteServerHost>? _logger;
    private readonly Action<LiteWebServer>? _configure;

    /// <summary>用现成实例构造。</summary>
    public LiteServerHost(LiteWebServer server, Action<LiteWebServer>? configure = null, ILogger<LiteServerHost>? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _configure = configure;
        _logger = logger;
    }

    /// <summary>底层服务器实例，便于注册路由。</summary>
    public LiteWebServer Server => _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _configure?.Invoke(_server);

        await _server.StartAsync(_server.Options.Host, _server.Options.Port).ConfigureAwait(false);

        _logger?.LogInformation("LiteServer 已启动：{Prefix}", _server.Prefix);
        foreach (string addr in LiteWebServer.GetLocalAddresses())
        {
            _logger?.LogInformation("局域网地址：{Address}", addr);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("LiteServer 正在停止…");
        await _server.StopAsync().ConfigureAwait(false);
        _logger?.LogInformation("LiteServer 已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
