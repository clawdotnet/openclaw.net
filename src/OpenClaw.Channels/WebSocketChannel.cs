using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

/// <summary>
/// WebSocket channel adapter — the primary control plane for companion apps.
/// Supports both raw-text and JSON envelope messaging, with per-connection routing.
/// </summary>
public sealed class WebSocketChannel : IChannelAdapter
{
    private readonly WebSocketConfig _config;
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();

    private sealed class ConnectionState
    {
        public required WebSocket Socket { get; init; }
        public string IpKey { get; init; } = "unknown";
        public bool UseJsonEnvelope { get; set; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public RateWindow Rate { get; }

        public ConnectionState(int messagesPerMinute)
        {
            Rate = new RateWindow(messagesPerMinute);
        }
    }

    private sealed class RateWindow
    {
        private readonly int _limit;
        private long _windowMinute;
        private int _count;

        public RateWindow(int limit) => _limit = Math.Max(1, limit);

        public bool TryConsume()
        {
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var currentWindow = Interlocked.Read(ref _windowMinute);

            // Fast path: same window, just increment
            if (minute == currentWindow)
            {
                var newCount = Interlocked.Increment(ref _count);
                return newCount <= _limit;
            }

            // Window changed - need to reset
            // Use CompareExchange to handle race conditions
            if (Interlocked.CompareExchange(ref _windowMinute, minute, currentWindow) == currentWindow)
            {
                // We won the race to update the window
                Interlocked.Exchange(ref _count, 1);
                return true;
            }

            // Someone else updated the window, retry
            var count = Interlocked.Increment(ref _count);
            return count <= _limit;
        }
    }

    public WebSocketChannel(WebSocketConfig config) => _config = config;

    public string ChannelId => "websocket";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask; // Kestrel manages the listener

    public async Task HandleConnectionAsync(WebSocket ws, string clientId, IPAddress? remoteIp, CancellationToken ct)
    {
        if (!TryAddConnection(clientId, ws, remoteIp, out var state))
        {
            await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "connection limit exceeded", ct);
            return;
        }

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReceiveFullTextMessageAsync(ws, ct);
                if (text is null)
                    break;

                if (!state.Rate.TryConsume())
                {
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "rate limit exceeded", ct);
                    break;
                }

                var (userText, messageId, replyToMessageId, isEnvelope) = TryParseUserMessage(text);
                if (isEnvelope)
                    state.UseJsonEnvelope = true;

                var msg = new InboundMessage
                {
                    ChannelId = ChannelId,
                    SenderId = clientId,
                    Text = userText,
                    MessageId = messageId,
                    ReplyToMessageId = replyToMessageId
                };

