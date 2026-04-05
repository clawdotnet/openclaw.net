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
    private int _connectionCount;

    private sealed class ConnectionState
    {
        public required WebSocket Socket { get; init; }
        public string IpKey { get; init; } = "unknown";
        public bool UseJsonEnvelope { get; set; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public object LifecycleGate { get; } = new();
        public bool Removed { get; set; }
        public bool SendLockDisposed { get; set; }
        public int SendReservations { get; set; }
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
        private readonly object _gate = new();

        public RateWindow(int limit) => _limit = Math.Max(1, limit);

        public bool TryConsume()
        {
            lock (_gate)
            {
                var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                if (minute != _windowMinute)
                {
                    _windowMinute = minute;
                    _count = 0;
                }

                _count++;
                return _count <= _limit;
            }
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

                var parsed = TryParseClientEnvelope(text);
                if (parsed.IsEnvelope)
                    state.UseJsonEnvelope = true;

                if (!state.Rate.TryConsume())
                {
                    if (state.UseJsonEnvelope)
                    {
                        await SendEnvelopeToStateAsync(
                            state,
                            new WsServerEnvelope
                            {
                                Type = "error",
                                Text = "Rate limit exceeded"
                            },
                            ct);
                    }

                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "rate limit exceeded", ct);
                    break;
                }

                var msg = new InboundMessage
                {
                    ChannelId = ChannelId,
                    SenderId = clientId,
                    SessionId = parsed.SessionId,
                    Type = parsed.Type,
                    Text = parsed.Text ?? "",
                    MessageId = parsed.MessageId,
                    ReplyToMessageId = parsed.ReplyToMessageId,
                    ApprovalId = parsed.ApprovalId,
                    Approved = parsed.Approved,
                    RequestCancellation = ct
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

        await SendPayloadAsync(message.RecipientId, state, payload, ct);
    }

    public async ValueTask SendEnvelopeAsync(string recipientId, WsServerEnvelope envelope, CancellationToken ct)
    {
        if (!_connections.TryGetValue(recipientId, out var state))
            return;

        if (!state.UseJsonEnvelope)
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, CoreJsonContext.Default.WsServerEnvelope);

        await SendPayloadAsync(recipientId, state, payload, ct);
    }

    private async ValueTask SendEnvelopeToStateAsync(ConnectionState state, WsServerEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, CoreJsonContext.Default.WsServerEnvelope);

        await SendPayloadAsync(recipientId: null, state, payload, ct);
    }

    private async ValueTask SendPayloadAsync(string? recipientId, ConnectionState state, byte[] payload, CancellationToken ct)
    {
        if (!TryReserveSend(state))
            return;

        var acquired = false;
        try
        {
            await state.SendLock.WaitAsync(ct);
            acquired = true;

            if (recipientId is not null &&
                (!_connections.TryGetValue(recipientId, out var current) || !ReferenceEquals(current, state)))
            {
                return;
            }

            if (state.Removed || state.Socket.State != WebSocketState.Open)
                return;

            await state.Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
        }
        catch (ObjectDisposedException)
        {
            // Client disconnected mid-send.
        }
        catch (WebSocketException)
        {
            // Client disconnected mid-send.
        }
        catch (InvalidOperationException)
        {
            // Socket is no longer usable.
        }
        finally
        {
            if (acquired)
                state.SendLock.Release();
            CompleteSendReservation(state);
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

        await SendPayloadAsync(recipientId, state, payload, ct);
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

            RemoveConnection(kvp.Key, kvp.Value);
        }

        _connections.Clear();
        _connectionsPerIp.Clear();
        Interlocked.Exchange(ref _connectionCount, 0);
    }

    internal bool TryAddConnectionForTest(string clientId, WebSocket ws, IPAddress? remoteIp, bool useJsonEnvelope)
    {
        if (!TryAddConnection(clientId, ws, remoteIp, out var state))
            return false;

        state.UseJsonEnvelope = useJsonEnvelope;
        return true;
    }

    internal void RemoveConnectionForTest(string clientId)
    {
        if (_connections.TryGetValue(clientId, out var state))
            RemoveConnection(clientId, state);
    }

    private bool TryAddConnection(string clientId, WebSocket ws, IPAddress? remoteIp, out ConnectionState state)
    {
        state = null!;

        var newCount = Interlocked.Increment(ref _connectionCount);
        if (newCount > _config.MaxConnections)
        {
            Interlocked.Decrement(ref _connectionCount);
            return false;
        }

        var ipKey = remoteIp?.ToString() ?? "unknown";
        state = new ConnectionState(_config.MessagesPerMinutePerConnection)
        {
            Socket = ws,
            IpKey = ipKey
        };

        var perIp = _connectionsPerIp.AddOrUpdate(state.IpKey, 1, (_, c) => c + 1);
        if (perIp > _config.MaxConnectionsPerIp)
        {
            _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
            Interlocked.Decrement(ref _connectionCount);
            state.SendLock.Dispose();
            return false;
        }

        if (!_connections.TryAdd(clientId, state))
        {
            _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
            Interlocked.Decrement(ref _connectionCount);
            state.SendLock.Dispose();
            return false;
        }

        return true;
    }

    private void RemoveConnection(string clientId, ConnectionState state)
    {
        if (!_connections.TryRemove(clientId, out _))
            return;

        Interlocked.Decrement(ref _connectionCount);
        _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
        try { state.Socket.Dispose(); } catch { /* ignore */ }
        DisposeSendLockIfSafe(state, removed: true);
    }

    private static bool TryReserveSend(ConnectionState state)
    {
        lock (state.LifecycleGate)
        {
            if (state.Removed)
                return false;

            state.SendReservations++;
            return true;
        }
    }

    private static void CompleteSendReservation(ConnectionState state)
    {
        SemaphoreSlim? sendLockToDispose = null;
        lock (state.LifecycleGate)
        {
            state.SendReservations--;
            if (state.Removed && !state.SendLockDisposed && state.SendReservations == 0)
            {
                state.SendLockDisposed = true;
                sendLockToDispose = state.SendLock;
            }
        }

        if (sendLockToDispose is not null)
        {
            try { sendLockToDispose.Dispose(); } catch { /* ignore */ }
        }
    }

    private static void DisposeSendLockIfSafe(ConnectionState state, bool removed)
    {
        SemaphoreSlim? sendLockToDispose = null;
        lock (state.LifecycleGate)
        {
            if (removed)
                state.Removed = true;

            if (!state.SendLockDisposed && state.SendReservations == 0)
            {
                state.SendLockDisposed = true;
                sendLockToDispose = state.SendLock;
            }
        }

        if (sendLockToDispose is not null)
        {
            try { sendLockToDispose.Dispose(); } catch { /* ignore */ }
        }
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
                ValueWebSocketReceiveResult result;
                using var timeoutCts = _config.ReceiveTimeoutSeconds > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;

                if (timeoutCts is not null)
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.ReceiveTimeoutSeconds));

                try
                {
                    result = await ws.ReceiveAsync(memory, timeoutCts?.Token ?? ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
                catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
                {
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "receive timeout", CancellationToken.None);
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                catch (WebSocketException)
                {
                    return null;
                }

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

    private sealed record ParsedWsInbound(
        bool IsEnvelope,
        string? Type,
        string? Text,
        string? SessionId,
        string? MessageId,
        string? ReplyToMessageId,
        string? ApprovalId,
        bool? Approved);

    private static ParsedWsInbound TryParseClientEnvelope(string payload)
    {
        const int MaxExtractedTextLength = 1_000_000; // 1MB text limit after JSON parsing

        var span = payload.AsSpan();
        var i = 0;
        while (i < span.Length && char.IsWhiteSpace(span[i]))
            i++;

        if (i >= span.Length || span[i] != '{')
            return new ParsedWsInbound(false, null, payload, null, null, null, null, null);

        try
        {
            var env = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsClientEnvelope);
            if (env is { Type: "user_message" })
            {
                var extractedText = env.Text ?? env.Content;
                extractedText ??= "";

                // Validate extracted text length to prevent memory pressure
                if (extractedText.Length > MaxExtractedTextLength)
                    extractedText = extractedText[..MaxExtractedTextLength];

                return new ParsedWsInbound(true, env.Type, extractedText, env.SessionId, env.MessageId, env.ReplyToMessageId, null, null);
            }

            if (env is { Type: "tool_approval_decision", ApprovalId: not null, Approved: not null })
            {
                return new ParsedWsInbound(true, env.Type, "", env.SessionId, env.MessageId, env.ReplyToMessageId, env.ApprovalId, env.Approved);
            }
        }
        catch
        {
            // fall through to raw
        }

        return new ParsedWsInbound(false, null, payload, null, null, null, null, null);
    }

    private static ValueTask CloseIfOpenAsync(WebSocket ws, WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return ValueTask.CompletedTask;

        return new ValueTask(ws.CloseAsync(status, description, ct));
    }
}
