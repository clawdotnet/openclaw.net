using System.Net.Http;
using System.Net.Http.Headers;
using System.Buffers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Collections.Concurrent;
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
/// DingTalk channel adapter.
/// Uses official Stream mode (WebSocket) to receive events - no public webhook required.
/// Supports text, image, file, audio, and video messages.
/// Config hot-reload: change DingTalk values in appsettings and the channel reconnects automatically.
/// </summary>
public sealed class DingTalkChannel : IChannelAdapter, IRestartableChannelAdapter
{
    private const string DingTalkBase = "https://api.dingtalk.com";
    private const string DingTalkStreamEndpoint = "https://api.dingtalk.com/v1.0/robot/robotCode/{0}/webhook/stream";

    private readonly DingTalkChannelConfig _initialConfig;
    private readonly HttpClient _http;
    private readonly ILogger<DingTalkChannel> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    private volatile DingTalkChannelConfig? _runtimeOverride;
    private readonly ConcurrentDictionary<string, SessionWebhookState> _sessionWebhooks = new(StringComparer.Ordinal);

    private string? _appKey;
    private string? _appSecret;
    private string? _appId;

    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private CancellationToken _appLifetime = CancellationToken.None;

    private string? _botUserId;

    // ── Message dedup: key=msgId, value=expiration time (Unix ms) ──
    private readonly ConcurrentDictionary<string, long> _dedup = new(StringComparer.Ordinal);
    private const long DedupTtlMs = 5L * 60 * 1_000; // 5 min TTL
    private const int DedupMaxSize = 2_000; // max 2000 entries

    // ── Media download temp directory ──
    private static readonly string MediaTempDir = Path.Combine(
        Path.GetTempPath(), "openclaw_dingtalk");

    public DingTalkChannel(
        DingTalkChannelConfig initialConfig,
        ILogger<DingTalkChannel> logger)
    {
        _initialConfig = initialConfig;
        _logger = logger;
        _http = HttpClientFactory.Create();
    }

    public string ChannelId => "dingtalk";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public DingTalkChannelConfig GetEffectiveConfig() => _runtimeOverride ?? _initialConfig;

    public void SetRuntimeConfig(DingTalkChannelConfig? cfg) => _runtimeOverride = cfg;

    public async Task UpdateConfigAsync(DingTalkChannelConfig newConfig, CancellationToken ct = default)
    {
        SetRuntimeConfig(newConfig);
        _logger.LogInformation("DingTalk runtime config updated via API — reconnecting.");
        await RestartAsync(ct);
    }

    // ──────────────────────────── Lifecycle ──────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        _appLifetime = ct;

        var cfg = GetEffectiveConfig();
        if (!cfg.Enabled)
        {
            _logger.LogInformation("DingTalk channel is disabled; set Enabled=true via admin API to activate.");
            return;
        }

