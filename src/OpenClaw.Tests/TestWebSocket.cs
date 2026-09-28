using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace OpenClaw.Tests;

internal sealed class TestWebSocket : WebSocket
{
    private readonly ConcurrentQueue<(byte[] Data, WebSocketMessageType Type, bool End)> _receive = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();
    private readonly ConcurrentQueue<Exception> _receiveExceptions = new();
    private bool _blockReceiveUntilCancelled;
    private TaskCompletionSource<bool>? _sendStarted;
    private TaskCompletionSource<bool>? _sendRelease;

    private WebSocketState _state = WebSocketState.Open;
    private (WebSocketCloseStatus? Status, string? Description) _queuedClose;

    public IReadOnlyCollection<byte[]> Sent => _sent.ToArray();

    public void QueueReceiveText(string text, bool endOfMessage = true)
        => _receive.Enqueue((System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage));

    public void QueueReceiveBytes(byte[] bytes, bool endOfMessage)
        => _receive.Enqueue((bytes, WebSocketMessageType.Text, endOfMessage));

    public void QueueClose(WebSocketCloseStatus? status = null, string? description = null)
    {
        _queuedClose = (status, description);
        _receive.Enqueue((Array.Empty<byte>(), WebSocketMessageType.Close, true));
    }

    public void QueueReceiveException(Exception exception)
        => _receiveExceptions.Enqueue(exception);

    public void BlockReceiveUntilCancelled()
        => _blockReceiveUntilCancelled = true;

    public void BlockSendUntilReleased()
    {
        _sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sendRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitForSendToStartAsync()
        => _sendStarted?.Task ?? Task.CompletedTask;

    public void ReleaseBlockedSend()
        => _sendRelease?.TrySetResult(true);

    public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.CloseReceived ? _queuedClose.Status : null;
    public override string? CloseStatusDescription => _state == WebSocketState.CloseReceived ? _queuedClose.Description : null;
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
        if (_receiveExceptions.TryDequeue(out var exception))
            return Task.FromException<WebSocketReceiveResult>(exception);

        if (_blockReceiveUntilCancelled)
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith<WebSocketReceiveResult>(_ => throw new OperationCanceledException(cancellationToken), cancellationToken);

        if (!_receive.TryDequeue(out var next))
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        if (next.Type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _queuedClose.Status, _queuedClose.Description));
        }

        if (buffer.Array is null)
            throw new InvalidOperationException("Receive buffer has no backing array.");

        Array.Copy(next.Data, 0, buffer.Array, buffer.Offset, next.Data.Length);
        return Task.FromResult(new WebSocketReceiveResult(next.Data.Length, next.Type, next.End));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (_sendRelease is not null)
        {
            _sendStarted?.TrySetResult(true);
            return WaitAndSendAsync(buffer, cancellationToken);
        }

        if (_state != WebSocketState.Open)
            throw new ObjectDisposedException(nameof(TestWebSocket));

        var copy = buffer.ToArray();
        _sent.Enqueue(copy);
        return Task.CompletedTask;
    }

    private async Task WaitAndSendAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await _sendRelease!.Task.WaitAsync(cancellationToken);
        if (_state != WebSocketState.Open)
            throw new ObjectDisposedException(nameof(TestWebSocket));

        var copy = buffer.ToArray();
        _sent.Enqueue(copy);
    }
}
