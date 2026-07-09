using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// WeCom (Enterprise WeChat) AI Bot channel adapter.
/// Uses WebSocket long connection for receiving messages (no public callback URL needed),
/// REST API for sending messages and uploading media.
/// Supports runtime hot-reload of configuration.
/// </summary>
public sealed class WeComChannel : IChannelAdapter, IRestartableChannelAdapter
{
    // ── WeCom API URLs ──
    /// <summary>AI Bot WebSocket long-connection URL</summary>
    private const string WeComWsUrl = "wss://openws.work.weixin.qq.com";

    /// <summary>WeCom REST API base URL</summary>
    private const string WeComApiBase = "https://qyapi.weixin.qq.com";

    // ── Heartbeat ──
    /// <summary>Send ping every 30s to keep alive</summary>
    private const int HeartbeatIntervalMs = 30_000;

    private readonly WeComChannelConfig _initialConfig;
    private readonly HttpClient _http;
    private readonly ILogger<WeComChannel> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    // Runtime override config (set via UpdateConfigAsync, takes precedence over appsettings)
    private volatile WeComChannelConfig? _runtimeOverride;

    // ── WebSocket long-connection credentials ──
    private string? _botId;
    private string? _botSecret;

    // ── REST API credentials ──
    private string? _corpId;
    private int _agentId;
    private string? _corpSecret;

    // ── Access Token cache ──
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // ── Connection lifecycle ──
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private CancellationToken _appLifetime = CancellationToken.None;

    // ── Active WebSocket connection (for sending replies) ──
    private volatile ClientWebSocket? _activeWs;

    // ── WebSocket send lock, prevents concurrent writes ──
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    // ── Message dedup: key=msgid, value=expiration time (Unix ms) ──
    private readonly ConcurrentDictionary<string, long> _dedup = new(StringComparer.Ordinal);
    private const long DedupTtlMs = 5L * 60 * 1_000; // 5 min TTL
    private const int DedupMaxSize = 2_000; // max 2000 entries

    // ── Media download temp directory ──
    private static readonly string MediaTempDir = Path.Combine(
        Path.GetTempPath(), "openclaw_wecom");

    // ── Inbound message context cache (for fast WebSocket replies) ──
    // key: chatid or userid, value: most recent inbound message context
    private readonly ConcurrentDictionary<string, InboundMsgContext> _inboundContexts = new(StringComparer.Ordinal);

    /// <summary>
    /// Cache the context of the most recent inbound message for fast WebSocket replies.
    /// </summary>
    /// <param name="ReqId">Message callback req_id, must be echoed in reply</param>
    private sealed record InboundMsgContext(string ReqId, DateTimeOffset ReceivedAt);

    public WeComChannel(
        WeComChannelConfig initialConfig,
        ILogger<WeComChannel> logger)
    {
        _initialConfig = initialConfig;
        _logger = logger;
        _http = HttpClientFactory.Create();
    }

    public string ChannelId => "wecom";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    /// <summary>Get current effective config (runtime override takes precedence)</summary>
    public WeComChannelConfig GetEffectiveConfig() => _runtimeOverride ?? _initialConfig;

    /// <summary>Set runtime config override</summary>
    public void SetRuntimeConfig(WeComChannelConfig? cfg) => _runtimeOverride = cfg;

    /// <summary>Hot-reload config and reconnect</summary>
    public async Task UpdateConfigAsync(WeComChannelConfig newConfig, CancellationToken ct = default)
    {
        SetRuntimeConfig(newConfig);
        await RestartAsync(ct);
    }

    // ════════════════════════════ Lifecycle ════════════════════════════

    public async Task StartAsync(CancellationToken ct)
    {
        _appLifetime = ct;

        var cfg = GetEffectiveConfig();
        if (!cfg.Enabled)
            return;

        ResolveCredentials(cfg);
        if (!ValidateWsCredentials())
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunWsLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    /// <summary>Reconnect (for config hot-reload)</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            // Cancel current receive loop
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                if (_receiveLoop is not null)
                {
                    try { await _receiveLoop; }
                    catch (OperationCanceledException) { }
                    catch (Exception) { }
                }
                _cts.Dispose();
                _cts = null;
                _receiveLoop = null;
            }

