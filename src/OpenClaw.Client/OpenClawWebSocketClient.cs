using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed class OpenClawWebSocketClient : IAsyncDisposable
{
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private WebSocket? _ws;
    private CancellationTokenSource? _rxCts;
    private Task? _rxLoop;

    public OpenClawWebSocketClient(int maxMessageBytes = 256 * 1024)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _ws?.State == WebSocketState.Open;
            }
        }
    }

    public event Action<string>? OnTextMessage;
    public event Action<WsServerEnvelope>? OnEnvelopeReceived;
    public event Action<string>? OnError;

    public async Task ConnectAsync(Uri wsUri, string? bearerToken, CancellationToken ct)
    {
        await DisconnectAsync(ct);

        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
            ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

        await ws.ConnectAsync(wsUri, ct);

        var rxCts = new CancellationTokenSource();
        var rxLoop = Task.Run(() => ReceiveLoopAsync(ws, rxCts.Token), rxCts.Token);

        lock (_stateLock)
        {
            _ws = ws;
            _rxCts = rxCts;
            _rxLoop = rxLoop;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        WebSocket? ws;
        CancellationTokenSource? rxCts;
        Task? rxLoop;

        await _sendLock.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                ws = _ws;
                rxCts = _rxCts;
                rxLoop = _rxLoop;
                _ws = null;
                _rxCts = null;
                _rxLoop = null;
            }

            try
            {
                if (rxCts is not null)
                    await rxCts.CancelAsync();
            }
            catch
            {
            }

            if (rxLoop is not null)
            {
                try { await rxLoop.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            }

            if (rxCts is not null)
            {
                try { rxCts.Dispose(); } catch { }
            }

            if (ws is null)
                return;

            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", ct);
            }
            catch
            {
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendUserMessageAsync(string text, string? messageId, string? replyToMessageId, CancellationToken ct)
        => await SendEnvelopeAsync(
            new WsClientEnvelope
            {
                Type = "user_message",
                Text = text,
                MessageId = messageId,
                ReplyToMessageId = replyToMessageId
            },
            ct);

    public async Task SendEnvelopeAsync(WsClientEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(envelope, CoreJsonContext.Default.WsClientEnvelope);

        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > _maxMessageBytes)
            throw new InvalidOperationException("Message too large.");

        await _sendLock.WaitAsync(ct);
        try
        {
            WebSocket? ws;
            lock (_stateLock)
            {
                ws = _ws;
            }

            if (ws is null || ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var writer = new ArrayBufferWriter<byte>(16 * 1024);

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                writer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    if (writer.WrittenCount + result.Count > _maxMessageBytes)
                        throw new InvalidOperationException("Inbound message too large.");

                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var text = Encoding.UTF8.GetString(writer.WrittenSpan);
                try
                {
                    OnTextMessage?.Invoke(text);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                }

                try
                {
                    var envelope = JsonSerializer.Deserialize(text, CoreJsonContext.Default.WsServerEnvelope);
                    if (envelope is not null)
                    {
                        try
                        {
                            OnEnvelopeReceived?.Invoke(envelope);
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke(ex.Message);
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); } catch { }
        _sendLock.Dispose();
    }

    internal void SetConnectedSocketForTest(WebSocket ws)
    {
        lock (_stateLock)
        {
            _ws = ws;
            _rxCts = null;
            _rxLoop = null;
        }
    }

    internal Task RunReceiveLoopForTest(WebSocket ws, CancellationToken ct)
        => ReceiveLoopAsync(ws, ct);
}
