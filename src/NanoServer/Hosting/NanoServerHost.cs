using System.Net;
using NanoServer.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NanoServer.Hosting;

/// <summary>
/// 把 <see cref="NanoWebServer"/> 包装为 .NET 通用主机的 <see cref="IHostedService"/>。
/// <para>
/// 这样 NanoServer 就能和依赖注入、配置、日志、优雅停机等 .NET 基础设施共用同一套宿主，
/// 而不必自建 Main 循环。
/// </para>
/// </summary>
public sealed class NanoServerHost : IHostedService, IAsyncDisposable
{
    private readonly NanoWebServer _server;
    private readonly ILogger<NanoServerHost>? _logger;
    private readonly Action<NanoWebServer>? _configure;

    /// <summary>用现成实例构造。</summary>
    public NanoServerHost(NanoWebServer server, Action<NanoWebServer>? configure = null, ILogger<NanoServerHost>? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _configure = configure;
        _logger = logger;
    }

    /// <summary>底层服务器实例，便于注册路由。</summary>
    public NanoWebServer Server => _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _configure?.Invoke(_server);

        await _server.StartAsync(_server.Options.Host, _server.Options.Port).ConfigureAwait(false);

        _logger?.LogInformation("NanoServer 已启动：{Prefix}", _server.Prefix);
        foreach (string addr in NanoWebServer.GetLocalAddresses())
        {
            _logger?.LogInformation("局域网地址：{Address}", addr);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("NanoServer 正在停止…");
        await _server.StopAsync().ConfigureAwait(false);
        _logger?.LogInformation("NanoServer 已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