            var cfg = GetEffectiveConfig();
            if (!cfg.Enabled)
                return;

            ResolveCredentials(cfg);
            if (!ValidateWsCredentials())
                return;

            // Clear cached state
            _accessToken = null;
            _tokenExpiry = DateTimeOffset.MinValue;
            _inboundContexts.Clear();
            _dedup.Clear();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime);
            _receiveLoop = RunWsLoopAsync(_cts.Token);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>Resolve credentials (SecretRef or plaintext value)</summary>
    private void ResolveCredentials(WeComChannelConfig cfg)
    {
        _botId = SecretResolver.Resolve(cfg.BotIdRef) ?? cfg.BotId;
        _botSecret = SecretResolver.Resolve(cfg.BotSecretRef) ?? cfg.BotSecret;
        _corpId = SecretResolver.Resolve(cfg.CorpIdRef) ?? cfg.CorpId;
        _corpSecret = SecretResolver.Resolve(cfg.CorpSecretRef) ?? cfg.CorpSecret;

        var agentIdStr = SecretResolver.Resolve(cfg.AgentIdRef);
        _agentId = int.TryParse(agentIdStr, out var aid) ? aid : cfg.AgentId;
    }

    /// <summary>Validate WebSocket long-connection credentials</summary>
    private bool ValidateWsCredentials()
    {
        if (string.IsNullOrWhiteSpace(_botId) || string.IsNullOrWhiteSpace(_botSecret))
        {
            _logger.LogError("WeCom BotId or BotSecret is not configured; channel cannot start.");
            return false;
        }
        return true;
    }

    /// <summary>Check if REST API credentials are complete</summary>
    private bool HasApiCredentials()
        => !string.IsNullOrWhiteSpace(_corpId) && !string.IsNullOrWhiteSpace(_corpSecret);

    // ════════════════════════════ WebSocket Long Connection Loop ════════════════════════════

    /// <summary>
    /// WebSocket main loop: connect → authenticate → receive messages → disconnect and reconnect.
    /// Uses exponential backoff, starting at 2s, max 60s.
    /// </summary>
    private async Task RunWsLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        const int maxBackoffSec = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try
                {
                    var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await ws.ConnectAsync(new Uri(WeComWsUrl), ct);

                    // Save active connection reference for SendAsync to send replies
                    _activeWs = ws;

                    // Send subscribe frame for authentication after connecting
                    await SendSubscribeAsync(ws, ct);

                    backoff = TimeSpan.FromSeconds(2);
                    // Enter message processing loop (includes heartbeat)
                    await ProcessWsMessagesAsync(ws, ct);
                }
                finally
                {
                    var oldWs = Interlocked.Exchange(ref _activeWs, null);
                    try { oldWs?.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WeCom WebSocket connection error, reconnecting in {Sec}s.", backoff.TotalSeconds);
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSec));
        }
    }

    /// <summary>
    /// Send aibot_subscribe frame to complete authentication.
    /// </summary>
    private async Task SendSubscribeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString("N");
        var json = BuildWsMessage("aibot_subscribe", reqId,
            $"\"bot_id\":{JsonString(_botId!)},\"secret\":{JsonString(_botSecret!)}");

        await SendWsTextDirectAsync(ws, json, ct);
    }

    /// <summary>
    /// Process WebSocket message loop: receive complete frames → dispatch to HandleWsFrameAsync.
    /// Also manage heartbeat timer.
    /// </summary>
    private async Task ProcessWsMessagesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        var lastPing = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Check if heartbeat needs to be sent
            if (DateTimeOffset.UtcNow - lastPing > TimeSpan.FromMilliseconds(HeartbeatIntervalMs))
            {
                await SendPingAsync(ws, ct);
                lastPing = DateTimeOffset.UtcNow;
            }

            buffer.Clear();
            ValueWebSocketReceiveResult result;

            // Read complete frame
            do
            {
                var mem = buffer.GetMemory(8192);
                // Set receive timeout to 2x heartbeat interval to avoid permanent blocking
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HeartbeatIntervalMs * 2);
                try
                {
                    result = await ws.ReceiveAsync(mem, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout, send heartbeat and continue
                    await SendPingAsync(ws, ct);
                    lastPing = DateTimeOffset.UtcNow;
                    result = new ValueWebSocketReceiveResult(0, WebSocketMessageType.Text, true);
                    break;
                }

                buffer.Advance(result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (buffer.WrittenCount == 0)
                continue;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            try
            {
                await HandleWsFrameAsync(json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WeCom WebSocket frame processing error: {Json}", json);
            }
        }
    }

    /// <summary>
    /// Dispatch WebSocket frames: route to handler by cmd field.
    /// Note: WeCom server response frames (subscribe results, heartbeat responses, etc.) do not have a cmd field, 
    /// format: {headers:{req_id}, errcode:0, errmsg:"ok"}.
    /// </summary>
    private async Task HandleWsFrameAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cmd = GetString(root, "cmd");
        var reqId = GetReqId(root);

        // No cmd field → server response frame (subscribe / ping / upload, etc.)
        if (cmd is null)
        {
            var errCode = root.TryGetProperty("errcode", out var ec) ? ec.GetInt32() : -1;
            if (errCode != 0)
            {
                var errMsg = GetString(root, "errmsg");
                _logger.LogError("WeCom response failed req_id={ReqId} errcode={ErrCode} errmsg={ErrMsg}", reqId, errCode, errMsg);
            }
            return;
        }

        switch (cmd)
        {
            // ── Message callback ──
            case "aibot_msg_callback":
                await HandleMsgCallbackAsync(root, reqId, ct);
                return;

            // ── Event callback ──
            case "aibot_event_callback":
                HandleEventCallback(root);
                return;

            default:
                return;
        }
    }

    // ════════════════════════════ Message Handling ════════════════════════════

    /// <summary>
    /// Handle aibot_msg_callback: parse message body → filter → build InboundMessage → trigger OnMessageReceived.
    /// </summary>
    private async Task HandleMsgCallbackAsync(JsonElement root, string? reqId, CancellationToken ct)
    {
        if (!root.TryGetProperty("body", out var body))
        {
            _logger.LogWarning("WeCom message callback missing body field.");
            return;
        }

        var cfg = GetEffectiveConfig();
        var msgId = GetString(body, "msgid");
        var chatId = GetString(body, "chatid");
        var chatType = GetString(body, "chattype"); // "group" or "single"
        var msgType = GetString(body, "msgtype") ?? "text";

        // Parse sender info
        string? senderId = null;
        string? senderName = null;
        if (body.TryGetProperty("from", out var fromProp))
        {
            senderId = GetString(fromProp, "userid");
            senderName = GetString(fromProp, "name") ?? GetString(fromProp, "username");
        }

        // Extract message text
        var text = ReadWeComText(body);

        // ── Message dedup ──
        if (!string.IsNullOrWhiteSpace(msgId) && !TryClaimDedup(msgId))
            return;

        // ── Extract ReplyToMessageId (quote reply) ──
        string? replyToMessageId = null;
        if (body.TryGetProperty("quote", out var quote))
        {
            replyToMessageId = msgId; // WeCom quote does not return original msgid, use current msgId for association
        }

        // ── Media file download ──
        var mediaText = await DownloadWeComMediaAsync(body, msgType, msgId, ct);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(mediaText))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(senderId))
            return;

        var isGroup = string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase);

        // ── Group policy filter ──
        if (isGroup)
        {
            if (string.Equals(cfg.GroupPolicy, "disabled", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(cfg.GroupPolicy, "allowlist", StringComparison.OrdinalIgnoreCase) &&
                !IsGroupAllowed(chatId, cfg))
                return;
        }

        // ── Sender allowlist filter ──
        if (!IsUserAllowed(senderId, cfg))
            return;

        // ── @mention extraction ──
        string[]? mentionedIds = null;
        var isBotMentioned = false;
        if (body.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.Object)
        {
            var content = GetString(textProp, "content") ?? "";
            // WeCom group @bot message format is "@BotName message content", extract @mentioned userId
            if (content.Contains('@'))
            {
                isBotMentioned = true;
                // Try to get from mentions array (if WeCom provides it)
                if (textProp.TryGetProperty("mentions", out var mentionsArr) && mentionsArr.ValueKind == JsonValueKind.Array)
                {
                    var list = mentionsArr.EnumerateArray()
                        .Select(static mention => GetString(mention, "userid"))
                        .OfType<string>()
                        .Where(static uid => !string.IsNullOrWhiteSpace(uid))
                        .ToList();
                    if (list.Count > 0)
                        mentionedIds = [.. list];
                }
            }
        }

        // ── Group @mention filter ──
        if (isGroup && cfg.RequireMentionInGroup && !isBotMentioned)
            return;

        // ── Merge text and media markers ──
        var finalText = text ?? "";
        if (!string.IsNullOrWhiteSpace(mediaText))
            finalText = string.IsNullOrWhiteSpace(finalText) ? mediaText : finalText + "\n" + mediaText;

        // ── Text truncation ──
        if (finalText.Length > cfg.MaxInboundChars)
            finalText = finalText[..cfg.MaxInboundChars];

        // ── Cache inbound context (for subsequent fast WebSocket replies) ──
        CacheInboundContext(msgId, reqId ?? "", chatId, senderId);

        // ── Build InboundMessage ──
        var inbound = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            SenderName = senderName,
            Text = finalText,
            MessageId = msgId,
            ReplyToMessageId = replyToMessageId,
            IsGroup = isGroup,
            GroupId = isGroup ? chatId : null,
            MentionedIds = mentionedIds,
            MediaType = msgType,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(inbound, ct);
    }

    /// <summary>
    /// Handle aibot_event_callback:
    /// - enter_chat: user enters 1-on-1 chat for the first time, send welcome message (reply within 5s)
    /// - template_card_event / feedback_event / disconnected_event: log only
    /// </summary>
    private void HandleEventCallback(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body))
            return;

        string? eventType = null;
        if (body.TryGetProperty("event", out var eventProp) &&
            eventProp.ValueKind == JsonValueKind.Object)
        {
            eventType = GetString(eventProp, "eventtype");
        }
        eventType ??= GetString(body, "eventtype");

        switch (eventType)
        {
            case "enter_chat":
            case "disconnected_event":
                break;
        }
    }

    /// <summary>Cache inbound message context for subsequent fast WebSocket replies (forward reqId)</summary>
    private void CacheInboundContext(string? msgId, string reqId, string? chatId, string? senderId)
    {
        if (string.IsNullOrWhiteSpace(msgId) || string.IsNullOrWhiteSpace(reqId))
            return;

        var ctx = new InboundMsgContext(reqId, DateTimeOffset.UtcNow);

        // Cache with both chatid and userid as keys
        if (!string.IsNullOrWhiteSpace(chatId))
            _inboundContexts[chatId] = ctx;
        if (!string.IsNullOrWhiteSpace(senderId))
            _inboundContexts[senderId] = ctx;
    }

    /// <summary>
    /// Try to get context usable for WebSocket replies.
    /// WeCom allows replying to the last user message within 24 hours.
    /// </summary>
    private bool TryGetInboundContext(string recipientId, out InboundMsgContext ctx)
    {
        ctx = null!;
        if (!_inboundContexts.TryGetValue(recipientId, out var found))
            return false;

        // Context older than 24 hours is not usable
        if (DateTimeOffset.UtcNow - found.ReceivedAt > TimeSpan.FromHours(24))
        {
            _inboundContexts.TryRemove(recipientId, out _);
            return false;
        }

        ctx = found;
        return true;
    }

    /// <summary>Extract WeCom message text</summary>
    private static string? ReadWeComText(JsonElement body)
    {
        // Plain text message
        if (body.TryGetProperty("text", out var textProp) &&
            textProp.ValueKind == JsonValueKind.Object)
        {
            var content = GetString(textProp, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return content;
        }

        // mixed message (text+image): extract all text-type items
        if (body.TryGetProperty("mixed", out var mixedProp) &&
            mixedProp.ValueKind == JsonValueKind.Object &&
            mixedProp.TryGetProperty("msg_item", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in items.EnumerateArray())
            {
                var itemType = GetString(item, "msgtype");
                if (string.Equals(itemType, "text", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("text", out var itemText) &&
                    itemText.ValueKind == JsonValueKind.Object)
                {
                    var itemContent = GetString(itemText, "content");
                    if (!string.IsNullOrWhiteSpace(itemContent))
                        sb.Append(itemContent);
                }
            }
            if (sb.Length > 0)
                return sb.ToString();
        }

        return null;
    }

    // ════════════════ Message Dedup ════════════════════

    /// <summary>Message dedup: check if msgid has been processed, mark it if not, and return true.</summary>
    private bool TryClaimDedup(string msgId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_dedup.TryGetValue(msgId, out var expMs) && expMs > now)
            return false;

        _dedup[msgId] = now + DedupTtlMs;

        if (_dedup.Count > DedupMaxSize)
            EvictExpiredDedup(now);

        return true;
    }

    private void EvictExpiredDedup(long now)
    {
        foreach (var key in _dedup.Keys.ToList().Where(key => _dedup.TryGetValue(key, out var expMs) && expMs <= now))
        {
            _dedup.TryRemove(key, out _);
        }
    }

    // ═══════════════ Media Download ════════════════════

    /// <summary>
    /// Download media files (images/files/voice) from WeCom messages to temp directory, 
    /// returning [IMAGE_PATH:...] or [FILE_PATH:...] markers.
    /// </summary>
    private async Task<string?> DownloadWeComMediaAsync(JsonElement body, string msgType, string? msgId, CancellationToken ct)
    {
        try
        {
            string? mediaId = null;
            var propName = msgType switch
            {
                "image" => "image",
                "file" => "file",
                "voice" => "voice",
                "video" => "video",
                _ => null
            };

            if (propName is not null &&
                body.TryGetProperty(propName, out var prop) &&
                prop.ValueKind == JsonValueKind.Object)
            {
                mediaId = GetString(prop, "media_id");
            }

            if (string.IsNullOrWhiteSpace(mediaId))
                return null;

            // REST API credentials required for download
            if (!HasApiCredentials())
                return BuildMediaMarker(body, msgType); // Fall back to plain text marker

            await RefreshAccessTokenAsync(ct);

            var url = $"{WeComApiBase}/cgi-bin/media/get?access_token={_accessToken}&media_id={Uri.EscapeDataString(mediaId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return BuildMediaMarker(body, msgType);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "application/pdf" => ".pdf",
                "audio/amr" => ".amr",
                "audio/mp3" => ".mp3",
                "video/mp4" => ".mp4",
                _ => msgType switch
                {
                    "image" => ".jpg",
                    "voice" => ".amr",
                    "video" => ".mp4",
                    _ => ".bin"
                }
            };

            Directory.CreateDirectory(MediaTempDir);
            var filePath = Path.Combine(MediaTempDir, $"{Guid.NewGuid():N}{ext}");
            await using var fs = File.Create(filePath);
            await response.Content.CopyToAsync(fs, ct);

            return msgType switch
            {
                "image" => $"[IMAGE_PATH:{filePath}]",
                "voice" => $"[VOICE_PATH:{filePath}]",
                "video" => $"[VIDEO_PATH:{filePath}]",
                _ => $"[FILE_PATH:{filePath}]"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download WeCom media msgId={MsgId}.", msgId);
            return BuildMediaMarker(body, msgType); // Fall back to text marker
        }
    }

    /// <summary>
    /// Build media marker (plain text fallback). WeCom image/file/voice/video messages carry a media_id, 
    /// when download fails, convert to [IMAGE:wecom:...] markers for LLM reference.
    /// </summary>
    private static string? BuildMediaMarker(JsonElement body, string msgType)
    {
        return msgType switch
        {
            "image" => body.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object
                ? $"[IMAGE:wecom:{GetString(img, "media_id") ?? "unknown"}]"
                : "[IMAGE:wecom:unknown]",

            "file" => body.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object
                ? $"[FILE:wecom:{GetString(file, "media_id") ?? "unknown"}]"
                : "[FILE:wecom:unknown]",

            "voice" => body.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object
                ? $"[VOICE:wecom:{GetString(voice, "media_id") ?? "unknown"}]"
                : "[VOICE:wecom:unknown]",

            "video" => body.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object
                ? $"[VIDEO:wecom:{GetString(video, "media_id") ?? "unknown"}]"
                : "[VIDEO:wecom:unknown]",

            _ => null
        };
    }

    // ════════════════════════════ Send Message ════════════════════════════

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            return;

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);

            // Try WebSocket reply first (valid within 24h)
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                var sentViaWs = await TrySendTextViaWsAsync(outbound.RecipientId, remaining, ct);
                if (!sentViaWs)
                {
                    // AI Bot proactive send should use WebSocket aibot_send_msg.
                    var sentViaActiveWs = await TrySendTextViaActiveWsAsync(outbound.RecipientId, remaining, ct);
                    if (!sentViaActiveWs && !HasApiCredentials())
                    {
                        _logger.LogWarning("WeCom REST API credentials not configured, cannot send message to {RecipientId}。", outbound.RecipientId);
                    }
                    else if (!sentViaActiveWs)
                    {
                        // Last resort: fall back to self-built app REST API; message will be sent as self-built app identity, not AI Bot.
                        await RefreshAccessTokenAsync(ct);
                        await SendTextViaApiAsync(outbound.RecipientId, remaining, ct);
                    }
                }
            }

            // Send media (images/files, etc.)
            if (markers.Count > 0 && HasApiCredentials())
            {
                foreach (var marker in markers)
                {
                    try
                    {
                        await RefreshAccessTokenAsync(ct);
                        await SendMarkerViaApiAsync(outbound.RecipientId, marker, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send WeCom media marker {Kind}={Value}.",
                            marker.Kind, marker.Value);
                    }
                }
            }

            // Fallback: when both text and markers are empty, send raw text directly
            if (string.IsNullOrWhiteSpace(remaining) && markers.Count == 0)
            {
                var sentViaWs = await TrySendTextViaWsAsync(outbound.RecipientId, outbound.Text, ct);
                if (!sentViaWs)
                    sentViaWs = await TrySendTextViaActiveWsAsync(outbound.RecipientId, outbound.Text, ct);
                if (!sentViaWs && HasApiCredentials())
                {
                    await RefreshAccessTokenAsync(ct);
                    await SendTextViaApiAsync(outbound.RecipientId, outbound.Text, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WeCom message to {RecipientId}.", outbound.RecipientId);
        }
    }

    /// <summary>Attempt to reply via WebSocket text message (requires inbound message within 24h)</summary>
    private async Task<bool> TrySendTextViaWsAsync(string recipientId, string text, CancellationToken ct)
    {
        if (!TryGetInboundContext(recipientId, out var ctx))
            return false;

        var streamId = Guid.NewGuid().ToString("N");
        // headers.req_id must forward the original req_id from the message callback
        // msgtype must be "stream"
        var body = $"\"msgtype\":\"stream\"," +
                   $"\"stream\":{{\"id\":{JsonString(streamId)},\"finish\":true,\"content\":{JsonString(text)}}}";
        var json = BuildWsMessage("aibot_respond_msg", ctx.ReqId, body);

        return await SendWsTextAsync(json, ct);
    }

    /// <summary>Send Markdown message proactively via AI Bot WebSocket.</summary>
    private async Task<bool> TrySendTextViaActiveWsAsync(string recipientId, string text, CancellationToken ct)
    {
        var reqId = "aibot_send_msg_" + Guid.NewGuid().ToString("N");
        var body = $"\"chatid\":{JsonString(recipientId)}," +
                   $"\"msgtype\":\"markdown\"," +
                   $"\"markdown\":{{\"content\":{JsonString(TruncateToMaxUtf8Bytes(text, 20480))}}}";
        var json = BuildWsMessage("aibot_send_msg", reqId, body);

        return await SendWsTextAsync(json, ct);
    }

    /// <summary>Send text message via REST API</summary>
    private async Task SendTextViaApiAsync(string recipientId, string text, CancellationToken ct)
    {
        const int maxBytes = 2048;
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = TruncateToMaxUtf8Bytes(text, maxBytes);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["touser"] = recipientId,
            ["msgtype"] = "text",
            ["agentid"] = _agentId,
            ["text"] = new Dictionary<string, object> { ["content"] = text }
        };

        var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
        await PostApiAsync("/cgi-bin/message/send", json, ct);
    }

    /// <summary>Send media markers (images/files) via REST API</summary>
    private async Task SendMarkerViaApiAsync(string recipientId, MediaMarker marker, CancellationToken ct)
    {
        switch (marker.Kind)
        {
            case MediaMarkerKind.ImagePath:
            case MediaMarkerKind.ImageUrl:
                {
                    var data = await FetchMediaBytesAsync(marker, ct);
                    if (data is null) return;
                    var mediaId = await UploadMediaAsync(data, "image", ct);
                    if (mediaId is null) return;

                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["touser"] = recipientId,
                        ["msgtype"] = "image",
                        ["agentid"] = _agentId,
                        ["image"] = new Dictionary<string, object> { ["media_id"] = mediaId }
                    };
                    var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
                    await PostApiAsync("/cgi-bin/message/send", json, ct);
                    break;
                }

            case MediaMarkerKind.FilePath:
            case MediaMarkerKind.FileUrl:
                {
                    var data = await FetchMediaBytesAsync(marker, ct);
                    if (data is null) return;
                    var mediaId = await UploadMediaAsync(data, "file", ct);
                    if (mediaId is null) return;

                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["touser"] = recipientId,
                        ["msgtype"] = "file",
                        ["agentid"] = _agentId,
                        ["file"] = new Dictionary<string, object> { ["media_id"] = mediaId }
                    };
                    var json = JsonSerializer.Serialize(payload, WeComJsonContext.Default.DictionaryStringObject);
                    await PostApiAsync("/cgi-bin/message/send", json, ct);
                    break;
                }

            case MediaMarkerKind.AudioUrl:
            case MediaMarkerKind.VideoUrl:
                break;
        }
    }

    /// <summary>POST request to WeCom REST API</summary>
    private async Task PostApiAsync(string path, string json, CancellationToken ct)
    {
        var url = $"{WeComApiBase}{path}?access_token={_accessToken}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("WeCom API call failed: {Status} {Body}", response.StatusCode, errBody);
        }
    }

    // ══════════════════ Heartbeat ═══════════════════════

    /// <summary>Send ping heartbeat frame</summary>
    private async Task SendPingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reqId = Guid.NewGuid().ToString("N");
        var json = BuildWsMessage("ping", reqId, null);
        await SendWsTextDirectAsync(ws, json, ct);
    }

    // ══════════════ Media Upload ════════════════════

    /// <summary>Upload media file to WeCom, return media_id</summary>
    private async Task<string?> UploadMediaAsync(byte[] data, string mediaType, CancellationToken ct)
    {
        var url = $"{WeComApiBase}/cgi-bin/media/upload?access_token={_accessToken}&type={mediaType}";

        using var content = new MultipartFormDataContent();
        var ext = mediaType == "image" ? ".jpg" : ".bin";
        var name = mediaType == "image" ? "image" : "file";
        content.Add(new ByteArrayContent(data), name, Guid.NewGuid().ToString("N") + ext);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("media_id", out var mediaId))
            return mediaId.GetString();

        _logger.LogWarning("WeCom media upload failed: {Body}", body);
        return null;
    }

    /// <summary>Get media file byte array</summary>
    private async Task<byte[]?> FetchMediaBytesAsync(MediaMarker marker, CancellationToken ct)
    {
        try
        {
            return marker.Kind is MediaMarkerKind.ImagePath or MediaMarkerKind.FilePath
                ? await File.ReadAllBytesAsync(marker.Value, ct)
                : await _http.GetByteArrayAsync(marker.Value, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get media file: {Source}", marker.Value);
            return null;
        }
    }

    // ════════════════════════════ REST API Auth ════════════════════════════

    /// <summary>
    /// Get/refresh access_token.
    /// GET /cgi-bin/gettoken?corpid={corpid}&corpsecret={corpsecret}
    /// Returns {"access_token":"...","expires_in":7200}
    /// Token auto-refreshes 10 minutes before expiry.
    /// </summary>
    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-10))
            return;

        var url = $"{WeComApiBase}/cgi-bin/gettoken?corpid={Uri.EscapeDataString(_corpId!)}&corpsecret={Uri.EscapeDataString(_corpSecret!)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("errcode", out var errCode) && errCode.GetInt32() != 0)
        {
            var errMsg = GetString(root, "errmsg") ?? "unknown";
            throw new InvalidOperationException($"WeCom access_token retrieval failed: {errCode.GetInt32()} {errMsg}");
        }

        _accessToken = root.TryGetProperty("access_token", out var tokenProp)
            ? tokenProp.GetString()
            : throw new InvalidOperationException($"WeCom access_token missing from response: {body}");

        var expireSec = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 7200;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expireSec);
    }

    // ════════════════════════════ WebSocket Send ════════════════════════════

    /// <summary>
    /// Build WeCom WebSocket message frame.
    /// Format: {"cmd":"...","headers":{"req_id":"..."},"body":{...}}
    /// bodyJson is JSON fragment of body object (without outer braces), body field omitted when null.
    /// </summary>
    private static string BuildWsMessage(string cmd, string reqId, string? bodyJson)
    {
        if (bodyJson is null)
            return $"{{\"cmd\":{JsonString(cmd)},\"headers\":{{\"req_id\":{JsonString(reqId)}}}}}";
        return $"{{\"cmd\":{JsonString(cmd)},\"headers\":{{\"req_id\":{JsonString(reqId)}}},\"body\":{{{bodyJson}}}}}";
    }

    /// <summary>Serialize JSON string value, including outer quotes.</summary>
    private static string JsonString(string value)
        => $"\"{JsonEncodedText.Encode(value)}\"";

    /// <summary>Send text frame via WebSocket (thread-safe, uses active connection). Returns true on success.</summary>
    private async Task<bool> SendWsTextAsync(string text, CancellationToken ct)
    {
        var ws = _activeWs;
        if (ws is null || ws.State != WebSocketState.Open)
            return false;

        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    /// <summary>Send text frame via WebSocket (specified ws instance, for internal use in message loop)</summary>
    private async Task SendWsTextDirectAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _wsSendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    // ════════════════════════════ Helpers ════════════════════════════

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;

    private static string? GetReqId(JsonElement root)
    {
        if (root.TryGetProperty("headers", out var headers) &&
            headers.ValueKind == JsonValueKind.Object)
            return GetString(headers, "req_id");
        return null;
    }

    private static bool IsUserAllowed(string userId, WeComChannelConfig cfg)
    {
        if (cfg.AllowedFromUserIds.Length > 0)
            return Array.Exists(cfg.AllowedFromUserIds,
                id => string.Equals(id, userId, StringComparison.Ordinal));
        return true;
    }

    private static bool IsGroupAllowed(string? groupId, WeComChannelConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return false;
        if (cfg.AllowedGroupIds.Length > 0)
            return Array.Exists(cfg.AllowedGroupIds,
                id => string.Equals(id, groupId, StringComparison.Ordinal));
        return true;
    }

    /// <summary>Truncate text by max UTF-8 byte count</summary>
    private static string TruncateToMaxUtf8Bytes(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes)
            return s;

        var truncated = new byte[maxBytes];
        Array.Copy(bytes, truncated, maxBytes);
        return Encoding.UTF8.GetString(truncated);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop; }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
        }
        _wsSendLock.Dispose();
        _restartLock.Dispose();
    }
}

// ════════════════════════════ JSON Serialization (AOT-safe) ════════════════════════════

[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class WeComJsonContext : JsonSerializerContext;
