using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

/// <summary>
/// Handles Discord Interaction endpoint webhooks (slash commands).
/// Regular messages arrive via the Gateway WebSocket in DiscordChannel.
/// </summary>
internal sealed class DiscordWebhookHandler
{
    private readonly DiscordChannelConfig _config;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _allowlistSemantics;
    private readonly byte[]? _publicKeyBytes;
    private readonly ILogger<DiscordWebhookHandler> _logger;

    public DiscordWebhookHandler(
        DiscordChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics,
        ILogger<DiscordWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _allowlistSemantics = allowlistSemantics;
        _logger = logger;

        var publicKeyHex = SecretResolver.Resolve(config.PublicKeyRef) ?? config.PublicKey;
        if (!string.IsNullOrWhiteSpace(publicKeyHex))
            _publicKeyBytes = Convert.FromHexString(publicKeyHex);

        if (config.ValidateSignature && !Ed25519Verify.IsSupported)
            logger.LogWarning("Discord signature validation is enabled but Ed25519 is not supported on this platform. " +
                "Signature checks will reject all requests. Disable ValidateSignature or add an Ed25519 provider.");
    }

    public readonly record struct WebhookResponse(int StatusCode, string? Body = null, string ContentType = "application/json");

    /// <summary>
    /// Handles an inbound Discord interaction webhook.
    /// </summary>
    public async ValueTask<WebhookResponse> HandleAsync(
        string bodyText,
        string? signatureHeader,
        string? timestampHeader,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        // Validate Ed25519 signature
        if (_config.ValidateSignature)
        {
            if (!ValidateSignature(bodyText, signatureHeader, timestampHeader))
            {
                _logger.LogWarning("Rejected Discord interaction due to invalid signature.");
                return new WebhookResponse(401, "invalid request signature");
            }
        }

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetInt32() : 0;

        // Type 1: Ping — required for Discord endpoint verification
        if (type == 1)
            return new WebhookResponse(200, """{"type":1}""");

        // Type 2: Application Command
        if (type == 2)
        {
            var interaction = JsonSerializer.Deserialize(bodyText, DiscordJsonContext.Default.DiscordInteraction);
            if (interaction is null)
                return new WebhookResponse(400, """{"error":"invalid interaction"}""");

            var userId = interaction.Member?.User?.Id ?? interaction.User?.Id;
            var username = interaction.Member?.User?.Username ?? interaction.User?.Username;
            var guildId = interaction.GuildId;
            var channelId = interaction.ChannelId;

            if (string.IsNullOrWhiteSpace(userId))
                return new WebhookResponse(400, """{"error":"missing user"}""");

            if (_config.AllowedGuildIds.Length > 0 && !string.IsNullOrWhiteSpace(guildId))
            {
                if (!Array.Exists(_config.AllowedGuildIds, id => string.Equals(id, guildId, StringComparison.Ordinal)))
                    return new WebhookResponse(403, """{"error":"guild not allowed"}""");
            }

            if (_config.AllowedChannelIds.Length > 0 && !string.IsNullOrWhiteSpace(channelId))
            {
                if (!Array.Exists(_config.AllowedChannelIds, id => string.Equals(id, channelId, StringComparison.Ordinal)))
                    return new WebhookResponse(403, """{"error":"channel not allowed"}""");
            }

            await _recentSenders.RecordAsync("discord", userId, username, ct);

            var effective = _allowlists.GetEffective("discord", new ChannelAllowlistFile
            {
                AllowedFrom = _config.AllowedFromUserIds
            });
            if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, userId, _allowlistSemantics))
                return new WebhookResponse(403, """{"error":"user not allowed"}""");

            // Extract command text from options
            var text = "";
            if (interaction.Data?.Options is { Length: > 0 } options)
            {
                foreach (var opt in options)
                {
                    if (string.Equals(opt.Name, "message", StringComparison.Ordinal) && opt.Value.HasValue)
                    {
                        text = opt.Value.Value.GetString() ?? "";
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                text = $"/{interaction.Data?.Name ?? "claw"}";

            if (text.Length > _config.MaxInboundChars)
                text = text[.._config.MaxInboundChars];

            var isDm = guildId is null;
            string? sessionId = null;
            if (!isDm && channelId is not null)
                sessionId = $"discord:{channelId}:{userId}";

            var message = new InboundMessage
            {
                ChannelId = "discord",
                SenderId = userId,
                SenderName = username,
                SessionId = sessionId,
                Text = text,
                MessageId = interaction.Id,
                IsGroup = !isDm,
                GroupId = guildId,
            };

            await enqueue(message, ct);

            // Respond with deferred message (type 5) so the user sees "thinking..."
            return new WebhookResponse(200, """{"type":5}""");
        }

        return new WebhookResponse(200, """{"type":1}""");
    }

    /// <summary>
    /// Validates the Discord Ed25519 signature.
    /// The signed message is: timestamp + body.
    /// Uses BouncyCastle Ed25519 verification.
    /// </summary>
    private bool ValidateSignature(string body, string? signature, string? timestamp)
    {
        if (_publicKeyBytes is null || _publicKeyBytes.Length != 32 ||
            string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
            return false;

        // Reject stale timestamps (5-minute window) to prevent replay attacks
        if (long.TryParse(timestamp, out var ts))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - ts) > 300)
            {
                _logger.LogWarning("Rejected Discord interaction with stale timestamp ({Timestamp}).", timestamp);
                return false;
            }
        }

        try
        {
            var signatureBytes = Convert.FromHexString(signature);
            if (signatureBytes.Length != 64)
                return false;

            var message = Encoding.UTF8.GetBytes(timestamp + body);
            return Ed25519Verify.Verify(signatureBytes, message, _publicKeyBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord Ed25519 signature verification failed.");
            return false;
        }
    }
}
