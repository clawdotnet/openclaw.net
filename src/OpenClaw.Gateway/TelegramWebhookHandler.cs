using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class TelegramWebhookHandler
{
    private readonly TelegramChannelConfig _config;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _allowlistSemantics;
    private readonly ILogger<TelegramWebhookHandler> _logger;

    public TelegramWebhookHandler(
        TelegramChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics,
        ILogger<TelegramWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _allowlistSemantics = allowlistSemantics;
        _logger = logger;
    }

    public static string ResolveDeliveryKey(string bodyText)
    {
        try
        {
            using var document = JsonDocument.Parse(bodyText, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.TryGetProperty("update_id", out var updateId)
                ? updateId.GetRawText()
                : WebhookDeliveryStore.HashDeliveryKey(bodyText);
        }
        catch (JsonException)
        {
            return WebhookDeliveryStore.HashDeliveryKey(bodyText);
        }
    }

    public async ValueTask<WebhookResult> HandleAsync(
        string bodyText,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        using var document = ParseBody(bodyText);
        if (document is null)
            return WebhookResult.BadRequest("Invalid JSON");

        var root = document.RootElement;
        if (!TryGetMessage(root, out var message))
            return Ok();

        if (!message.TryGetProperty("chat", out var chat) ||
            !chat.TryGetProperty("id", out var chatIdNode))
        {
            return Ok();
        }

        var senderId = GetTelegramId(chatIdNode);
        if (string.IsNullOrWhiteSpace(senderId))
            return Ok();

        var senderName = GetSenderName(message, chat);
        await _recentSenders.RecordAsync("telegram", senderId, senderName, ct);

        var effective = _allowlists.GetEffective("telegram", new ChannelAllowlistFile
        {
            AllowedFrom = _config.AllowedFromUserIds
        });

        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderId, _allowlistSemantics))
        {
            _logger.LogInformation("Rejected Telegram update from blocked chat {ChatId}.", senderId);
            return WebhookResult.Status(StatusCodes.Status403Forbidden);
        }

        var text = BuildText(message);
        if (!string.IsNullOrWhiteSpace(text) && text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];

        if (string.IsNullOrWhiteSpace(text))
            return Ok();

        var inbound = new InboundMessage
        {
            ChannelId = "telegram",
            SenderId = senderId,
            SenderName = senderName,
            Text = text,
            MessageId = GetOptionalIntString(message, "message_id"),
            ReplyToMessageId = TryGetReplyToMessageId(message)
        };

        await enqueue(inbound, ct);
        return Ok();
    }

    private static JsonDocument? ParseBody(string bodyText)
    {
        try
        {
            return JsonDocument.Parse(bodyText, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetMessage(JsonElement root, out JsonElement message)
    {
        var propertyName = new[] { "message", "channel_post", "edited_message", "edited_channel_post" }
            .Where(name => root.TryGetProperty(name, out _))
            .FirstOrDefault();

        if (propertyName is not null && root.TryGetProperty(propertyName, out message))
        {
            return true;
        }

        message = default;
        return false;
    }

    private static string? BuildText(JsonElement message)
    {
        var text = message.TryGetProperty("text", out var textNode)
            ? textNode.GetString()
            : null;

        var marker = TryBuildMediaMarker(message);
        if (string.IsNullOrWhiteSpace(marker))
            return text;

        var caption = message.TryGetProperty("caption", out var captionNode)
            ? captionNode.GetString()
            : null;

        return string.IsNullOrWhiteSpace(caption) ? marker : marker + "\n" + caption;
    }

    private static string? TryBuildMediaMarker(JsonElement message)
    {
        if (message.TryGetProperty("photo", out var photoNode) && photoNode.ValueKind == JsonValueKind.Array)
        {
            var fileId = photoNode.EnumerateArray()
                .Where(static photo => photo.TryGetProperty("file_id", out _))
                .Select(static photo => photo.GetProperty("file_id").GetString())
                .LastOrDefault(static id => !string.IsNullOrWhiteSpace(id));

            return string.IsNullOrWhiteSpace(fileId) ? null : $"[IMAGE:telegram:file_id={fileId}]";
        }

        if (TryGetFileId(message, "video", out var videoFileId))
            return $"[VIDEO:telegram:file_id={videoFileId}]";

        if (TryGetFileId(message, "audio", out var audioFileId) ||
            TryGetFileId(message, "voice", out audioFileId))
        {
            return $"[AUDIO:telegram:file_id={audioFileId}]";
        }

        if (TryGetFileId(message, "document", out var documentFileId))
            return $"[DOCUMENT:telegram:file_id={documentFileId}]";

        if (TryGetFileId(message, "sticker", out var stickerFileId))
            return $"[STICKER:telegram:file_id={stickerFileId}]";

        return null;
    }

    private static bool TryGetFileId(JsonElement message, string propertyName, out string fileId)
    {
        fileId = "";
        if (!message.TryGetProperty(propertyName, out var media) ||
            !media.TryGetProperty("file_id", out var fileIdNode))
        {
            return false;
        }

        fileId = fileIdNode.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(fileId);
    }

    private static string? GetSenderName(JsonElement message, JsonElement chat)
    {
        if (message.TryGetProperty("from", out var from))
        {
            var userName = GetUserDisplayName(from);
            if (!string.IsNullOrWhiteSpace(userName))
                return userName;
        }

        if (chat.TryGetProperty("title", out var titleNode))
        {
            var title = titleNode.GetString();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return GetUserDisplayName(chat);
    }

    private static string? GetUserDisplayName(JsonElement user)
    {
        if (user.TryGetProperty("username", out var usernameNode))
        {
            var username = usernameNode.GetString();
            if (!string.IsNullOrWhiteSpace(username))
                return username.StartsWith('@') ? username : "@" + username;
        }

        var firstName = user.TryGetProperty("first_name", out var firstNameNode) ? firstNameNode.GetString() : null;
        var lastName = user.TryGetProperty("last_name", out var lastNameNode) ? lastNameNode.GetString() : null;
        var fullName = string.Join(" ", new[] { firstName, lastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
    }

    private static string? TryGetReplyToMessageId(JsonElement message)
    {
        if (!message.TryGetProperty("reply_to_message", out var reply) ||
            !reply.TryGetProperty("message_id", out var messageId))
        {
            return null;
        }

        return GetTelegramId(messageId);
    }

    private static string? GetOptionalIntString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var value) ? GetTelegramId(value) : null;

    private static string GetTelegramId(JsonElement node)
    {
        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetInt64(out var value) => value.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => node.GetString() ?? "",
            _ => node.GetRawText()
        };
    }

    private static WebhookResult Ok() => new(StatusCodes.Status200OK, "text/plain; charset=utf-8", "OK");
}
