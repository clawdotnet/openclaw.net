using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Adapts a plugin-registered channel to <see cref="IChannelAdapter"/>.
/// Inbound messages arrive via bridge notifications; outbound messages are sent via bridge requests.
/// </summary>
public sealed class BridgedChannelAdapter : IBridgedChannelControl, IRestartableChannelAdapter
{
    private readonly PluginBridgeProcess _bridge;
    private readonly ILogger _logger;

    public string ChannelId { get; }

    /// <summary>
    /// The self-identity of the connected account (e.g. WhatsApp JID).
    /// Populated from the <c>channel_start</c> response if the plugin provides it.
    /// </summary>
    public string? SelfId { get; private set; }

    /// <summary>
    /// All known self-identities returned by the channel start handshake.
    /// Multi-account workers populate this when available.
    /// </summary>
    public IReadOnlyList<string> SelfIds { get; private set; } = [];

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    /// <summary>
    /// Raised when the plugin sends a <c>channel_auth_event</c> notification (e.g. QR code for linking).
    /// </summary>
    public event Action<BridgeChannelAuthEvent>? OnAuthEvent;

    public BridgedChannelAdapter(PluginBridgeProcess bridge, string channelId, ILogger logger)
    {
        _bridge = bridge;
        _logger = logger;
        ChannelId = channelId;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var response = await _bridge.SendAndWaitAsync(
            "channel_start",
            new BridgeChannelControlRequest { ChannelId = ChannelId },
            CoreJsonContext.Default.BridgeChannelControlRequest,
            ct);

        if (response.Result is { } result &&
            result.TryGetProperty("selfId", out var selfIdProp))
        {
            SelfId = selfIdProp.GetString();
        }

        if (response.Result is { } idsResult &&
            idsResult.TryGetProperty("selfIds", out var selfIdsProp) &&
            selfIdsProp.ValueKind == JsonValueKind.Array)
        {
            var selfIds = new List<string>();
            foreach (var item in selfIdsProp.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    selfIds.Add(value);
            }

            if (selfIds.Count > 0)
            {
                SelfIds = selfIds;
                if (string.IsNullOrWhiteSpace(SelfId))
                    SelfId = selfIds[0];
            }
        }
        else if (!string.IsNullOrWhiteSpace(SelfId))
        {
            SelfIds = [SelfId];
        }
    }

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var (markers, remainingText) = MediaMarkerProtocol.Extract(message.Text);

        BridgeMediaAttachment[]? attachments = null;
        if (markers.Count > 0)
        {
            attachments = new BridgeMediaAttachment[markers.Count];
            for (var i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                attachments[i] = new BridgeMediaAttachment
                {
                    Type = MarkerKindToMediaType(m.Kind),
                    Url = m.Value
                };
            }
        }