        ResolveCredentials(cfg);
        if (!ValidateCredentials())
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunStreamLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                if (_receiveLoop is not null)
                {
                    try { await _receiveLoop; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogDebug(ex, "DingTalk receive loop exited with error during restart."); }
                }
                _cts.Dispose();
                _cts = null;
                _receiveLoop = null;
            }

            var cfg = GetEffectiveConfig();
            if (!cfg.Enabled)
            {
                _logger.LogInformation("DingTalk channel disabled after config hot-reload.");
                return;
            }

            ResolveCredentials(cfg);
            if (!ValidateCredentials())
                return;

            _accessToken = null;
            _tokenExpiry = DateTimeOffset.MinValue;
            _botUserId = null;
            _sessionWebhooks.Clear();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime);
            _receiveLoop = RunStreamLoopAsync(_cts.Token);
            _logger.LogInformation("DingTalk channel restarted with updated config.");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private void ResolveCredentials(DingTalkChannelConfig cfg)
    {
        _appId = SecretResolver.Resolve(cfg.AppIdRef) ?? cfg.AppId;
        _appKey = SecretResolver.Resolve(cfg.AppKeyRef) ?? cfg.AppKey;
        _appSecret = SecretResolver.Resolve(cfg.AppSecretRef) ?? cfg.AppSecret;
    }

    private bool ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_appKey) || string.IsNullOrWhiteSpace(_appSecret))
        {
            _logger.LogError("DingTalk AppKey or AppSecret is not configured; channel will not start.");
            return false;
        }
        return true;
    }

    // ──────────────────────────── Stream loop ─────────────────────────────────

    private async Task RunStreamLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        const int maxBackoffSec = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connection = await OpenStreamConnectionAsync(ct);
                var wsUrl = string.IsNullOrWhiteSpace(connection.Endpoint)
                    ? throw new InvalidOperationException("DingTalk stream endpoint is empty.")
                    : string.IsNullOrWhiteSpace(connection.Ticket)
                        ? connection.Endpoint
                        : $"{connection.Endpoint}{(connection.Endpoint.Contains('?') ? "&" : "?")}ticket={Uri.EscapeDataString(connection.Ticket)}";

                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await ws.ConnectAsync(new Uri(wsUrl), ct);
                _logger.LogInformation("DingTalk WebSocket connected.");

                backoff = TimeSpan.FromSeconds(2);
                await ProcessStreamMessagesAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DingTalk stream error. Reconnecting in {Sec}s.", backoff.TotalSeconds);
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSec));
        }
    }

    private async Task<DingTalkGatewayConnectionResponse> OpenStreamConnectionAsync(CancellationToken ct)
    {
        var requestBody = new DingTalkGatewayConnectionRequest
        {
            ClientId = _appKey,
            ClientSecret = _appSecret,
            Ua = "openclaw-dotnet/1.0",
            Subscriptions =
            [
                new DingTalkSubscription { Type = "EVENT", Topic = "*" },
                new DingTalkSubscription { Type = "CALLBACK", Topic = "/v1.0/im/bot/messages/get" }
            ]
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DingTalkBase}/v1.0/gateway/connections/open");
        request.Content = JsonContent.Create(requestBody, DingTalkJsonContext.Default.DingTalkGatewayConnectionRequest);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DingTalk stream connection open failed: {(int)response.StatusCode} {body}");

        var opened = JsonSerializer.Deserialize(body, DingTalkJsonContext.Default.DingTalkGatewayConnectionResponse);
        if (opened is null || string.IsNullOrWhiteSpace(opened.Endpoint) || string.IsNullOrWhiteSpace(opened.Ticket))
            throw new InvalidOperationException($"DingTalk stream connection open returned invalid payload: {body}");

        return opened;
    }

    private async Task ProcessStreamMessagesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;

            do
            {
                var mem = buffer.GetMemory(8192);
                result = await ws.ReceiveAsync(mem, ct);
                buffer.Advance(result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
            {
                _logger.LogDebug("DingTalk stream received non-text frame; skipping.");
                continue;
            }

            var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
            try
            {
                await HandleStreamEnvelopeAsync(ws, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DingTalk stream envelope processing error: {Json}", json);
            }
        }
    }

    private async Task HandleStreamEnvelopeAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = GetString(root, "type");
        var headers = root.TryGetProperty("headers", out var headersProp) ? headersProp : default;
        var topic = headers.ValueKind == JsonValueKind.Object ? GetString(headers, "topic") : null;
        var messageId = headers.ValueKind == JsonValueKind.Object ? GetString(headers, "messageId") : null;

        if (string.Equals(topic, "/v1.0/im/bot/messages/get", StringComparison.Ordinal))
        {
            await HandleRobotCallbackAsync(ws, root, messageId, ct);
            return;
        }

        if (string.Equals(type, "SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(topic, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var opaqueResponse = BuildPingOpaqueResponse(root);
                await SendStreamResponseAsync(ws, messageId, 200, "OK", opaqueResponse, ct);
            }
            return;
        }

        if (string.Equals(type, "EVENT", StringComparison.OrdinalIgnoreCase))
        {
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"status\":\"SUCCESS\",\"message\":\"success\"}", ct);
            return;
        }

        if (string.Equals(type, "CALLBACK", StringComparison.OrdinalIgnoreCase))
        {
            await SendStreamResponseAsync(ws, messageId, 404, "topic not supported", "{\"response\": null}", ct);
            return;
        }

        _logger.LogDebug("DingTalk stream envelope ignored: type={Type}, topic={Topic}", type, topic);
    }

    private async Task HandleRobotCallbackAsync(ClientWebSocket ws, JsonElement root, string? messageId, CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var dataProp))
        {
            await SendStreamResponseAsync(ws, messageId, 400, "missing data", "{\"response\": null}", ct);
            return;
        }

        var dataJson = dataProp.GetString();
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            await SendStreamResponseAsync(ws, messageId, 400, "empty data", "{\"response\": null}", ct);
            return;
        }

        using var dataDoc = JsonDocument.Parse(dataJson);
        var data = dataDoc.RootElement;

        var cfg = GetEffectiveConfig();
        var conversationId = GetString(data, "conversationId");
        var senderId = GetString(data, "senderId") ?? GetString(data, "senderStaffId");
        var senderNick = GetString(data, "senderNick");
        var conversationType = GetString(data, "conversationType");
        var msgId = GetString(data, "msgId");
        var text = ReadDingTalkText(data);
        var msgType = GetString(data, "msgtype") ?? GetString(data, "msgType") ?? "text";

        var chatbotUserId = GetString(data, "chatbotUserId");
        if (!string.IsNullOrWhiteSpace(chatbotUserId))
            _botUserId = chatbotUserId;

        // ── Message dedup ──
        if (!string.IsNullOrWhiteSpace(msgId) && !TryClaimDedup(msgId))
        {
            _logger.LogDebug("DingTalk duplicate message {MsgId} suppressed.", msgId);
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
            return;
        }

        // ── Extract ReplyToMessageId from reply messages ──
        string? replyToMessageId = null;
        if (GetBool(data, "isReplyMsg") && data.TryGetProperty("repliedMsg", out var repliedMsg))
        {
            replyToMessageId = GetString(repliedMsg, "msgId");
        }

        // ── Media file download ──
        var mediaText = await DownloadDingTalkMediaAsync(data, msgType, msgId, ct);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(mediaText))
        {
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(senderId))
        {
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
            return;
        }

        var isGroup = string.Equals(conversationType, "2", StringComparison.Ordinal);
        if (isGroup)
        {
            if (string.Equals(cfg.GroupPolicy, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("DingTalk group message dropped (GroupPolicy=disabled).");
                await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
                return;
            }
            if (string.Equals(cfg.GroupPolicy, "allowlist", StringComparison.OrdinalIgnoreCase) &&
                !IsGroupAllowed(conversationId, cfg))
            {
                _logger.LogDebug("DingTalk message from non-allowlisted group {GroupId} dropped.", conversationId);
                await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
                return;
            }
        }

        if (!IsUserAllowed(senderId, cfg))
        {
            _logger.LogDebug("DingTalk message from non-allowlisted user {UserId} dropped.", senderId);
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
            return;
        }

        var isInAtList = GetBool(data, "isInAtList");
        if (isGroup && cfg.RequireMentionInGroup && !isInAtList)
        {
            _logger.LogDebug("DingTalk group message dropped (RequireMentionInGroup=true and bot not mentioned).");
            await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
            return;
        }

        // ── Merge text and media markers ──
        var finalText = text ?? "";
        if (!string.IsNullOrWhiteSpace(mediaText))
            finalText = string.IsNullOrWhiteSpace(finalText) ? mediaText : finalText + "\n" + mediaText;

        if (finalText.Length > cfg.MaxInboundChars)
            finalText = finalText[..cfg.MaxInboundChars];

        var sessionWebhook = GetString(data, "sessionWebhook");
        var sessionWebhookExpiredTime = GetNullableLong(data, "sessionWebhookExpiredTime");
        CacheSessionWebhook(senderId, conversationId, sessionWebhook, sessionWebhookExpiredTime);

        string[]? mentions = null;
        if (data.TryGetProperty("atUsers", out var atUsers) && atUsers.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var mention in atUsers.EnumerateArray())
            {
                var mentionId = GetString(mention, "dingtalkId");
                if (!string.IsNullOrWhiteSpace(mentionId))
                    list.Add(mentionId);
            }
            if (list.Count > 0)
                mentions = [.. list];
        }

        var inbound = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            SenderName = senderNick,
            Text = finalText,
            MessageId = msgId,
            ReplyToMessageId = replyToMessageId,
            IsGroup = isGroup,
            GroupId = isGroup ? conversationId : null,
            MentionedIds = mentions,
            MediaType = msgType,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(inbound, ct);

        await SendStreamResponseAsync(ws, messageId, 200, "OK", "{\"response\": null}", ct);
    }

    private void CacheSessionWebhook(string? senderId, string? conversationId, string? webhookUrl, long? expiresAtMs)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;

        var expiresAtUtc = expiresAtMs is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs.Value) : (DateTimeOffset?)null;
        var state = new SessionWebhookState(webhookUrl, expiresAtUtc);

        if (!string.IsNullOrWhiteSpace(conversationId))
            _sessionWebhooks[conversationId] = state;
        if (!string.IsNullOrWhiteSpace(senderId))
            _sessionWebhooks[senderId] = state;
    }

    private bool TryGetSessionWebhook(string recipientId, out string webhookUrl)
    {
        webhookUrl = "";
        if (!_sessionWebhooks.TryGetValue(recipientId, out var state))
            return false;

        if (state.ExpiresAtUtc is { } expiresAtUtc && DateTimeOffset.UtcNow >= expiresAtUtc)
        {
            _sessionWebhooks.TryRemove(recipientId, out _);
            return false;
        }

        webhookUrl = state.WebhookUrl;
        return !string.IsNullOrWhiteSpace(webhookUrl);
    }

    private static async Task SendStreamResponseAsync(ClientWebSocket ws, string? messageId, int code, string message, string data, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId) || ws.State != WebSocketState.Open)
            return;

        var response = new DingTalkStreamResponse
        {
            Code = code,
            Message = message,
            Headers = new DingTalkStreamResponseHeaders
            {
                MessageId = messageId,
                ContentType = "application/json"
            },
            Data = data
        };

        var payload = JsonSerializer.Serialize(response, DingTalkJsonContext.Default.DingTalkStreamResponse);
        await SendWsTextAsync(ws, payload, ct);
    }

    private static async Task SendWsTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static string? ReadDingTalkText(JsonElement data)
    {
        if (data.TryGetProperty("text", out var textProp))
        {
            if (textProp.ValueKind == JsonValueKind.Object && textProp.TryGetProperty("content", out var contentProp))
                return contentProp.GetString();
            return textProp.GetString();
        }

        if (data.TryGetProperty("content", out var contentProp2))
            return contentProp2.GetString();

        return null;
    }




  


    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;

    private static bool GetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static string BuildPingOpaqueResponse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataProp))
            return "{\"opaque\":null}";

        if (dataProp.ValueKind == JsonValueKind.Object)
            return dataProp.GetRawText();

        var raw = dataProp.ValueKind == JsonValueKind.String ? dataProp.GetString() : dataProp.GetRawText();
        if (string.IsNullOrWhiteSpace(raw))
            return "{\"opaque\":null}";

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
            return raw;

        return $"{{\"opaque\":\"{JsonEncodedText.Encode(raw)}\"}}";
    }

    private static long? GetNullableLong(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.TryGetInt64(out var value) ? value : null;

    private static bool IsUserAllowed(string userId, DingTalkChannelConfig cfg)
    {
        if (cfg.AllowedFromUserIds.Length > 0)
            return Array.Exists(cfg.AllowedFromUserIds,
                id => string.Equals(id, userId, StringComparison.Ordinal));
        return true;
    }

    private static bool IsGroupAllowed(string? groupId, DingTalkChannelConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return false;
        if (cfg.AllowedGroupIds.Length > 0)
            return Array.Exists(cfg.AllowedGroupIds,
                id => string.Equals(id, groupId, StringComparison.Ordinal));
        return true;
    }

    // ──────────────────────────── Sending ────────────────────────────────────

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            return;

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);

            if (!string.IsNullOrWhiteSpace(remaining) && TryGetSessionWebhook(outbound.RecipientId, out var sessionWebhook))
            {
                await SendTextViaSessionWebhookAsync(sessionWebhook, remaining, ct);
            }
            else if (!string.IsNullOrWhiteSpace(remaining))
            {
                await RefreshAccessTokenAsync(ct);
                await SendTextMessageAsync(outbound.RecipientId, remaining, ct);
            }

            if (!string.IsNullOrWhiteSpace(remaining))
            {
                if (!TryGetSessionWebhook(outbound.RecipientId, out _))
                    await RefreshAccessTokenAsync(ct);
            }

            foreach (var marker in markers)
            {
                try
                {
                    await RefreshAccessTokenAsync(ct);
                    await SendMarkerAsync(outbound.RecipientId, marker, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send DingTalk media marker {Kind}={Value}.",
                        marker.Kind, marker.Value);
                }
            }

            if (string.IsNullOrWhiteSpace(remaining) && markers.Count == 0)
            {
                if (TryGetSessionWebhook(outbound.RecipientId, out var fallbackSessionWebhook))
                    await SendTextViaSessionWebhookAsync(fallbackSessionWebhook, outbound.Text, ct);
                else
                {
                    await RefreshAccessTokenAsync(ct);
                    await SendTextMessageAsync(outbound.RecipientId, outbound.Text, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DingTalk message to {RecipientId}.", outbound.RecipientId);
        }
    }

    private async Task SendTextViaSessionWebhookAsync(string sessionWebhook, string text, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new DingTalkSessionWebhookMessage
        {
            MsgType = "text",
            Text = new DingTalkSessionWebhookText { Content = text }
        }, DingTalkJsonContext.Default.DingTalkSessionWebhookMessage);

        using var request = new HttpRequestMessage(HttpMethod.Post, sessionWebhook);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("DingTalk sessionWebhook send failed: {Status} {Body}", response.StatusCode, body);
        }
    }

    private async Task SendTextMessageAsync(string recipientId, string text, CancellationToken ct)
    {
        const int maxBytes = 8000;
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = TruncateToMaxUtf8Bytes(text, maxBytes);

        var body = new DingTalkSendRequest
        {
            MsgType = "text",
            MsgParam = new DingTalkMsgParam { Text = text },
            ReceiverUserIdList = new[] { recipientId }
        };

        await SendDingTalkMessageAsync(recipientId, body, ct);
    }

    private async Task SendMarkerAsync(string recipientId, MediaMarker marker, CancellationToken ct)
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
                    var body = new DingTalkSendRequest
                    {
                        MsgType = "image",
                        MsgParam = new DingTalkMsgParam { ImageId = mediaId },
                        ReceiverUserIdList = new[] { recipientId }
                    };
                    await SendDingTalkMessageAsync(recipientId, body, ct);
                    break;
                }

            case MediaMarkerKind.FilePath:
            case MediaMarkerKind.FileUrl:
                {
                    var data = await FetchMediaBytesAsync(marker, ct);
                    if (data is null) return;
                    var name = Path.GetFileName(marker.Value).NullIfEmpty() ?? "file";
                    var mediaId = await UploadMediaAsync(data, "file", ct);
                    if (mediaId is null) return;
                    var body = new DingTalkSendRequest
                    {
                        MsgType = "file",
                        MsgParam = new DingTalkMsgParam { FileId = mediaId, FileName = name },
                        ReceiverUserIdList = new[] { recipientId }
                    };
                    await SendDingTalkMessageAsync(recipientId, body, ct);
                    break;
                }

            case MediaMarkerKind.AudioUrl:
            case MediaMarkerKind.VideoUrl:
                _logger.LogDebug("DingTalk does not support audio/video media markers directly.");
                break;
        }
    }

    private async Task SendDingTalkMessageAsync(string recipientId, DingTalkSendRequest body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, DingTalkJsonContext.Default.DingTalkSendRequest);
        var url = $"{DingTalkBase}/v1.0/im/messages?receiverUserId={Uri.EscapeDataString(recipientId)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("DingTalk send failed: {Status} {Body}", response.StatusCode, errBody);
        }
        else
        {
            _logger.LogInformation("Sent DingTalk message to {RecipientId}.", recipientId);
        }
    }

    // ──────────────────────────── Media upload ────────────────────────────────

    private async Task<string?> UploadMediaAsync(byte[] data, string mediaType, CancellationToken ct)
    {
        var endpoint = mediaType == "image" ? "/v1.0/im/images" : "/v1.0/im/files";
        var formKey = mediaType == "image" ? "image" : "file";

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(data), formKey, Guid.NewGuid().ToString("N") + (mediaType == "image" ? ".jpg" : ".bin"));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DingTalkBase}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("mediaId", out var mediaId))
            return mediaId.GetString();

        _logger.LogWarning("DingTalk media upload failed: {Body}", body);
        return null;
    }

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
            _logger.LogWarning(ex, "Failed to fetch media from {Source}.", marker.Value);
            return null;
        }
    }

    // ──────────────────────────── Authentication ─────────────────────────────

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-10))
            return;

        var body = JsonSerializer.Serialize(new DingTalkTokenRequest
        {
            AppKey = _appKey,
            AppSecret = _appSecret
        }, DingTalkJsonContext.Default.DingTalkTokenRequest);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{DingTalkBase}/v1.0/oauth2/accessToken");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("accessToken", out var tokenProp))
            throw new InvalidOperationException($"DingTalk token refresh failed: {responseBody}");

        _accessToken = tokenProp.GetString()
            ?? throw new InvalidOperationException("DingTalk returned a null accessToken.");

        var expireSec = root.TryGetProperty("expireIn", out var exp) ? exp.GetInt32() : 7200;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expireSec);

        _logger.LogInformation("DingTalk Access Token refreshed (expires in {Sec}s).", expireSec);
    }

    private async Task<string?> FetchBotUserIdAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DingTalkBase}/v1.0/robot/getInfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("robotUserId", out var robotUserId))
            {
                var userId = robotUserId.GetString();
                _logger.LogInformation("DingTalk bot userId fetched: {UserId}.", userId);
                return userId;
            }

            _logger.LogWarning("DingTalk getInfo did not return robotUserId: {Body}", body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch DingTalk bot userId; group @mention filtering will be unavailable.");
            return null;
        }
    }

    // ──────────────────────────── Dedup ──────────────────────────────────────

    /// <summary>Message dedup: check if msgId has been processed, mark it if not, and return true.</summary>
    private bool TryClaimDedup(string msgId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_dedup.TryGetValue(msgId, out var expMs) && expMs > now)
            return false;

        _dedup[msgId] = now + DedupTtlMs;

        // Periodically clean up expired entries
        if (_dedup.Count > DedupMaxSize)
            EvictExpiredDedup(now);

        return true;
    }

    private void EvictExpiredDedup(long now)
    {
        foreach (var key in _dedup.Keys.ToList())
        {
            if (_dedup.TryGetValue(key, out var expMs) && expMs <= now)
                _dedup.TryRemove(key, out _);
        }
    }

    // ──────────────────────────── Media download ──────────────────────────────

    /// <summary>
    /// Download media files (images/files) from DingTalk messages to temp directory,
    /// returning [IMAGE_PATH:...] or [FILE_PATH:...] markers.
    /// </summary>
    private async Task<string?> DownloadDingTalkMediaAsync(JsonElement data, string msgType, string? msgId, CancellationToken ct)
    {
        try
        {
            var downloadCode = GetString(data, "downloadCode");
            if (string.IsNullOrWhiteSpace(downloadCode) && data.TryGetProperty("content", out var content))
                downloadCode = GetString(content, "downloadCode");

            if (string.IsNullOrWhiteSpace(downloadCode))
                return null;

            // Download media via DingTalk file download API
            var url = $"{DingTalkBase}/v1.0/media/download?downloadCode={Uri.EscapeDataString(downloadCode)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("DingTalk media download failed for msgId={MsgId}: {Status}", msgId, response.StatusCode);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "application/pdf" => ".pdf",
                _ => msgType == "picture" || msgType == "image" ? ".jpg" : ".bin"
            };

            Directory.CreateDirectory(MediaTempDir);
            var filePath = Path.Combine(MediaTempDir, $"{Guid.NewGuid():N}{ext}");
            await using var fs = File.Create(filePath);
            await response.Content.CopyToAsync(fs, ct);

            _logger.LogInformation("DingTalk media downloaded for msgId={MsgId}: {Path}", msgId, filePath);
            return msgType == "picture" || msgType == "image"
                ? $"[IMAGE_PATH:{filePath}]"
                : $"[FILE_PATH:{filePath}]";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download DingTalk media for msgId={MsgId}.", msgId);
            return null;
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────────────

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
        _restartLock.Dispose();
    }
}

