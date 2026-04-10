using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class WhatsAppWebhookHandler
{
    private const string OfficialSignatureHeader = "X-Hub-Signature-256";
    private const string BridgeTokenHeader = "X-Bridge-Token";

    private readonly WhatsAppChannelConfig _config;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _allowlistSemantics;
    private readonly ILogger<WhatsAppWebhookHandler> _logger;

    public WhatsAppWebhookHandler(
        WhatsAppChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics,
        ILogger<WhatsAppWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _allowlistSemantics = allowlistSemantics;
        _logger = logger;
    }

    public async Task<WebhookResult> HandleAsync(
        HttpContext context,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        if (!_config.Enabled)
            return WebhookResult.NotFound();

        if (HttpMethods.IsGet(context.Request.Method))
        {
            return HandleVerification(context);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (string.Equals(_config.Type, "official", StringComparison.OrdinalIgnoreCase))
                return await HandleOfficialPostAsync(context, enqueue, ct);
            if (string.Equals(_config.Type, "bridge", StringComparison.OrdinalIgnoreCase))
                return await HandleBridgePostAsync(context, enqueue, ct);

            _logger.LogWarning("WhatsApp webhook invoked for unsupported channel type '{Type}'.", _config.Type);
            return WebhookResult.NotFound();
        }

        return WebhookResult.Status(405);
    }

    private WebhookResult HandleVerification(HttpContext context)
    {
        var mode = context.Request.Query["hub.mode"];
        var token = context.Request.Query["hub.verify_token"];
        var challenge = context.Request.Query["hub.challenge"];

        var expectedToken = SecretResolver.Resolve(_config.WebhookVerifyTokenRef) ?? _config.WebhookVerifyToken;

        if (mode == "subscribe" && token == expectedToken)
        {
            _logger.LogInformation("WhatsApp webhook verified successfully.");
            return new WebhookResult(200, "text/plain", challenge);
        }

        _logger.LogWarning("WhatsApp webhook verification failed. Token mismatch.");
        return WebhookResult.Unauthorized();
    }

    private async Task<WebhookResult> HandleOfficialPostAsync(
        HttpContext context,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        try
        {
            var body = await ReadBodyWithLimitAsync(context, _config.MaxRequestBytes, ct);
            if (body is null)
                return WebhookResult.Status(StatusCodes.Status413PayloadTooLarge);

            if (!ValidateOfficialSignature(context, body))
                return WebhookResult.Unauthorized();

            var payload = JsonSerializer.Deserialize(
                body,
                WhatsAppJsonContext.Default.WhatsAppInboundPayload);

            if (payload?.Entry is null) return WebhookResult.Ok();

            foreach (var entry in payload.Entry)
            {
                if (entry.Changes is null) continue;
                foreach (var change in entry.Changes)
                {
                    if (change.Value?.Messages is null) continue;

                    var contacts = change.Value.Contacts;

                    foreach (var message in change.Value.Messages)
                    {
                        if (message.Type != "text" || message.Text is null || string.IsNullOrWhiteSpace(message.From))
                            continue;

                        var effective = _allowlists.GetEffective("whatsapp", new ChannelAllowlistFile
                        {
                            AllowedFrom = _config.AllowedFromIds
                        });
                        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, message.From, _allowlistSemantics))
                        {
                            _logger.LogInformation("Ignoring WhatsApp inbound message from blocked sender={Sender}.", message.From);
                            continue;
                        }

                        string? senderName = null;
                        if (contacts is not null)
                        {
                            foreach (var contact in contacts)
                            {
                                if (contact.WaId == message.From)
                                {
                                    senderName = contact.Profile?.Name;
                                    break;
                                }
                            }
                        }

                        await _recentSenders.RecordAsync("whatsapp", message.From, senderName, ct);

                        var text = message.Text.Body;
                        if (text.Length > _config.MaxInboundChars)
                        {
                            _logger.LogWarning("Truncating WhatsApp message from {Sender} (exceeds {Max} chars).", message.From, _config.MaxInboundChars);
                            text = text[.._config.MaxInboundChars];
                        }

                        var msg = new InboundMessage
                        {
                            ChannelId = "whatsapp",
                            SenderId = message.From,
                            SenderName = senderName,
                            Text = text,
                            MessageId = message.Id
                        };

                        await enqueue(msg, ct);
                    }
                }
            }

            return WebhookResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse official WhatsApp webhook.");
            return WebhookResult.BadRequest("Invalid JSON");
        }
    }

    private async Task<WebhookResult> HandleBridgePostAsync(
        HttpContext context,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        try
        {
            if (!ValidateBridgeToken(context))
                return WebhookResult.Unauthorized();

            var body = await ReadBodyWithLimitAsync(context, _config.MaxRequestBytes, ct);
            if (body is null)
                return WebhookResult.Status(StatusCodes.Status413PayloadTooLarge);

            var payload = JsonSerializer.Deserialize(
                body,
                WhatsAppBridgeJsonContext.Default.WhatsAppBridgeInboundPayload);

            if (payload is null || string.IsNullOrWhiteSpace(payload.From))
                return WebhookResult.BadRequest("Missing From");

            await _recentSenders.RecordAsync("whatsapp", payload.From, payload.SenderName, ct);

            var effective = _allowlists.GetEffective("whatsapp", new ChannelAllowlistFile
            {
                AllowedFrom = _config.AllowedFromIds
            });
            if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, payload.From, _allowlistSemantics))
                return WebhookResult.Unauthorized();

            var text = payload.Text ?? "";
            if (text.Length > _config.MaxInboundChars)
            {
                _logger.LogWarning("Truncating WhatsApp Bridge message from {Sender} (exceeds {Max} chars).", payload.From, _config.MaxInboundChars);
                text = text[.._config.MaxInboundChars];
            }

            var msg = new InboundMessage
            {
                ChannelId = "whatsapp",
                SenderId = payload.From,
                Text = text,
                SenderName = payload.SenderName,
                MessageId = payload.MessageId
            };

            await enqueue(msg, ct);
            return WebhookResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse WhatsApp Bridge webhook.");
            return WebhookResult.BadRequest("Invalid JSON");
        }
    }

    private static async Task<byte[]?> ReadBodyWithLimitAsync(HttpContext context, int maxBytes, CancellationToken ct)
    {
        var contentLength = context.Request.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            return null;

        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maxBytes;

        var buffer = new byte[8 * 1024];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            if (ms.Length + read > maxBytes)
                return null;

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private bool ValidateOfficialSignature(HttpContext context, ReadOnlySpan<byte> body)
    {
        if (!_config.ValidateSignature)
            return true;

        var appSecret = SecretResolver.Resolve(_config.WebhookAppSecretRef) ?? _config.WebhookAppSecret;
        if (string.IsNullOrWhiteSpace(appSecret))
        {
            _logger.LogWarning(
                "WhatsApp official webhook signature validation is enabled but no app secret is configured.");
            return false;
        }

        var provided = context.Request.Headers[OfficialSignatureHeader].ToString();
        var valid = GatewaySecurity.IsHmacSha256SignatureValid(appSecret, body, provided);
        if (!valid)
            _logger.LogWarning("Rejected WhatsApp official webhook due to invalid signature.");

        return valid;
    }

    private bool ValidateBridgeToken(HttpContext context)
    {
        var expectedToken = SecretResolver.Resolve(_config.BridgeTokenRef) ?? _config.BridgeToken;
        if (string.IsNullOrWhiteSpace(expectedToken))
            return true;

        var bearer = GatewaySecurity.GetBearerToken(context);
        if (!string.IsNullOrWhiteSpace(bearer) && GatewaySecurity.IsTokenValid(bearer, expectedToken))
            return true;

        var bridgeHeader = context.Request.Headers[BridgeTokenHeader].ToString();
        if (!string.IsNullOrWhiteSpace(bridgeHeader) &&
            GatewaySecurity.IsTokenValid(bridgeHeader.Trim(), expectedToken))
        {
            return true;
        }

        _logger.LogWarning("Rejected WhatsApp bridge webhook due to missing/invalid bridge token.");
        return false;
    }
}
