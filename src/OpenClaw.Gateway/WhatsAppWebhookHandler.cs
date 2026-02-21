using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class WhatsAppWebhookHandler
{
    private readonly WhatsAppChannelConfig _config;
    private readonly ILogger<WhatsAppWebhookHandler> _logger;

    public WhatsAppWebhookHandler(WhatsAppChannelConfig config, ILogger<WhatsAppWebhookHandler> logger)
    {
        _config = config;
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
            if (_config.Type == "official")
            {
                return await HandleOfficialPostAsync(context, enqueue, ct);
            }
            else
            {
                return await HandleBridgePostAsync(context, enqueue, ct);
            }
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
            var payload = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                WhatsAppJsonContext.Default.WhatsAppInboundPayload,
                ct);

            if (payload?.Entry is null) return WebhookResult.Ok();

            foreach (var entry in payload.Entry)
            {
                if (entry.Changes is null) continue;
                foreach (var change in entry.Changes)
                {
                    if (change.Value?.Messages is null) continue;
                    foreach (var message in change.Value.Messages)
                    {
                        if (message.Type != "text" || message.Text is null || string.IsNullOrWhiteSpace(message.From))
                            continue;

                        var msg = new InboundMessage
                        {
                            ChannelId = "whatsapp",
                            SenderId = message.From,
                            Text = message.Text.Body,
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
            var payload = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                WhatsAppBridgeJsonContext.Default.WhatsAppBridgeInboundPayload,
                ct);

            if (payload is null || string.IsNullOrWhiteSpace(payload.From))
                return WebhookResult.BadRequest("Missing From");

            var msg = new InboundMessage
            {
                ChannelId = "whatsapp",
                SenderId = payload.From,
                Text = payload.Text ?? "",
                SenderName = payload.SenderName
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
}
