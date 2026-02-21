using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenClaw.Tests;

internal sealed class TestWebSocket : WebSocket
{
    private readonly ConcurrentQueue<(byte[] Data, WebSocketMessageType Type, bool End)> _receive = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();

    private WebSocketState _state = WebSocketState.Open;

    public IReadOnlyCollection<byte[]> Sent => _sent.ToArray();

    public void QueueReceiveText(string text, bool endOfMessage = true)
        => _receive.Enqueue((System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage));

    public void QueueReceiveBytes(byte[] bytes, bool endOfMessage)
        => _receive.Enqueue((bytes, WebSocketMessageType.Text, endOfMessage));

    public void QueueClose()
        => _receive.Enqueue((Array.Empty<byte>(), WebSocketMessageType.Close, true));

    public override WebSocketCloseStatus? CloseStatus { get; }
    public override string? CloseStatusDescription { get; }
    public override WebSocketState State => _state;
    public override string? SubProtocol { get; }

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_receive.TryDequeue(out var next))
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        if (next.Type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        if (buffer.Array is null)
            throw new InvalidOperationException("Receive buffer has no backing array.");

        Array.Copy(next.Data, 0, buffer.Array, buffer.Offset, next.Data.Length);
        return Task.FromResult(new WebSocketReceiveResult(next.Data.Length, next.Type, next.End));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var copy = buffer.ToArray();
        _sent.Enqueue(copy);
        return Task.CompletedTask;
    }
}