                if (OnMessageReceived is not null)
                    await OnMessageReceived(msg, ct);
            }
        }
        finally
        {
            RemoveConnection(clientId, state);
        }
    }

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(message.RecipientId, out var state))
            return;

        var payload = state.UseJsonEnvelope
            ? JsonSerializer.SerializeToUtf8Bytes(
                new WsServerEnvelope
                {
                    Type = "assistant_message",
                    Text = message.Text,
                    InReplyToMessageId = message.ReplyToMessageId
                },
                CoreJsonContext.Default.WsServerEnvelope)
            : Encoding.UTF8.GetBytes(message.Text);

        await state.SendLock.WaitAsync(ct);
        try
        {
            if (state.Socket.State == WebSocketState.Open)
                await state.Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    /// <summary>
    /// Returns true if the client is using JSON envelope mode (eligible for streaming).
    /// </summary>
    public bool IsClientUsingEnvelopes(string clientId) =>
        _connections.TryGetValue(clientId, out var state) && state.UseJsonEnvelope;

    /// <summary>
    /// Sends a streaming event to a connected client. Only works for envelope-mode clients.
    /// For raw-text clients, use <see cref="SendAsync"/> with the complete message.
    /// </summary>
    public async ValueTask SendStreamEventAsync(
        string recipientId, string envelopeType, string? text, string? inReplyToMessageId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(recipientId, out var state))
            return;

        // Raw-text clients don't support streaming events
        if (!state.UseJsonEnvelope)
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new WsServerEnvelope
            {
                Type = envelopeType,
                Text = text,
                InReplyToMessageId = inReplyToMessageId
            },
            CoreJsonContext.Default.WsServerEnvelope);

        await state.SendLock.WaitAsync(ct);
        try
        {
            if (state.Socket.State == WebSocketState.Open)
                await state.Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _connections)
        {
            try
            {
                await CloseIfOpenAsync(kvp.Value.Socket, WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }

        _connections.Clear();
        _connectionsPerIp.Clear();
    }

    internal bool TryAddConnectionForTest(string clientId, WebSocket ws, IPAddress? remoteIp, bool useJsonEnvelope)
    {
        if (!TryAddConnection(clientId, ws, remoteIp, out var state))
            return false;

        state.UseJsonEnvelope = useJsonEnvelope;
        return true;
    }

    private bool TryAddConnection(string clientId, WebSocket ws, IPAddress? remoteIp, out ConnectionState state)
    {
        state = new ConnectionState(_config.MessagesPerMinutePerConnection)
        {
            Socket = ws,
            IpKey = remoteIp?.ToString() ?? "unknown"
        };

        if (_connections.Count >= _config.MaxConnections)
            return false;

        var perIp = _connectionsPerIp.AddOrUpdate(state.IpKey, 1, (_, c) => c + 1);
        if (perIp > _config.MaxConnectionsPerIp)
        {
            _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
            return false;
        }

        if (!_connections.TryAdd(clientId, state))
        {
            _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
            return false;
        }

        return true;
    }

    private void RemoveConnection(string clientId, ConnectionState state)
    {
        _connections.TryRemove(clientId, out _);
        _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
        try { state.SendLock.Dispose(); } catch { /* ignore */ }
        try { state.Socket.Dispose(); } catch { /* ignore */ }
    }

    private async Task<string?> ReceiveFullTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var total = 0;
        WebSocketMessageType? messageType = null;

        try
        {
            while (true)
            {
                if (total >= buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Min(_config.MaxMessageBytes, buffer.Length * 2));
                    Buffer.BlockCopy(buffer, 0, grown, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var memory = buffer.AsMemory(total, buffer.Length - total);
                var result = await ws.ReceiveAsync(memory, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                messageType ??= result.MessageType;

                total += result.Count;

                if (total > _config.MaxMessageBytes)
                {
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                    return null;
                }

                if (result.EndOfMessage)
                    break;
            }

            if (messageType != WebSocketMessageType.Text)
                return null;

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (string Text, string? MessageId, string? ReplyToMessageId, bool IsEnvelope) TryParseUserMessage(string payload)
    {
        const int MaxExtractedTextLength = 1_000_000; // 1MB text limit after JSON parsing

        var span = payload.AsSpan();
        var i = 0;
        while (i < span.Length && char.IsWhiteSpace(span[i]))
            i++;

        if (i >= span.Length || span[i] != '{')
            return (payload, null, null, false);

        try
        {
            var env = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsClientEnvelope);
            if (env is { Type: "user_message", Text: not null })
            {
                // Validate extracted text length to prevent memory pressure
                if (env.Text.Length > MaxExtractedTextLength)
                    return (env.Text[..MaxExtractedTextLength], env.MessageId, env.ReplyToMessageId, true);
                
                return (env.Text, env.MessageId, env.ReplyToMessageId, true);
            }
        }
        catch
        {
            // fall through to raw
        }

        return (payload, null, null, false);
    }

    private static ValueTask CloseIfOpenAsync(WebSocket ws, WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            return ValueTask.CompletedTask;

        return new ValueTask(ws.CloseAsync(status, description, ct));
    }
}