[JsonSerializable(typeof(DingTalkSendRequest))]
[JsonSerializable(typeof(DingTalkTokenRequest))]
[JsonSerializable(typeof(DingTalkGatewayConnectionRequest))]
[JsonSerializable(typeof(DingTalkGatewayConnectionResponse))]
[JsonSerializable(typeof(DingTalkStreamResponse))]
[JsonSerializable(typeof(DingTalkSessionWebhookMessage))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DingTalkJsonContext : JsonSerializerContext;

public sealed class DingTalkSendRequest
{
    [JsonPropertyName("msgType")]
    public string MsgType { get; set; } = "text";

    [JsonPropertyName("msgParam")]
    public required DingTalkMsgParam MsgParam { get; set; }

    [JsonPropertyName("receiverUserIdList")]
    public string[] ReceiverUserIdList { get; set; } = [];
}

public sealed class DingTalkMsgParam
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("imageId")]
    public string? ImageId { get; set; }

    [JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}

public sealed class DingTalkTokenRequest
{
    [JsonPropertyName("appKey")]
    public string? AppKey { get; set; }

    [JsonPropertyName("appSecret")]
    public string? AppSecret { get; set; }
}

public sealed class DingTalkGatewayConnectionRequest
{
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }

    [JsonPropertyName("clientSecret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("ua")]
    public string? Ua { get; set; }

    [JsonPropertyName("subscriptions")]
    public DingTalkSubscription[] Subscriptions { get; set; } = [];
}

public sealed class DingTalkSubscription
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }
}

public sealed class DingTalkGatewayConnectionResponse
{
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [JsonPropertyName("ticket")]
    public string? Ticket { get; set; }
}

public sealed class DingTalkStreamResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("headers")]
    public DingTalkStreamResponseHeaders Headers { get; set; } = new();

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public sealed class DingTalkStreamResponseHeaders
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
}

public sealed class DingTalkSessionWebhookMessage
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; set; } = "text";

    [JsonPropertyName("text")]
    public DingTalkSessionWebhookText Text { get; set; } = new();
}

public sealed class DingTalkSessionWebhookText
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal sealed record SessionWebhookState(string WebhookUrl, DateTimeOffset? ExpiresAtUtc);
