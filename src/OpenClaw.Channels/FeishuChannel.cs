using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// Feishu (Lark) channel adapter.
/// Uses WebSocket long connection to receive events and REST API to send messages.
/// Supports text, image, file, audio, and video.
/// Config hot-reload: change Feishu values in appsettings and the channel reconnects automatically.
/// </summary>
public sealed class FeishuChannel : IChannelAdapter, IRestartableChannelAdapter
{
    private const string FeishuBase = "https://open.feishu.cn/open-apis";

    // Client-side heartbeat interval — Feishu server expects periodic pings
    private const int HeartbeatIntervalMs = 120_000;

    private readonly FeishuChannelConfig _initialConfig;
    private readonly HttpClient _http;
    private readonly ILogger<FeishuChannel> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);

    // In-memory override applied via UpdateConfigAsync (takes precedence over initial config)
    private volatile FeishuChannelConfig? _runtimeOverride;

    // Derived credentials — refreshed on restart
    private string? _appId;
    private string? _appSecret;

    // Cached access token
    private string? _appAccessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // WebSocket state
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    // Application lifetime token saved during StartAsync so RestartAsync can link to it
    private CancellationToken _appLifetime = CancellationToken.None;

    // Parsed from the WS endpoint URL's service_id query param; used in binary ping frames
    private int _serviceId;

    // Bot's own open_id, fetched once after token refresh; used for @mention filtering
    private string? _botOpenId;

    // Two-layer dedup: memory TTL (5 min) + persistent disk (24 h) keyed on message_id.
    // Protects against Feishu event retries and replays after WebSocket reconnects / restarts.
    private readonly FeishuMessageDedup _dedup = new();

    public FeishuChannel(
        FeishuChannelConfig initialConfig,
        ILogger<FeishuChannel> logger)
    {
        _initialConfig = initialConfig;
        _logger = logger;
        _http = HttpClientFactory.Create();
    }

    public string ChannelId => "feishu";
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    /// <summary>
    /// Returns the current effective config: runtime override wins over initial config.
    /// </summary>
    public FeishuChannelConfig GetEffectiveConfig() => _runtimeOverride ?? _initialConfig;

    /// <summary>
    /// Returns the effective config with resolved credentials back-filled so the admin UI can display them.
    /// <c>AppSecret</c> is populated from the resolved <c>_appSecret</c> value (which may come from <c>AppSecretRef</c>).
    /// </summary>
    public FeishuChannelConfig GetEffectiveConfigForAdmin()
    {
        var cfg = _runtimeOverride ?? _initialConfig;
        if (!string.IsNullOrEmpty(_appSecret) && string.IsNullOrEmpty(cfg.AppSecret))
        {
            // Return a copy with the resolved secret filled in so it appears in the admin response.
            return new FeishuChannelConfig
            {
                Enabled                = cfg.Enabled,
                AppId                  = cfg.AppId,
                AppIdRef               = cfg.AppIdRef,
                AppSecret              = _appSecret,
                AppSecretRef           = cfg.AppSecretRef,
                GroupPolicy            = cfg.GroupPolicy,
                AllowedFromUserIds     = cfg.AllowedFromUserIds,
                AllowedGroupIds        = cfg.AllowedGroupIds,
                MaxInboundChars        = cfg.MaxInboundChars,
                RequireMentionInGroup  = cfg.RequireMentionInGroup,
                ExposeInboundMediaUrls = cfg.ExposeInboundMediaUrls,
            };
        }
        return cfg;
    }

    /// <summary>
    /// Applies a config override in memory WITHOUT reconnecting.
    /// Call this at startup (before <see cref="StartAsync"/>) to restore a persisted config.
    /// Pass <c>null</c> to clear the override and revert to appsettings.
    /// </summary>
    public void SetRuntimeConfig(FeishuChannelConfig? cfg) => _runtimeOverride = cfg;

    /// <summary>
    /// Applies an in-memory config override and reconnects the WebSocket.
    /// Call this from an admin endpoint instead of editing appsettings.
    /// </summary>
    public async Task UpdateConfigAsync(FeishuChannelConfig newConfig, CancellationToken ct = default)
    {
        SetRuntimeConfig(newConfig);
        _logger.LogInformation("Feishu runtime config updated via API — reconnecting.");
        await RestartAsync(ct);
    }

    // ──────────────────────────── Lifecycle ──────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        _appLifetime = ct;

        var cfg = GetEffectiveConfig();
        if (!cfg.Enabled)
        {
            _logger.LogInformation("Feishu channel is disabled; set Enabled=true via admin API to activate.");
            return;
        }

        ResolveCredentials(cfg);
        if (!ValidateCredentials())
            return;

        // Pre-load previously seen message_ids from disk so replays are caught immediately.
        _dedup.Warmup(_appId!, _logger);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunWsLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await _restartLock.WaitAsync(ct);
        try
        {
            // Cancel and await the old loop
            if (_cts is not null)
            {
                await _cts.CancelAsync();
                if (_receiveLoop is not null)
                {
                    try { await _receiveLoop; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogDebug(ex, "Feishu receive loop exited with error during restart."); }
                }
                _cts.Dispose();
                _cts = null;
                _receiveLoop = null;
            }

            var cfg = GetEffectiveConfig();
            if (!cfg.Enabled)
            {
                _logger.LogInformation("Feishu channel disabled after config hot-reload.");
                return;
            }

            ResolveCredentials(cfg);
            if (!ValidateCredentials())
                return;

            // Invalidate cached token — credentials may have changed
            _appAccessToken = null;
            _tokenExpiry = DateTimeOffset.MinValue;
            _botOpenId = null;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime);
            _receiveLoop = RunWsLoopAsync(_cts.Token);
            _logger.LogInformation("Feishu channel restarted with updated config.");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private void ResolveCredentials(FeishuChannelConfig cfg)
    {
        _appId = SecretResolver.Resolve(cfg.AppIdRef) ?? cfg.AppId;
        _appSecret = SecretResolver.Resolve(cfg.AppSecretRef) ?? cfg.AppSecret;
    }

    private bool ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_appId) || string.IsNullOrWhiteSpace(_appSecret))
        {
            _logger.LogError("Feishu AppId or AppSecret is not configured; channel will not start.");
            return false;
        }
        return true;
    }

    // ──────────────────────────── WebSocket loop ─────────────────────────────

    private async Task RunWsLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        const int maxBackoffSec = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAppTokenAsync(ct);
                if (string.IsNullOrEmpty(_botOpenId))
                    _botOpenId = await FetchBotOpenIdAsync(ct);
                var wsUrl = await GetWsEndpointAsync(ct);
                _serviceId = ParseServiceId(wsUrl);

                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(wsUrl), ct);
                _logger.LogInformation("Feishu WebSocket connected.");

                backoff = TimeSpan.FromSeconds(2); // reset on successful connect
                await ProcessWsMessagesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feishu WebSocket error. Reconnecting in {Sec}s.", backoff.TotalSeconds);
            }
            finally
            {
                if (_ws?.State == WebSocketState.Open)
                {
                    try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                    catch { /* best-effort */ }
                }
                _ws?.Dispose();
                _ws = null;
            }

            if (!ct.IsCancellationRequested)
            {
                // Suppress OCE so the while-condition handles cancellation cleanly
                await Task.Delay(backoff, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoffSec));
        }
    }

    private async Task ProcessWsMessagesAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunClientHeartbeatAsync(heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                buffer.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    var mem = buffer.GetMemory(8192);
                    result = await _ws.ReceiveAsync(mem, ct);
                    buffer.Advance(result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // Feishu long-connection protocol uses binary WebSocket frames
                // encoded as protobuf (pbbp2 format). Text frames are unexpected.
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    _logger.LogDebug("Feishu WS received unexpected non-binary frame; skipping.");
                    continue;
                }

                try
                {
                    var frame = Pbbp2.DecodeFrame(buffer.WrittenSpan);

                    // Method 0 = FrameTypeControl, Method 1 = FrameTypeData
                    switch (frame.Method)
                    {
                        case 0:
                            await HandleBinaryControlFrameAsync(frame, ct);
                            break;
                        case 1:
                            await HandleBinaryDataFrameAsync(frame, ct);
                            break;
                        default:
                            _logger.LogDebug("Feishu WS unknown frame method {Method}.", frame.Method);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Feishu WS frame decode/dispatch error ({Bytes} bytes).", buffer.WrittenCount);
                }
            }
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunClientHeartbeatAsync(CancellationToken ct)
    {
        // First heartbeat after 30s, then every HeartbeatIntervalMs
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        while (!ct.IsCancellationRequested)
        {
            // Send binary pbbp2-encoded ping (Feishu WS long-connection protocol requirement)
            var pingHeaders = new List<(string Key, string Value)> { ("type", "ping") };
            var pingBytes = Pbbp2.EncodeFrame(new FsFrame(0, 0, _serviceId, 0, pingHeaders, null));
            await SendWsBinaryAsync(pingBytes, ct);
            await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // ──────────────────────────── Control / data frame handling ─────────────

    private async Task HandleBinaryControlFrameAsync(FsFrame frame, CancellationToken ct)
    {
        var type = frame.GetHeader("type");
        switch (type)
        {
            case "hello":
                _logger.LogInformation("Feishu WS connection acknowledged (hello).");
                break;

            case "ping":
                // Mirror the received frame back as a pong (same SeqId/LogId/Service)
                var pongHeaders = new List<(string Key, string Value)> { ("type", "pong") };
                var pongBytes = Pbbp2.EncodeFrame(new FsFrame(frame.SeqId, frame.LogId, frame.Service, 0, pongHeaders, null));
                await SendWsBinaryAsync(pongBytes, ct);
                break;

            case "pong":
                // Our keepalive was acknowledged — nothing to do
                break;

            default:
                _logger.LogDebug("Feishu WS unhandled control type: {Type}.", type);
                break;
        }
    }

    private async Task HandleBinaryDataFrameAsync(FsFrame frame, CancellationToken ct)
    {
        var type = frame.GetHeader("type");
        if (!string.Equals(type, "event", StringComparison.Ordinal))
        {
            _logger.LogDebug("Feishu WS data frame type={Type} (skipped).", type);
            await SendDataFrameResponseAsync(frame, 200, ct);
            return;
        }

        if (frame.Payload is not { Length: > 0 })
        {
            _logger.LogWarning("Feishu WS event frame has empty payload.");
            await SendDataFrameResponseAsync(frame, 400, ct);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(frame.Payload);
            await HandleEventEnvelopeAsync(doc.RootElement, ct);
            await SendDataFrameResponseAsync(frame, 200, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Feishu WS event payload JSON parse error.");
            await SendDataFrameResponseAsync(frame, 500, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Feishu WS event processing error.");
            await SendDataFrameResponseAsync(frame, 500, ct);
        }
    }

    private async Task SendDataFrameResponseAsync(FsFrame receivedFrame, int statusCode, CancellationToken ct)
    {
        var payloadBytes = Encoding.UTF8.GetBytes($"{{\"code\":{statusCode},\"headers\":null,\"data\":null}}");
        var responseBytes = Pbbp2.EncodeFrame(new FsFrame(
            receivedFrame.SeqId, receivedFrame.LogId, receivedFrame.Service, 1,
            receivedFrame.Headers, payloadBytes));
        await SendWsBinaryAsync(responseBytes, ct);
    }

    // ──────────────────────────── Event envelope handling ────────────────────

    private async Task HandleEventEnvelopeAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("header", out var header))
            return;

        var eventType = header.TryGetProperty("event_type", out var et) ? et.GetString() : null;

        if (!root.TryGetProperty("event", out var eventData))
            return;

        _logger.LogDebug("Feishu event type={EventType}.", eventType);

        if (string.Equals(eventType, "im.message.receive_v1", StringComparison.Ordinal))
        {
            // Deduplicate by message_id (stable across Feishu retries; event_id may differ on re-delivery).
            // Two-layer cache: memory (5 min) catches rapid retries; disk (24 h) catches replays after
            // WebSocket reconnects and process restarts.
            var messageId = eventData.TryGetProperty("message", out var msgEl) &&
                            msgEl.TryGetProperty("message_id", out var midEl)
                ? midEl.GetString() : null;

            if (!string.IsNullOrEmpty(messageId) &&
                !await _dedup.TryClaimAsync(messageId, _appId ?? "default", _logger, ct))
                return;

            await HandleMessageReceiveV1Async(eventData, ct);
        }
        else
        {
            _logger.LogDebug("Feishu unhandled event type: {EventType}.", eventType);
        }
    }

    private async Task HandleMessageReceiveV1Async(JsonElement evt, CancellationToken ct)
    {
        if (!evt.TryGetProperty("sender", out var sender) ||
            !evt.TryGetProperty("message", out var msg))
            return;

        // Only handle user-sent messages (not bots)
        var senderType = sender.TryGetProperty("sender_type", out var st) ? st.GetString() : null;
        if (!string.Equals(senderType, "user", StringComparison.Ordinal))
            return;

        if (!sender.TryGetProperty("sender_id", out var senderId))
            return;

        var openId = senderId.TryGetProperty("open_id", out var oid) ? oid.GetString() : null;
        if (string.IsNullOrWhiteSpace(openId))
            return;

        var chatId = msg.TryGetProperty("chat_id", out var cid) ? cid.GetString() : null;
        var chatType = msg.TryGetProperty("chat_type", out var ct2) ? ct2.GetString() : "p2p";
        var msgId = msg.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
        var msgType = msg.TryGetProperty("message_type", out var mtype) ? mtype.GetString() : "text";
        var contentJson = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
        var parentId = msg.TryGetProperty("parent_id", out var pid) ? pid.GetString() : null;
        var senderName = sender.TryGetProperty("sender_id", out var sidObj)
            && sidObj.TryGetProperty("union_id", out var uid) ? uid.GetString() : null;

        if (string.IsNullOrWhiteSpace(chatId))
            return;

        var isGroup = string.Equals(chatType, "group", StringComparison.Ordinal);
        var cfg = GetEffectiveConfig();

        // Group policy check
        if (isGroup)
        {
            if (string.Equals(cfg.GroupPolicy, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Feishu group message dropped (GroupPolicy=disabled).");
                return;
            }
            if (string.Equals(cfg.GroupPolicy, "allowlist", StringComparison.OrdinalIgnoreCase) &&
                !IsGroupAllowed(chatId, cfg))
            {
                _logger.LogDebug("Feishu message from non-allowlisted group {GroupId} dropped.", chatId);
                return;
            }
        }

        // User allowlist
        if (!IsUserAllowed(openId, cfg))
        {
            _logger.LogDebug("Feishu message from non-allowlisted user {UserId} dropped.", openId);
            return;
        }

        // Extract mentions (open_ids of mentioned users) — needed before @mention gate
        string[]? mentions = null;
        if (msg.TryGetProperty("mentions", out var mentionsArr) &&
            mentionsArr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var m in mentionsArr.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var mentionId) &&
                    mentionId.TryGetProperty("open_id", out var mOid))
                {
                    var mOpenId = mOid.GetString();
                    if (mOpenId is not null)
                        list.Add(mOpenId);
                }
            }
            if (list.Count > 0)
                mentions = [.. list];
        }

        // @mention gate: when RequireMentionInGroup is enabled, only process messages that
        // explicitly @mention this bot. This prevents responding to every group message when
        // multiple bots coexist in the same group.
        if (isGroup && cfg.RequireMentionInGroup)
        {
            if (string.IsNullOrEmpty(_botOpenId))
            {
                // Bot open_id not yet fetched (API call pending or failed).
                // Fall through and process the message to avoid silent blackout;
                // mention filtering is unavailable until open_id is resolved.
                _logger.LogWarning("Feishu @mention gate skipped: bot open_id not available. Message will be processed without mention filtering.");
            }
            else if (mentions is null || mentions.Length == 0 ||
                !Array.Exists(mentions, id => string.Equals(id, _botOpenId, StringComparison.Ordinal)))
            {
                _logger.LogDebug("Feishu group message dropped (RequireMentionInGroup=true and bot not @mentioned).");
                return;
            }
        }

        // Parse message content
        var (text, mediaType, mediaUrl, mediaFileName) =
            ParseMessageContent(msgType, contentJson, msgId, cfg.ExposeInboundMediaUrls);

        if (string.IsNullOrWhiteSpace(text) && mediaType is null)
            return;

        // Download media (image/file/audio/video/post-images) from Feishu REST API and save
        // to a local temp file so the agent can pass it to vision models / read-file tools.
        // On success the [IMAGE_PATH:…] / [FILE_PATH:…] marker is embedded in the message text;
        // mediaType and mediaUrl are cleared so GatewayWorkers won't emit a second
        // (non-accessible) [IMAGE_URL:feishu-resource://…] marker.
        var isPostMsg = string.Equals(msgType, "post", StringComparison.Ordinal);
        if ((mediaType is not null || isPostMsg)
            && !string.IsNullOrWhiteSpace(contentJson)
            && !string.IsNullOrWhiteSpace(msgId))
        {
            var (pathMarker, isImage) = await TryDownloadInboundMediaAsync(
                msgType ?? string.Empty, contentJson, msgId, ct);

            if (pathMarker is not null)
            {
                // Images (standalone): replace the "[image]" placeholder with the path marker.
                // Post / files / audio / video: keep the human-readable label, append marker(s).
                text = (isImage && !isPostMsg)
                    ? pathMarker
                    : (string.IsNullOrWhiteSpace(text) ? pathMarker : $"{text}\n{pathMarker}");
                mediaType = null;
                mediaUrl = null;
            }
        }

        // If this message is a quoted reply to another message, try to download
        // the parent message's media so the agent has access to the referenced file/image.
        // This lets users: (1) send a file, (2) reply to it and @mention the bot.
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parentMarker = await TryDownloadParentMessageMediaAsync(parentId, ct);
            if (parentMarker is not null)
                text = string.IsNullOrWhiteSpace(text) ? parentMarker : $"{text}\n{parentMarker}";
        }

        if (!string.IsNullOrWhiteSpace(text) && text.Length > cfg.MaxInboundChars)
            text = text[..cfg.MaxInboundChars];

        var inbound = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = openId,
            SenderName = senderName,
            Text = text ?? string.Empty,
            MessageId = msgId,
            ReplyToMessageId = string.IsNullOrWhiteSpace(parentId) ? null : parentId,
            IsGroup = isGroup,
            GroupId = isGroup ? chatId : null,
            MentionedIds = mentions,
            MediaType = mediaType,
            MediaUrl = mediaUrl,
            MediaFileName = mediaFileName,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(inbound, ct);
    }

    // ──────────────────────────── Content parsing ────────────────────────────

    private static (string? text, string? mediaType, string? mediaUrl, string? fileName) ParseMessageContent(
        string? msgType,
        string? contentJson,
        string? msgId,
        bool exposeMediaUrls)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
            return (null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            return msgType switch
            {
                "text" => (
                    root.TryGetProperty("text", out var t) ? t.GetString() : null,
                    null, null, null),

                "image" => (
                    "[image]",
                    "image",
                    exposeMediaUrls && root.TryGetProperty("image_key", out var ik)
                        ? BuildResourceUrl(ik.GetString(), msgId, "image") : null,
                    null),

                "file" => (
                    root.TryGetProperty("file_name", out var fn)
                        ? $"[file: {fn.GetString()}]" : "[file]",
                    "document",
                    exposeMediaUrls && root.TryGetProperty("file_key", out var fk)
                        ? BuildResourceUrl(fk.GetString(), msgId, "file") : null,
                    root.TryGetProperty("file_name", out var fn2) ? fn2.GetString() : null),

                "audio" => (
                    "[audio]",
                    "audio",
                    exposeMediaUrls && root.TryGetProperty("file_key", out var ak)
                        ? BuildResourceUrl(ak.GetString(), msgId, "file") : null,
                    null),

                "video" or "media" => (
                    "[video]",
                    "video",
                    exposeMediaUrls && root.TryGetProperty("file_key", out var vk)
                        ? BuildResourceUrl(vk.GetString(), msgId, "file") : null,
                    null),

                "sticker" => ("[sticker]", null, null, null),

                "post" => (
                    root.TryGetProperty("zh_cn", out var zh) ? ExtractPostText(zh) :
                    root.TryGetProperty("en_us", out var en) ? ExtractPostText(en) :
                    ExtractPostText(root),  // flat structure: title/content at root level
                    null, null, null),

                _ => (contentJson, null, null, null)
            };
        }
        catch
        {
            return (contentJson, null, null, null);
        }
    }

    private static string? BuildResourceUrl(string? fileKey, string? msgId, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(fileKey) || string.IsNullOrWhiteSpace(msgId))
            return null;
        return $"feishu-resource://{msgId}/{fileKey}?type={resourceType}";
    }

    /// <summary>
    /// Downloads an image or file from Feishu REST API and saves it to a local temp file.
    /// Returns a tuple of (pathMarker, isImage):
    ///   - pathMarker is "[IMAGE_PATH:/tmp/...]" for images or "[FILE_PATH:/tmp/...]" for files.
    ///   - isImage indicates whether the download was an image (for caller to decide text layout).
    /// Returns (null, false) if the download fails or the message type is unsupported.
    /// </summary>
    private async Task<(string? pathMarker, bool isImage)> TryDownloadInboundMediaAsync(
        string msgType,
        string contentJson,
        string msgId,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            string? mediaKey;
            string apiType;
            bool isImage;
            string? fileName = null;

            switch (msgType)
            {
                case "image":
                    mediaKey = root.TryGetProperty("image_key", out var ik) ? ik.GetString() : null;
                    apiType = "image";
                    isImage = true;
                    break;

                case "file":
                    mediaKey = root.TryGetProperty("file_key", out var fk) ? fk.GetString() : null;
                    fileName = root.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                    apiType = "file";
                    isImage = false;
                    break;

                case "audio":
                    mediaKey = root.TryGetProperty("file_key", out var ak) ? ak.GetString() : null;
                    apiType = "file";
                    isImage = false;
                    break;

                case "video":
                case "media":
                    mediaKey = root.TryGetProperty("file_key", out var vk) ? vk.GetString() : null;
                    fileName = root.TryGetProperty("file_name", out var vfn) ? vfn.GetString() : null;
                    apiType = "file";
                    isImage = false;
                    break;

                case "post":
                {
                    // Rich-text post: extract every img tag's image_key and download each.
                    var markers = await DownloadPostImagesAsync(root, msgId, ct);
                    return (markers.Length > 0 ? markers : null, false);
                }

                default:
                    return (null, false);
            }

            if (string.IsNullOrWhiteSpace(mediaKey))
                return (null, false);

            var tempPath = await DownloadResourceToTempFileAsync(msgId, mediaKey!, apiType, isImage, fileName, msgType, ct);
            if (tempPath is null) return (null, false);

            var marker = isImage ? $"[IMAGE_PATH:{tempPath}]" : $"[FILE_PATH:{tempPath}]";
            return (marker, isImage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feishu media download failed for {MsgType} in message {MsgId}.", msgType, msgId);
            return (null, false);
        }
    }

    /// <summary>
    /// Fetches a parent/quoted message by ID and downloads its media attachment.
    /// Only acts on media types (file/image/audio/video/media/post).
    /// Returns "[FILE_PATH:…]" or "[IMAGE_PATH:…]" markers, or null if no downloadable media.
    /// </summary>
    private async Task<string?> TryDownloadParentMessageMediaAsync(string parentMsgId, CancellationToken ct)
    {
        try
        {
            var url = $"{FeishuBase}/im/v1/messages/{Uri.EscapeDataString(parentMsgId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appAccessToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Feishu fetch parent message {ParentId} HTTP {Status}.", parentMsgId, (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;

            // Response is an array; we want the first (and only) message item.
            JsonElement? firstItem = null;
            foreach (var item in items.EnumerateArray()) { firstItem = item; break; }
            if (firstItem is null) return null;

            var first = firstItem.Value;
            var parentMsgType = first.TryGetProperty("msg_type", out var mt) ? mt.GetString() : null;
            if (string.IsNullOrEmpty(parentMsgType)) return null;

            // Only download if the parent is a media message (ignore text-only parents).
            if (parentMsgType is not ("file" or "image" or "audio" or "video" or "media" or "post"))
                return null;

            if (!first.TryGetProperty("body", out var body)) return null;
            var contentJson = body.TryGetProperty("content", out var contentProp) ? contentProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(contentJson)) return null;

            _logger.LogInformation("Feishu parent message {ParentId} is {MsgType}; downloading media.", parentMsgId, parentMsgType);
            var (marker, _) = await TryDownloadInboundMediaAsync(parentMsgType, contentJson, parentMsgId, ct);
            return marker;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feishu parent message {ParentId} media download failed.", parentMsgId);
            return null;
        }
    }

    /// <summary>
    /// Extracts all img tags from a post content element (handles both flat and zh_cn/en_us
    /// wrapped structures), downloads each image, and returns newline-joined [IMAGE_PATH:…] markers.
    /// </summary>
    private async Task<string> DownloadPostImagesAsync(JsonElement root, string msgId, CancellationToken ct)
    {
        // Support both flat structure {title, content:[…]} and wrapped {zh_cn:{title, content:[…]}}
        var postElem = root.TryGetProperty("zh_cn", out var zh) ? zh :
                       root.TryGetProperty("en_us", out var en) ? en : root;

        if (!postElem.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var para in content.EnumerateArray())
        {
            if (para.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in para.EnumerateArray())
            {
                if (!item.TryGetProperty("tag", out var tag)) continue;
                if (!string.Equals(tag.GetString(), "img", StringComparison.Ordinal)) continue;
                if (!item.TryGetProperty("image_key", out var imgKeyProp)) continue;
                var imgKey = imgKeyProp.GetString();
                if (string.IsNullOrWhiteSpace(imgKey)) continue;

                var tempPath = await DownloadResourceToTempFileAsync(
                    msgId, imgKey, "image", isImage: true, fileName: null, msgType: "post", ct);
                if (tempPath is not null)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"[IMAGE_PATH:{tempPath}]");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Performs the actual HTTP download of a Feishu message resource (image or file) and
    /// saves the bytes to a temp file under %TEMP%/openclaw_feishu/.
    /// Returns the local file path on success, or null on failure.
    /// </summary>
    private async Task<string?> DownloadResourceToTempFileAsync(
        string msgId, string mediaKey, string apiType, bool isImage,
        string? fileName, string msgType, CancellationToken ct)
    {
        var url = $"{FeishuBase}/im/v1/messages/{Uri.EscapeDataString(msgId)}/resources/{Uri.EscapeDataString(mediaKey)}?type={apiType}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appAccessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Feishu media download HTTP {Status} for {MsgType} message={MsgId} key={Key}.",
                (int)resp.StatusCode, msgType, msgId, mediaKey);
            return null;
        }

        // Feishu returns JSON (not binary) on permission errors even with 2xx status.
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Feishu media download returned error JSON for {MsgType} message={MsgId}: {Body}",
                msgType, msgId, errorBody);
            return null;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
        {
            _logger.LogWarning("Feishu media download returned empty body for {MsgType} message={MsgId}.", msgType, msgId);
            return null;
        }

        var ext = isImage
            ? GuessImageExtension(contentType)
            : (fileName is not null
                ? Path.GetExtension(fileName)
                : GuessFileExtension(contentType, msgType));

        if (string.IsNullOrEmpty(ext))
            ext = isImage ? ".jpg" : ".bin";

        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw_feishu");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");

        await File.WriteAllBytesAsync(tempFile, bytes, ct);
        _logger.LogInformation("Feishu {MsgType} saved: {Bytes} bytes → {TempFile}.", msgType, bytes.Length, tempFile);
        return tempFile;
    }

    private static string GuessImageExtension(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif"                 => ".gif",
            "image/webp"                => ".webp",
            "image/bmp"                 => ".bmp",
            _                           => ".png",
        };

    private static string GuessFileExtension(string contentType, string msgType)
    {
        if (msgType == "audio") return ".opus";
        if (msgType is "video" or "media") return ".mp4";
        return contentType.ToLowerInvariant() switch
        {
            "application/pdf"      => ".pdf",
            "audio/opus"           => ".opus",
            "audio/ogg"            => ".ogg",
            "audio/mpeg"           => ".mp3",
            "video/mp4"            => ".mp4",
            "application/msword"   => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"       => ".xlsx",
            _                      => ".bin",
        };
    }

    private static string ExtractPostText(JsonElement postElem)
    {
        var sb = new StringBuilder();

        if (postElem.TryGetProperty("title", out var title))
        {
            var titleText = title.GetString();
            if (!string.IsNullOrWhiteSpace(titleText))
                sb.AppendLine(titleText);
        }

        if (postElem.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var para in content.EnumerateArray())
            {
                if (para.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in para.EnumerateArray())
                {
                    if (!item.TryGetProperty("tag", out var tag)) continue;
                    var tagName = tag.GetString();

                    if (string.Equals(tagName, "text", StringComparison.Ordinal) &&
                        item.TryGetProperty("text", out var text))
                    {
                        sb.Append(text.GetString());
                    }
                    else if (string.Equals(tagName, "img", StringComparison.Ordinal))
                    {
                        // placeholder so downstream knows an image exists;
                        // actual pixel data is downloaded by TryDownloadInboundMediaAsync.
                        sb.Append("[Image]");
                    }
                    else if (string.Equals(tagName, "at", StringComparison.Ordinal) &&
                             item.TryGetProperty("user_name", out var uname))
                    {
                        sb.Append($"@{uname.GetString()}");
                    }
                }
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    // ──────────────────────────── Sending ────────────────────────────────────

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            return;

        try
        {
            await RefreshAppTokenAsync(ct);

            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);

            // Send text portion (if any) first
            if (!string.IsNullOrWhiteSpace(remaining))
                await SendTextMessageAsync(outbound.RecipientId, remaining, ct);

            // Send each media attachment
            foreach (var marker in markers)
            {
                try
                {
                    await SendMarkerAsync(outbound.RecipientId, marker, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send Feishu media marker {Kind}={Value}.",
                        marker.Kind, marker.Value);
                }
            }

            // No text and no markers — send the full text as-is
            if (string.IsNullOrWhiteSpace(remaining) && markers.Count == 0)
                await SendTextMessageAsync(outbound.RecipientId, outbound.Text, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Feishu message to {RecipientId}.", outbound.RecipientId);
        }
    }

    private async Task SendTextMessageAsync(string recipientId, string text, CancellationToken ct)
    {
        // Feishu text limit is ~30 KB
        const int maxBytes = 30_000;
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = TruncateToMaxUtf8Bytes(text, maxBytes);

        var contentJson = $"{{\"text\":{JsonEscapeString(text)}}}";
        await SendFeishuMessageAsync(recipientId, "text", contentJson, ct);
    }

    private async Task SendMarkerAsync(string recipientId, MediaMarker marker, CancellationToken ct)
    {
        switch (marker.Kind)
        {
            case MediaMarkerKind.ImageUrl:
            case MediaMarkerKind.ImagePath:
            {
                var data = await FetchMediaBytesAsync(marker, ct);
                if (data is null) return;
                var imageKey = await UploadImageAsync(data, ct);
                if (imageKey is null) return;
                var content = $"{{\"image_key\":{JsonEscapeString(imageKey)}}}";
                await SendFeishuMessageAsync(recipientId, "image", content, ct);
                break;
            }

            case MediaMarkerKind.FileUrl:
            case MediaMarkerKind.FilePath:
            {
                var data = await FetchMediaBytesAsync(marker, ct);
                if (data is null) return;
                var name = Path.GetFileName(marker.Value).NullIfEmpty() ?? "file";
                var fileKey = await UploadFileAsync(data, name, GuessFileType(name), ct);
                if (fileKey is null) return;
                var content = $"{{\"file_key\":{JsonEscapeString(fileKey)}}}";
                await SendFeishuMessageAsync(recipientId, "file", content, ct);
                break;
            }

            case MediaMarkerKind.VideoUrl:
            {
                var data = await FetchMediaBytesAsync(marker, ct);
                if (data is null) return;
                var name = Path.GetFileName(marker.Value).NullIfEmpty() ?? "video.mp4";
                var fileKey = await UploadFileAsync(data, name, "mp4", ct);
                if (fileKey is null) return;
                var content = $"{{\"file_key\":{JsonEscapeString(fileKey)}}}";
                await SendFeishuMessageAsync(recipientId, "media", content, ct);
                break;
            }

            case MediaMarkerKind.AudioUrl:
            {
                var data = await FetchMediaBytesAsync(marker, ct);
                if (data is null) return;
                var name = Path.GetFileName(marker.Value).NullIfEmpty() ?? "audio.opus";
                var fileKey = await UploadFileAsync(data, name, "opus", ct);
                if (fileKey is null) return;
                var content = $"{{\"file_key\":{JsonEscapeString(fileKey)}}}";
                await SendFeishuMessageAsync(recipientId, "audio", content, ct);
                break;
            }

            // DocumentUrl and StickerUrl not applicable for Feishu — ignore silently
        }
    }

    private async Task SendFeishuMessageAsync(
        string recipientId,
        string msgType,
        string contentJson,
        CancellationToken ct)
    {
        // Chat IDs start with "oc_", user open_ids start with "ou_"
        var receiveIdType = recipientId.StartsWith("oc_", StringComparison.Ordinal)
            ? "chat_id" : "open_id";

        var body =
            $"{{\"receive_id\":{JsonEscapeString(recipientId)}," +
            $"\"msg_type\":{JsonEscapeString(msgType)}," +
            $"\"content\":{JsonEscapeString(contentJson)}}}";

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FeishuBase}/im/v1/messages?receive_id_type={receiveIdType}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appAccessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Feishu send failed: {Status} {Body}", resp.StatusCode, errBody);
        }
        else
        {
            _logger.LogInformation("Sent Feishu {MsgType} message to {RecipientId}.", msgType, recipientId);
        }
    }

    // ──────────────────────────── Media upload ────────────────────────────────

    private async Task<string?> UploadImageAsync(byte[] data, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("message"), "image_type");
        content.Add(new ByteArrayContent(data), "image", "image.jpg");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{FeishuBase}/im/v1/images");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appAccessToken);
        req.Content = content;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
            root.TryGetProperty("data", out var data2) &&
            data2.TryGetProperty("image_key", out var imageKey))
        {
            return imageKey.GetString();
        }

        _logger.LogWarning("Feishu image upload failed: {Body}", body);
        return null;
    }

    private async Task<string?> UploadFileAsync(
        byte[] data,
        string fileName,
        string fileType,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(fileType), "file_type");
        content.Add(new StringContent(fileName), "file_name");
        content.Add(new ByteArrayContent(data), "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{FeishuBase}/im/v1/files");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appAccessToken);
        req.Content = content;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0 &&
            root.TryGetProperty("data", out var data2) &&
            data2.TryGetProperty("file_key", out var fileKey))
        {
            return fileKey.GetString();
        }

        _logger.LogWarning("Feishu file upload failed: {Body}", body);
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

    /// <summary>
    /// Gets or refreshes the App Access Token.
    /// Tokens are cached and renewed 10 minutes before expiry.
    /// </summary>
    private async Task RefreshAppTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_appAccessToken) &&
            DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-10))
            return;

        var body =
            $"{{\"app_id\":{JsonEscapeString(_appId)}," +
            $"\"app_secret\":{JsonEscapeString(_appSecret)}}}";

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{FeishuBase}/auth/v3/app_access_token/internal");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("app_access_token", out var tokenProp))
            throw new InvalidOperationException($"Feishu token refresh failed: {responseBody}");

        _appAccessToken = tokenProp.GetString()
            ?? throw new InvalidOperationException("Feishu returned a null app_access_token.");

        var expireSec = root.TryGetProperty("expire", out var exp) ? exp.GetInt32() : 7200;
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expireSec);

        _logger.LogInformation("Feishu App Access Token refreshed (expires in {Sec}s).", expireSec);
    }

    /// <summary>
    /// Fetches this bot's own open_id via /bot/v3/info.
    /// Used to determine whether the bot is @mentioned in group messages.
    /// </summary>
    private async Task<string?> FetchBotOpenIdAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{FeishuBase}/bot/v3/info");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _appAccessToken);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("bot", out var bot) &&
                bot.TryGetProperty("open_id", out var oid))
            {
                var openId = oid.GetString();
                _logger.LogInformation("Feishu bot open_id fetched: {OpenId}.", openId);
                return openId;
            }

            _logger.LogWarning("Feishu /bot/v3/info did not return open_id: {Body}", body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Feishu bot open_id; group @mention filtering will be unavailable until next reconnect.");
            return null;
        }
    }

    /// <summary>
    /// Requests a WebSocket endpoint URL for the long connection.
    /// Each call obtains a fresh URL (tokens are embedded in the URL).
    /// </summary>
    private async Task<string> GetWsEndpointAsync(CancellationToken ct)
    {
        // Correct endpoint per official oapi-sdk-go: POST https://open.feishu.cn/callback/ws/endpoint
        // Body must include both AppID and AppSecret (not a Bearer-token call).
        var body = $"{{\"AppID\":{JsonEscapeString(_appId)},\"AppSecret\":{JsonEscapeString(_appSecret ?? "")}}}";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/callback/ws/endpoint");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("Feishu WS endpoint raw response ({Status}): {Body}", (int)resp.StatusCode, responseBody);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Feishu WS endpoint returned non-JSON (HTTP {(int)resp.StatusCode}): {responseBody}", ex);
        }

        using (doc)
        {
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException(
                $"Feishu WS endpoint response missing 'data' (code={GetCode(root)}): {responseBody}");
        }

        // Official Go SDK uses data.Url; keep fallbacks for safety.
        JsonElement endpoint;
        if (!data.TryGetProperty("Url", out endpoint) &&
            !data.TryGetProperty("URL", out endpoint) &&
            !data.TryGetProperty("url", out endpoint) &&
            !data.TryGetProperty("Endpoint", out endpoint) &&
            !data.TryGetProperty("endpoint", out endpoint))
        {
            throw new InvalidOperationException(
                $"Feishu WS endpoint response missing URL field (code={GetCode(root)}): {responseBody}");
        }

        var url = endpoint.GetString()
            ?? throw new InvalidOperationException("Feishu returned a null WS endpoint URL.");

        _logger.LogDebug("Feishu WS endpoint obtained.");
        return url;
        } // end using (doc)
    }

    private static int GetCode(JsonElement root)
        => root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;

    // ──────────────────────────── Allowlists ─────────────────────────────────

    private static bool IsUserAllowed(string openId, FeishuChannelConfig cfg)
    {
        if (cfg.AllowedFromUserIds is { Length: > 0 })
            return Array.Exists(cfg.AllowedFromUserIds,
                id => string.Equals(id, openId, StringComparison.Ordinal));
        return true;
    }

    private static bool IsGroupAllowed(string chatId, FeishuChannelConfig cfg)
    {
        if (cfg.AllowedGroupIds is { Length: > 0 })
            return Array.Exists(cfg.AllowedGroupIds,
                id => string.Equals(id, chatId, StringComparison.Ordinal));
        return true;
    }

    // ──────────────────────────── Helpers ────────────────────────────────────

    private async Task SendWsBinaryAsync(byte[] data, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open)
            return;

        await _ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    private static int ParseServiceId(string wsUrl)
    {
        // Extract service_id=<value> from the WS URL query string
        var idx = wsUrl.IndexOf("service_id=", StringComparison.Ordinal);
        if (idx < 0) return 0;
        var start = idx + 11; // "service_id=".Length
        var end = wsUrl.IndexOf('&', start);
        var val = end < 0 ? wsUrl[start..] : wsUrl[start..end];
        return int.TryParse(val, out var id) ? id : 0;
    }

    /// <summary>
    /// AOT-safe JSON string serialization. Handles all control characters and
    /// special JSON characters without reflection or source generation.
    /// Returns "null" when <paramref name="s"/> is null.
    /// </summary>
    private static string JsonEscapeString(string? s)
    {
        if (s is null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string GuessFileType(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "pdf" => "pdf",
            "doc" or "docx" => "doc",
            "xls" or "xlsx" => "xls",
            "ppt" or "pptx" => "ppt",
            "mp4" or "mov" or "avi" or "mkv" => "mp4",
            "opus" => "opus",
            _ => "stream"
        };
    }

    private static string TruncateToMaxUtf8Bytes(string s, int maxBytes)
    {
        var encoded = Encoding.UTF8.GetBytes(s);
        if (encoded.Length <= maxBytes)
            return s;
        // Decode back, trimming incomplete character sequences
        return Encoding.UTF8.GetString(encoded, 0, maxBytes)
                            .TrimEnd('\uFFFD');
    }

    // ──────────────────────────── pbbp2 Protobuf frame types ────────────────

    /// <summary>
    /// Parsed Feishu WS long-connection frame (pbbp2 protobuf format).
    /// Method 0 = control frame, Method 1 = data frame.
    /// </summary>
    private readonly struct FsFrame
    {
        public readonly ulong SeqId;
        public readonly ulong LogId;
        public readonly int Service;
        public readonly int Method;
        public readonly List<(string Key, string Value)> Headers;
        public readonly byte[]? Payload;

        public FsFrame(ulong seqId, ulong logId, int service, int method,
            List<(string Key, string Value)> headers, byte[]? payload)
        {
            SeqId = seqId; LogId = logId; Service = service; Method = method;
            Headers = headers; Payload = payload;
        }

        public string? GetHeader(string key)
        {
            foreach (var (k, v) in Headers)
                if (string.Equals(k, key, StringComparison.Ordinal)) return v;
            return null;
        }
    }

    /// <summary>
    /// Minimal AOT-compatible encoder/decoder for the Feishu pbbp2 protobuf frame format.
    /// Only handles the field types present in Frame and Header messages.
    /// </summary>
    private static class Pbbp2
    {
        private const int WireVarint = 0;
        private const int WireLenDelim = 2;

        // ── Decode ──────────────────────────────────────────────────────────

        public static FsFrame DecodeFrame(ReadOnlySpan<byte> data)
        {
            ulong seqId = 0, logId = 0;
            int service = 0, method = 0;
            var headers = new List<(string Key, string Value)>();
            byte[]? payload = null;
            int pos = 0;

            while (pos < data.Length)
            {
                var tag = (int)ReadVarint(data, ref pos);
                var field = tag >> 3;
                var wireType = tag & 7;

                switch (field)
                {
                    case 1 when wireType == WireVarint:
                        seqId = ReadVarint(data, ref pos);
                        break;
                    case 2 when wireType == WireVarint:
                        logId = ReadVarint(data, ref pos);
                        break;
                    case 3 when wireType == WireVarint:
                        service = (int)ReadVarint(data, ref pos);
                        break;
                    case 4 when wireType == WireVarint:
                        method = (int)ReadVarint(data, ref pos);
                        break;
                    case 5 when wireType == WireLenDelim:
                        headers.Add(DecodeHeader(ReadLenDelim(data, ref pos)));
                        break;
                    case 6 when wireType == WireLenDelim:
                        ReadLenDelim(data, ref pos); // PayloadEncoding — ignored
                        break;
                    case 7 when wireType == WireLenDelim:
                        ReadLenDelim(data, ref pos); // PayloadType — ignored
                        break;
                    case 8 when wireType == WireLenDelim:
                        payload = ReadLenDelim(data, ref pos).ToArray();
                        break;
                    case 9 when wireType == WireLenDelim:
                        ReadLenDelim(data, ref pos); // LogIDNew — ignored
                        break;
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }

            return new FsFrame(seqId, logId, service, method, headers, payload);
        }

        private static (string Key, string Value) DecodeHeader(ReadOnlySpan<byte> data)
        {
            string key = string.Empty, value = string.Empty;
            int pos = 0;

            while (pos < data.Length)
            {
                var tag = (int)ReadVarint(data, ref pos);
                var field = tag >> 3;
                var wireType = tag & 7;

                switch (field)
                {
                    case 1 when wireType == WireLenDelim:
                        key = Encoding.UTF8.GetString(ReadLenDelim(data, ref pos));
                        break;
                    case 2 when wireType == WireLenDelim:
                        value = Encoding.UTF8.GetString(ReadLenDelim(data, ref pos));
                        break;
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }

            return (key, value);
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
        {
            ulong result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                var b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 64) throw new InvalidDataException("Protobuf varint overflow.");
            }
            throw new InvalidDataException("Protobuf: truncated varint.");
        }

        private static ReadOnlySpan<byte> ReadLenDelim(ReadOnlySpan<byte> data, ref int pos)
        {
            var len = (int)ReadVarint(data, ref pos);
            if (len < 0 || pos + len > data.Length)
                throw new InvalidDataException("Protobuf: truncated length-delimited field.");
            var result = data.Slice(pos, len);
            pos += len;
            return result;
        }

        private static void SkipField(ReadOnlySpan<byte> data, ref int pos, int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(data, ref pos); break;
                case 1: pos += 8; break;
                case 2: ReadLenDelim(data, ref pos); break;
                case 5: pos += 4; break;
                default: throw new InvalidDataException($"Protobuf: unknown wire type {wireType}.");
            }
        }

        // ── Encode ──────────────────────────────────────────────────────────

        public static byte[] EncodeFrame(FsFrame frame)
        {
            var buf = new List<byte>(256);

            WriteTagVarint(buf, 1, frame.SeqId);
            WriteTagVarint(buf, 2, frame.LogId);
            WriteTagVarint(buf, 3, (ulong)(uint)frame.Service);
            WriteTagVarint(buf, 4, (ulong)(uint)frame.Method);

            foreach (var (key, value) in frame.Headers)
            {
                var headerBytes = EncodeHeader(key, value);
                WriteTag(buf, 5, WireLenDelim);
                WriteVarint(buf, (ulong)headerBytes.Length);
                buf.AddRange(headerBytes);
            }

            if (frame.Payload is { Length: > 0 })
            {
                WriteTag(buf, 8, WireLenDelim);
                WriteVarint(buf, (ulong)frame.Payload.Length);
                buf.AddRange(frame.Payload);
            }

            return [.. buf];
        }

        private static byte[] EncodeHeader(string key, string value)
        {
            var buf = new List<byte>(key.Length + value.Length + 4);
            var kb = Encoding.UTF8.GetBytes(key);
            var vb = Encoding.UTF8.GetBytes(value);
            WriteTag(buf, 1, WireLenDelim);
            WriteVarint(buf, (ulong)kb.Length);
            buf.AddRange(kb);
            WriteTag(buf, 2, WireLenDelim);
            WriteVarint(buf, (ulong)vb.Length);
            buf.AddRange(vb);
            return [.. buf];
        }

        private static void WriteTagVarint(List<byte> buf, int fieldNum, ulong value)
        {
            WriteTag(buf, fieldNum, WireVarint);
            WriteVarint(buf, value);
        }

        private static void WriteTag(List<byte> buf, int fieldNum, int wireType)
            => WriteVarint(buf, (ulong)((fieldNum << 3) | wireType));

        private static void WriteVarint(List<byte> buf, ulong value)
        {
            while (value > 0x7F)
            {
                buf.Add((byte)(value & 0x7F | 0x80));
                value >>= 7;
            }
            buf.Add((byte)value);
        }
    }

    // ──────────────────────────── Disposal ───────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _dedup.Dispose();


        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Feishu receive loop exited during dispose.");
                }
            }
            _cts.Dispose();
        }

        _ws?.Dispose();
        _restartLock.Dispose();
        _http.Dispose();
    }
}

internal static class StringNullIfEmptyExtensions
{
    public static string? NullIfEmpty(this string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
