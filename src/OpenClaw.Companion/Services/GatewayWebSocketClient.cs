using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Companion.Services;

public sealed class GatewayWebSocketClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _rxCts;
    private Task? _rxLoop;

    public GatewayWebSocketClient(int maxMessageBytes = 256 * 1024)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<string>? OnTextMessage;
    public event Action<string>? OnError;

    public async Task ConnectAsync(Uri wsUri, string? bearerToken, CancellationToken ct)
    {
        await DisconnectAsync(ct);

        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
            ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

        await ws.ConnectAsync(wsUri, ct);

        _ws = ws;
        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rxLoop = Task.Run(() => ReceiveLoopAsync(ws, _rxCts.Token), _rxCts.Token);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var ws = _ws;
        _ws = null;

        try
        {
            if (_rxCts is not null)
            {
                await _rxCts.CancelAsync();
                _rxCts.Dispose();
                _rxCts = null;
            }
        }
        catch
        {
            // Best-effort.
        }

        if (_rxLoop is not null)
        {
            try { await _rxLoop.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { /* ignore */ }
            _rxLoop = null;
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
            // ignore
        }
        finally
        {
            ws.Dispose();
        }
    }

    public async Task SendUserMessageAsync(string text, string? messageId, string? replyToMessageId, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var payload = JsonSerializer.Serialize(new
        {
            type = "user_message",
            text,
            messageId,
            replyToMessageId
        }, JsonOptions);

        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > _maxMessageBytes)
            throw new InvalidOperationException("Message too large.");

        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
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
                OnTextMessage?.Invoke(text);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
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
        try { await DisconnectAsync(CancellationToken.None); } catch { /* ignore */ }
        _sendLock.Dispose();
    }
}

