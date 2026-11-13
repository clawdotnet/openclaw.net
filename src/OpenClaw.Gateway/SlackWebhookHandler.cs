using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class SlackWebhookHandler
{
    private readonly SlackChannelConfig _config;
    private readonly string? _signingSecret;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _semantics;
    private readonly ILogger<SlackWebhookHandler> _logger;

    public SlackWebhookHandler(
        SlackChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics,
        ILogger<SlackWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _semantics = allowlistSemantics;
        _logger = logger;

        _signingSecret = SecretResolver.Resolve(config.SigningSecretRef) ?? config.SigningSecret;
    }

    public readonly record struct WebhookResponse(int StatusCode, string? Body = null, string ContentType = "text/plain");

    /// <summary>
    /// Handles an inbound Slack Events API webhook.
    /// </summary>
    public async ValueTask<WebhookResponse> HandleEventAsync(
        string bodyText,
        string? timestampHeader,
        string? signatureHeader,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct,
        Func<bool>? tryBeginDelivery = null)
    {
        if (_config.ValidateSignature)
        {
            if (!ValidateSlackSignature(bodyText, timestampHeader, signatureHeader))
            {
                _logger.LogWarning("Rejected Slack webhook due to invalid signature.");
                return new WebhookResponse(401);
            }
        }

        var wrapper = JsonSerializer.Deserialize(bodyText, SlackJsonContext.Default.SlackEventWrapper);
        if (wrapper is null)
            return new WebhookResponse(400, "Invalid payload.");

        // URL verification challenge
        if (string.Equals(wrapper.Type, "url_verification", StringComparison.Ordinal))
        {
            return new WebhookResponse(200, wrapper.Challenge, "text/plain");
        }

        if (!string.Equals(wrapper.Type, "event_callback", StringComparison.Ordinal))
            return new WebhookResponse(200, "OK");

        if (tryBeginDelivery is not null && !tryBeginDelivery())
            return new WebhookResponse(200, "Duplicate ignored.");

        var evt = wrapper.Event;
        if (evt is null)
            return new WebhookResponse(200, "OK");

        // Filter bot messages to prevent loops
        if (!string.IsNullOrEmpty(evt.BotId) || string.Equals(evt.Subtype, "bot_message", StringComparison.Ordinal))
            return new WebhookResponse(200, "OK");

        // Only handle message and app_mention events
        if (!string.Equals(evt.Type, "message", StringComparison.Ordinal) &&
            !string.Equals(evt.Type, "app_mention", StringComparison.Ordinal))
            return new WebhookResponse(200, "OK");

        var userId = evt.User;
        var text = evt.Text;
        var channel = evt.Channel;

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text))
            return new WebhookResponse(200, "OK");

        // Workspace allowlist
        if (_config.AllowedWorkspaceIds.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(wrapper.TeamId))
                return new WebhookResponse(400, "Missing team_id.");

            if (!Array.Exists(_config.AllowedWorkspaceIds, id => string.Equals(id, wrapper.TeamId, StringComparison.Ordinal)))
            {
                _logger.LogWarning("Rejected Slack message from disallowed workspace {TeamId}.", wrapper.TeamId);
                return new WebhookResponse(403);
            }
        }

        // Channel allowlist
        if (_config.AllowedChannelIds.Length > 0 && !string.IsNullOrWhiteSpace(channel))
        {
            if (!Array.Exists(_config.AllowedChannelIds, id => string.Equals(id, channel, StringComparison.Ordinal)))
                return new WebhookResponse(200, "OK");
        }

        await _recentSenders.RecordAsync("slack", userId, senderName: null, ct);

        // User allowlist
        var effective = _allowlists.GetEffective("slack", new ChannelAllowlistFile
        {
            AllowedFrom = _config.AllowedFromUserIds
        });

        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, userId, _semantics))
        {
            _logger.LogWarning("Rejected Slack message from disallowed user {UserId}.", userId);
            return new WebhookResponse(403);
        }

        if (text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];

        // Determine session ID based on thread mapping
        var isDm = string.Equals(evt.ChannelType, "im", StringComparison.Ordinal);
        string? sessionId;
        if (evt.ThreadTs is not null && channel is not null)
            sessionId = $"slack:thread:{channel}:{evt.ThreadTs}";
        else if (isDm)
            sessionId = null; // default: slack:{userId}
        else if (channel is not null)
            sessionId = $"slack:{channel}:{userId}";
        else
            sessionId = null;

        var message = new InboundMessage
        {
            ChannelId = "slack",
            SenderId = userId,
            SessionId = sessionId,
            Text = text,
            MessageId = evt.Ts,
            ReplyToMessageId = evt.ThreadTs,
            IsGroup = !isDm,
            GroupId = isDm ? null : channel,
        };

        await enqueue(message, ct);
        return new WebhookResponse(200, "OK");
    }

    /// <summary>
    /// Handles an inbound Slack slash command (form-encoded POST).
    /// </summary>
    public async ValueTask<WebhookResponse> HandleSlashCommandAsync(
        Dictionary<string, string> form,
        string? timestampHeader,
        string? signatureHeader,
        string rawBody,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        if (_config.ValidateSignature)
        {
            if (!ValidateSlackSignature(rawBody, timestampHeader, signatureHeader))
            {
                _logger.LogWarning("Rejected Slack slash command due to invalid signature.");
                return new WebhookResponse(401);
            }
        }

        var userId = form.GetValueOrDefault("user_id");
        var commandText = form.GetValueOrDefault("text") ?? "";
        var command = form.GetValueOrDefault("command") ?? "";
        var channel = form.GetValueOrDefault("channel_id");
        var teamId = form.GetValueOrDefault("team_id");
        var channelName = form.GetValueOrDefault("channel_name");

        if (string.IsNullOrWhiteSpace(userId))
            return new WebhookResponse(400, "Missing user_id.");

        if (_config.AllowedWorkspaceIds.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(teamId))
                return new WebhookResponse(400, "Missing team_id.");
            if (!Array.Exists(_config.AllowedWorkspaceIds, id => string.Equals(id, teamId, StringComparison.Ordinal)))
                return new WebhookResponse(403);
        }

        if (_config.AllowedChannelIds.Length > 0 && !string.IsNullOrWhiteSpace(channel))
        {
            if (!Array.Exists(_config.AllowedChannelIds, id => string.Equals(id, channel, StringComparison.Ordinal)))
                return new WebhookResponse(403);
        }

        await _recentSenders.RecordAsync("slack", userId, senderName: form.GetValueOrDefault("user_name"), ct);

        var effective = _allowlists.GetEffective("slack", new ChannelAllowlistFile
        {
            AllowedFrom = _config.AllowedFromUserIds
        });

        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, userId, _semantics))
            return new WebhookResponse(403);

        var text = string.IsNullOrWhiteSpace(commandText) ? command : $"{command} {commandText}";

        if (text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];

        var isDm =
            string.Equals(channelName, "directmessage", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(channel) && channel.StartsWith('D'));
        string? sessionId;
        if (isDm)
            sessionId = null;
        else if (!string.IsNullOrWhiteSpace(channel))
            sessionId = $"slack:{channel}:{userId}";
        else
            sessionId = null;

        var message = new InboundMessage
        {
            ChannelId = "slack",
            SenderId = userId,
            SessionId = sessionId,
            Text = text,
            GroupId = isDm ? null : channel,
            IsGroup = !isDm && !string.IsNullOrWhiteSpace(channel),
        };

        await enqueue(message, ct);
        return new WebhookResponse(200, "Processing...");
    }

    /// <summary>
    /// Validates the Slack request signature using HMAC-SHA256.
    /// Slack signs requests with: v0=HMAC-SHA256(signing_secret, "v0:{timestamp}:{body}")
    /// </summary>
    private bool ValidateSlackSignature(string body, string? timestamp, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(_signingSecret) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(providedSignature))
            return false;

        // Prevent replay attacks: reject requests with invalid or stale timestamps
        if (!long.TryParse(timestamp, out var ts))
        {
            _logger.LogWarning("Rejected Slack webhook with invalid timestamp format ({Timestamp}).", timestamp);
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > 300)
        {
            _logger.LogWarning("Rejected Slack webhook with stale timestamp ({Timestamp}).", timestamp);
            return false;
        }

        var baseString = $"v0:{timestamp}:{body}";
        var secretBytes = Encoding.UTF8.GetBytes(_signingSecret);
        var baseBytes = Encoding.UTF8.GetBytes(baseString);
        var hash = HMACSHA256.HashData(secretBytes, baseBytes);
        var expected = $"v0={Convert.ToHexStringLower(hash)}";

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(providedSignature));
    }
}