        await _bridge.SendRequestAsync(
            "channel_send",
            new BridgeChannelSendRequest
            {
                ChannelId = ChannelId,
                RecipientId = message.RecipientId,
                Text = remainingText,
                AccountId = message.AccountId,
                SessionId = message.SessionId,
                ReplyToMessageId = message.ReplyToMessageId,
                Subject = message.Subject,
                Attachments = attachments,
            },
            CoreJsonContext.Default.BridgeChannelSendRequest,
            ct);
    }

    /// <summary>
    /// Sends a typing indicator through the bridge channel.
    /// </summary>
    public async ValueTask SendTypingAsync(string recipientId, bool isTyping, string? accountId, CancellationToken ct)
    {
        try
        {
            await _bridge.SendRequestAsync(
                "channel_typing",
                new BridgeChannelTypingRequest
                {
                    ChannelId = ChannelId,
                    RecipientId = recipientId,
                    AccountId = accountId,
                    IsTyping = isTyping,
                },
                CoreJsonContext.Default.BridgeChannelTypingRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send typing indicator for '{ChannelId}'", ChannelId);
        }
    }

    /// <summary>
    /// Sends a read receipt for a message through the bridge channel.
    /// </summary>
    public async ValueTask SendReadReceiptAsync(string messageId, string? remoteJid, string? participant, string? accountId, CancellationToken ct)
    {
        try
        {
            await _bridge.SendRequestAsync(
                "channel_read_receipt",
                new BridgeChannelReceiptRequest
                {
                    ChannelId = ChannelId,
                    MessageId = messageId,
                    AccountId = accountId,
                    RemoteJid = remoteJid,
                    Participant = participant,
                },
                CoreJsonContext.Default.BridgeChannelReceiptRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send read receipt for '{ChannelId}'", ChannelId);
        }
    }

    /// <summary>
    /// Sends an emoji reaction to a message through the bridge channel.
    /// </summary>
    public async ValueTask SendReactionAsync(string messageId, string emoji, string? remoteJid, string? participant, string? accountId, CancellationToken ct)
    {
        try
        {
            await _bridge.SendRequestAsync(
                "channel_react",
                new BridgeChannelReactionRequest
                {
                    ChannelId = ChannelId,
                    MessageId = messageId,
                    Emoji = emoji,
                    AccountId = accountId,
                    RemoteJid = remoteJid,
                    Participant = participant,
                },
                CoreJsonContext.Default.BridgeChannelReactionRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send reaction for '{ChannelId}'", ChannelId);
        }
    }

    /// <summary>
    /// Called by the notification dispatcher when a <c>channel_message</c> notification arrives.
    /// </summary>
    internal async ValueTask HandleInboundAsync(JsonElement parameters, CancellationToken ct)
    {
        var senderId = parameters.TryGetProperty("senderId", out var sid) ? sid.GetString() ?? "unknown" : "unknown";
        var text = parameters.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
        var sessionId = parameters.TryGetProperty("sessionId", out var sess) ? sess.GetString() : null;
        var senderName = parameters.TryGetProperty("senderName", out var sn) ? sn.GetString() : null;
        var messageId = parameters.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
        var replyToMessageId = parameters.TryGetProperty("replyToMessageId", out var rtm) ? rtm.GetString() : null;
        var accountId = parameters.TryGetProperty("accountId", out var aid) ? aid.GetString() : null;

        // Group fields
        var isGroup = parameters.TryGetProperty("isGroup", out var ig) && ig.GetBoolean();
        var groupId = parameters.TryGetProperty("groupId", out var gid) ? gid.GetString() : null;
        var groupName = parameters.TryGetProperty("groupName", out var gn) ? gn.GetString() : null;
        string[]? mentionedIds = null;
        if (parameters.TryGetProperty("mentionedIds", out var mids) && mids.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in mids.EnumerateArray())
            {
                var v = item.GetString();
                if (v is not null) list.Add(v);
            }
            mentionedIds = list.Count > 0 ? list.ToArray() : null;
        }

        // Media fields — convert to marker-prefixed text for pipeline compatibility
        string? mediaType = null;
        string? mediaUrl = null;
        string? mediaMimeType = null;
        string? mediaFileName = null;

        if (parameters.TryGetProperty("mediaType", out var mt))
        {
            mediaType = mt.GetString();
            mediaUrl = parameters.TryGetProperty("mediaUrl", out var mu) ? mu.GetString() : null;
            mediaMimeType = parameters.TryGetProperty("mediaMimeType", out var mm) ? mm.GetString() : null;
            mediaFileName = parameters.TryGetProperty("mediaFileName", out var mf) ? mf.GetString() : null;

            // Prepend a media marker line so downstream pipeline can extract it
            if (!string.IsNullOrWhiteSpace(mediaUrl) && !string.IsNullOrWhiteSpace(mediaType))
            {
                var markerPrefix = mediaType switch
                {
                    "image" => $"[IMAGE_URL:{mediaUrl}]",
                    "video" => $"[VIDEO_URL:{mediaUrl}]",
                    "audio" => $"[AUDIO_URL:{mediaUrl}]",
                    "document" => $"[DOCUMENT_URL:{mediaUrl}]",
                    "sticker" => $"[STICKER_URL:{mediaUrl}]",
                    _ => $"[FILE_URL:{mediaUrl}]",
                };
                text = string.IsNullOrWhiteSpace(text) ? markerPrefix : $"{markerPrefix}\n{text}";
            }
        }

        var msg = new InboundMessage
        {
            ChannelId = ChannelId,
            SenderId = senderId,
            Text = text,
            AccountId = accountId,
            SessionId = sessionId,
            SenderName = senderName,
            MessageId = messageId,
            ReplyToMessageId = replyToMessageId,
            IsGroup = isGroup,
            GroupId = groupId,
            GroupName = groupName,
            MentionedIds = mentionedIds,
            MediaType = mediaType,
            MediaUrl = mediaUrl,
            MediaMimeType = mediaMimeType,
            MediaFileName = mediaFileName,
        };

        if (OnMessageReceived is not null)
        {
            try
            {
                await OnMessageReceived.Invoke(msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BridgedChannelAdapter '{ChannelId}' OnMessageReceived handler threw", ChannelId);
            }
        }
    }

    /// <summary>
    /// Called by the notification dispatcher when a <c>channel_auth_event</c> notification arrives.
    /// </summary>
    internal void HandleAuthEvent(JsonElement parameters)
    {
        var state = parameters.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
        var data = parameters.TryGetProperty("data", out var d) ? d.GetString() : null;
        var accountId = parameters.TryGetProperty("accountId", out var a) ? a.GetString() : null;

        var evt = new BridgeChannelAuthEvent
        {
            ChannelId = ChannelId,
            State = state,
            Data = data,
            AccountId = accountId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            OnAuthEvent?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BridgedChannelAdapter '{ChannelId}' OnAuthEvent handler threw", ChannelId);
        }
    }

    public ValueTask DisposeAsync()
    {
        // channel_stop is sent during plugin shutdown via PluginBridgeProcess.DisposeAsync
        return ValueTask.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await _bridge.SendRequestAsync(
            "channel_stop",
            new BridgeChannelControlRequest { ChannelId = ChannelId },
            CoreJsonContext.Default.BridgeChannelControlRequest,
            ct);

        SelfId = null;
        SelfIds = [];
        await StartAsync(ct);
    }

    private static string MarkerKindToMediaType(MediaMarkerKind kind) => kind switch
    {
        MediaMarkerKind.ImageUrl or MediaMarkerKind.ImagePath or MediaMarkerKind.TelegramImageFileId => "image",
        MediaMarkerKind.VideoUrl => "video",
        MediaMarkerKind.AudioUrl => "audio",
        MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl or MediaMarkerKind.FilePath => "document",
        MediaMarkerKind.StickerUrl => "sticker",
        _ => "document",
    };
}
