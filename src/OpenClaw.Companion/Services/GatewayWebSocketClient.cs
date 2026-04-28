using System.Net.WebSockets;

namespace OpenClaw.Companion.Services;

public sealed class GatewayWebSocketClient : IAsyncDisposable
{
    private readonly OpenClaw.Client.OpenClawWebSocketClient _inner;

    public GatewayWebSocketClient(int maxMessageBytes = 256 * 1024)
    {
        _inner = new OpenClaw.Client.OpenClawWebSocketClient(maxMessageBytes);
        _inner.OnTextMessage += text => OnTextMessage?.Invoke(text);
        _inner.OnEnvelopeReceived += envelope => OnEnvelopeReceived?.Invoke(envelope);
        _inner.OnError += error => OnError?.Invoke(error);
    }

    public bool IsConnected
        => _inner.IsConnected;

    public event Action<string>? OnTextMessage;
    public event Action<OpenClaw.Core.Models.WsServerEnvelope>? OnEnvelopeReceived;
    public event Action<string>? OnError;

    public async Task ConnectAsync(Uri wsUri, string? bearerToken, CancellationToken ct)
        => await _inner.ConnectAsync(wsUri, bearerToken, ct);

    public async Task DisconnectAsync(CancellationToken ct)
        => await _inner.DisconnectAsync(ct);

    public async Task SendUserMessageAsync(string text, string? messageId, string? replyToMessageId, CancellationToken ct)
        => await _inner.SendUserMessageAsync(text, messageId, replyToMessageId, ct);

    public async Task SendEnvelopeAsync(OpenClaw.Core.Models.WsClientEnvelope envelope, CancellationToken ct)
        => await _inner.SendEnvelopeAsync(envelope, ct);

    public async ValueTask DisposeAsync()
    {
        try { await _inner.DisposeAsync(); } catch { }
    }

    internal void SetConnectedSocketForTest(WebSocket ws)
    {
        var method = typeof(OpenClaw.Client.OpenClawWebSocketClient).GetMethod(
            "SetConnectedSocketForTest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method?.Invoke(_inner, [ws]);
    }
}
