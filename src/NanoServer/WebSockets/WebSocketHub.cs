using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using NanoServer.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NanoServer.WebSockets;

/// <summary>收到 WebSocket 消息时的处理委托。</summary>
/// <param name="clientId">服务端分配的客户端标识。</param>
/// <param name="message">消息文本。</param>
/// <param name="reply">向该客户端回发文本。</param>
public delegate Task OnWebSocketMessage(string clientId, string message, Func<string, Task> reply);

/// <summary>连接状态变化时回调。</summary>
public delegate Task OnWebSocketConnectionChanged(string clientId, bool connected);

/// <summary>WebSocket 连接选项。</summary>
public sealed class WebSocketOptions
{
    /// <summary>最大并发连接数，超出返回 503。</summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>启用心跳超时检测。关闭时不产生任何后台开销。</summary>
    public bool EnableHeartbeat { get; set; }

    /// <summary>多久没收到任何帧即判定掉线。</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>心跳巡检间隔。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>客户端发送该文本即视为心跳，不转交业务处理器。</summary>
    public string PingText { get; set; } = "ping";

    /// <summary>单帧接收缓冲大小。</summary>
    public int ReceiveBufferSize { get; set; } = 4096;

    /// <summary>单条消息最大字节数，超出即断开，防止内存被撑爆。</summary>
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>
/// WebSocket 连接注册表与会话循环。
/// <para>
/// 设计要点：
/// ① 连接表与活跃时间表分离（两个 ConcurrentDictionary），读取心跳时间不阻塞连接读写；
/// ② 接收缓冲从 <see cref="ArrayPool{T}.Shared"/> 租借，长连接下不产生逐帧 GC；
/// ③ 心跳巡检惰性启动且幂等，不启用心跳就零成本；
/// ④ 单条消息累积超过上限即断开，避免大消息打爆内存。
/// </para>
/// </summary>
public sealed class WebSocketHub : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastActivity = new(StringComparer.Ordinal);
    private readonly WebSocketOptions _options;
    private readonly object _heartbeatLock = new();
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    public WebSocketHub(WebSocketOptions? options = null) => _options = options ?? new WebSocketOptions();

    /// <summary>收到消息时的回调，未设置则仅维持连接（用于纯推送场景）。</summary>
    public OnWebSocketMessage? OnMessage { get; set; }

    /// <summary>连接建立/断开回调。</summary>
    public OnWebSocketConnectionChanged? OnConnectionChanged { get; set; }

    public WebSocketOptions Options => _options;

    /// <summary>日志器。默认 NullLogger，零开销。</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>当前在线客户端标识。</summary>
    public string[] OnlineClients() => _connections.Keys.ToArray();

    public int Count => _connections.Count;

    /// <summary>
    /// 接管一次已升级的 WebSocket 请求。
    /// 调用方应在中间件或路由中判定 <c>request.IsWebSocketRequest</c> 后调用。
    /// </summary>
    public async Task AcceptAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        if (_connections.Count >= _options.MaxConnections)
        {
            NanoLogger.WebSocketRejected(Logger, _options.MaxConnections);
            response.StatusCode = 503;
            response.ContentLength64 = 0;
            response.Close();
            return;
        }

        string clientId = Guid.NewGuid().ToString("N");
        WebSocket? socket = null;
        byte[]? buffer = null;

