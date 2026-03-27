using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Uses stdio for bootstrap and shutdown fallback, with socket transport for steady-state traffic.
/// </summary>
public sealed class HybridBridgeTransport : IBridgeTransport
{
    private readonly StdioBridgeTransport _bootstrap;
    private readonly SocketBridgeTransport _socket;
    private readonly ILogger _logger;
    private Action<BridgeNotification>? _currentHandler;
    private volatile bool _useSocket;

    public HybridBridgeTransport(
        string socketPath,
        string? socketDirectory,
        bool ownsSocketDirectory,
        string authToken,
        ILogger logger,
        RuntimeMetrics? metrics = null)
    {
        _logger = logger;
        _bootstrap = new StdioBridgeTransport(logger);
        _socket = new SocketBridgeTransport(socketPath, socketDirectory, ownsSocketDirectory, authToken, logger, metrics);
    }

    public Task PrepareAsync(CancellationToken ct)
        => _socket.PrepareAsync(ct);

    public async Task StartAsync(Process process, CancellationToken ct)
    {
        await _bootstrap.StartAsync(process, ct);
        await _socket.StartAsync(process, ct);
    }

    public void UseSocketTransport()
    {
        _useSocket = true;
        _bootstrap.SetNotificationHandler(null!); // suppress bootstrap notifications
    }

    public void SetNotificationHandler(Action<BridgeNotification> handler)
    {
        _currentHandler = handler;
        if (_useSocket)
        {
            _socket.SetNotificationHandler(handler);
        }
        else
        {
            _bootstrap.SetNotificationHandler(handler);
            _socket.SetNotificationHandler(handler);
        }
    }

    public Task SendRequestAsync(string method, JsonElement? parameters, CancellationToken ct)
        => SendAndWaitAsync(method, parameters, ct);

    public async Task<BridgeResponse> SendAndWaitAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        if (!_useSocket)
            return await _bootstrap.SendAndWaitAsync(method, parameters, ct);

        try
        {
            return await _socket.SendAndWaitAsync(method, parameters, ct);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException
                                    || string.Equals(method, "shutdown", StringComparison.Ordinal))
        {
            _logger.LogWarning(ex, "Socket transport failed for '{Method}', falling back to stdio", method);
            _useSocket = false;
            if (_currentHandler is not null)
                _bootstrap.SetNotificationHandler(_currentHandler);
            return await _bootstrap.SendAndWaitAsync(method, parameters, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _socket.DisposeAsync();
        await _bootstrap.DisposeAsync();
    }
}