        try
        {
            WebSocketContext? wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            socket = wsContext.WebSocket;

            if (!_connections.TryAdd(clientId, socket))
            {
                await TryCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Server busy").ConfigureAwait(false);
                return;
            }

            Touch(clientId);
            EnsureHeartbeatRunning();
            NanoLogger.WebSocketConnected(Logger, clientId, _connections.Count);

            if (OnConnectionChanged is not null)
            {
                try { await OnConnectionChanged(clientId, true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* 业务回调异常不应断开连接 */ }
            }

            buffer = ArrayPool<byte>.Shared.Rent(_options.ReceiveBufferSize);
            OnWebSocketMessage? onMessage = OnMessage;

            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, _options.ReceiveBufferSize), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, string.Empty).ConfigureAwait(false);
                    break;
                }

                Touch(clientId);

                string? text = await ReadMessageAsync(socket, buffer, result).ConfigureAwait(false);
                if (text is null) break; // 超限或读失败

                if (text.Length == 0) continue;
                if (string.Equals(text, _options.PingText, StringComparison.OrdinalIgnoreCase)) continue;
                if (onMessage is null) continue;

                try
                {
                    await onMessage(clientId, text, reply => SendAsync(clientId, reply)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 业务处理器抛错不应打死整条连接。
                }
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or WebSocketException or InvalidOperationException)
        {
            // 握手失败或对端已断开。
        }
        finally
        {
            _connections.TryRemove(clientId, out _);
            _lastActivity.TryRemove(clientId, out _);

            if (socket is not null)
            {
                try { socket.Dispose(); } catch (ObjectDisposedException) { }
            }
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);

            NanoLogger.WebSocketDisconnected(Logger, clientId, _connections.Count);

            if (OnConnectionChanged is not null)
            {
                try { await OnConnectionChanged(clientId, false).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// 拼装完整消息。首帧可能只是分片的一部分，需继续接收直到 EndOfMessage。
    /// 累计超过 MaxMessageBytes 时返回 null（调用方断开连接）。
    /// </summary>
    private async Task<string?> ReadMessageAsync(WebSocket socket, byte[] buffer, WebSocketReceiveResult first)
    {
        if (first.EndOfMessage)
        {
            return Encoding.UTF8.GetString(buffer, 0, first.Count);
        }

        using var ms = new MemoryStream();
        ms.Write(buffer, 0, first.Count);

        WebSocketReceiveResult result = first;
        while (!result.EndOfMessage)
        {
            if (ms.Length > _options.MaxMessageBytes)
            {
                NanoLogger.WebSocketMessageTooLarge(Logger, "-");
                await TryCloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "Message too large").ConfigureAwait(false);
                return null;
            }

            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, _options.ReceiveBufferSize), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpListenerException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>向指定客户端发送文本。客户端不存在或已关闭时静默忽略。</summary>
    public async Task SendAsync(string clientId, string message)
    {
        if (string.IsNullOrEmpty(clientId) || message is null) return;
        if (!_connections.TryGetValue(clientId, out WebSocket? socket)) return;
        if (socket.State != WebSocketState.Open) return;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
            Touch(clientId);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or HttpListenerException)
        {
            // 发送失败说明连接已废：移出注册表。
            _connections.TryRemove(clientId, out _);
            _lastActivity.TryRemove(clientId, out _);
        }
    }

    /// <summary>广播文本给所有在线客户端。个别失败不影响其余。</summary>
    public async Task BroadcastAsync(string message)
    {
        if (message is null) return;
        foreach (string id in _connections.Keys.ToArray())
        {
            await SendAsync(id, message).ConfigureAwait(false);
        }
    }

    /// <summary>主动断开指定客户端。</summary>
    public async Task CloseClientAsync(string clientId, string reason = "Server closing")
    {
        if (!_connections.TryRemove(clientId, out WebSocket? socket)) return;
        _lastActivity.TryRemove(clientId, out _);
        await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, reason).ConfigureAwait(false);
    }

    /// <summary>断开所有客户端（停机时调用）。</summary>
    public async Task CloseAllAsync(string reason = "Server closing")
    {
        string[] ids = _connections.Keys.ToArray();
        foreach (string id in ids)
        {
            await CloseClientAsync(id, reason).ConfigureAwait(false);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or HttpListenerException)
        {
            // 对端已消失，忽略。
        }
    }

    private void Touch(string clientId) => _lastActivity[clientId] = DateTime.UtcNow.Ticks;

    /// <summary>惰性启动心跳巡检；重复调用无副作用。</summary>
    private void EnsureHeartbeatRunning()
    {
        if (!_options.EnableHeartbeat) return;

        lock (_heartbeatLock)
        {
            if (_heartbeatTask is not null) return;

            _heartbeatCts = new CancellationTokenSource();
            CancellationToken ct = _heartbeatCts.Token;
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), CancellationToken.None);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        long timeoutTicks = _options.HeartbeatTimeout.Ticks;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long now = DateTime.UtcNow.Ticks;
            foreach (KeyValuePair<string, long> kv in _lastActivity)
            {
                if (ct.IsCancellationRequested) break;
                if (now - kv.Value <= timeoutTicks) continue;

                // 超时：摘出连接并关闭。
                if (_connections.TryRemove(kv.Key, out WebSocket? socket))
                {
                    NanoLogger.WebSocketHeartbeatTimeout(Logger, kv.Key);
                    await TryCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Heartbeat timeout").ConfigureAwait(false);
                    try { socket.Dispose(); } catch (ObjectDisposedException) { }
                }
                _lastActivity.TryRemove(kv.Key, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync("Server shutting down").ConfigureAwait(false);

        lock (_heartbeatLock)
        {
            if (_heartbeatCts is not null)
            {
                try { _heartbeatCts.Cancel(); } catch (ObjectDisposedException) { }
                _heartbeatCts.Dispose();
                _heartbeatCts = null;
            }
            _heartbeatTask = null;
        }

        foreach (WebSocket socket in _connections.Values)
        {
            try { socket.Dispose(); } catch (ObjectDisposedException) { }
        }
        _connections.Clear();
        _lastActivity.Clear();
    }
}
