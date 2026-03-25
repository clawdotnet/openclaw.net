using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WebhookEndpoints
{
    public static void MapOpenClawWebhookEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var deliveries = runtime.Operations.WebhookDeliveries;

        if (runtime.TwilioSmsWebhookHandler is not null)
        {
            app.MapPost(startup.Config.Channels.Sms.Twilio.WebhookPath, async (HttpContext ctx) =>
            {
                var maxRequestSize = Math.Max(4 * 1024, startup.Config.Channels.Sms.Twilio.MaxRequestBytes);

                if (!ctx.Request.HasFormContentType)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsync("Expected form content.", ctx.RequestAborted);
                    return;
                }

                var (bodyOk, bodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                var parsed = QueryHelpers.ParseQuery(bodyText);
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in parsed)
                    dict[kvp.Key] = kvp.Value.ToString();

                var deliveryKey = dict.TryGetValue("MessageSid", out var messageSid) && !string.IsNullOrWhiteSpace(messageSid)
                    ? messageSid
                    : WebhookDeliveryStore.HashDeliveryKey(bodyText);
                if (!deliveries.TryBegin("twilio", deliveryKey, TimeSpan.FromHours(6)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.WriteAsync("Duplicate ignored.", ctx.RequestAborted);
                    return;
                }

                var sig = ctx.Request.Headers["X-Twilio-Signature"].ToString();
                InboundMessage? replayMessage = null;

                try
                {
                    var response = await runtime.TwilioSmsWebhookHandler.HandleAsync(
                        dict,
                        sig,
                        (msg, ct) =>
                        {
                            replayMessage = msg;
                            return runtime.Pipeline.InboundWriter.WriteAsync(msg, ct);
                        },
                        ctx.RequestAborted);

                    ctx.Response.StatusCode = response.StatusCode;
                    if (response.Body is not null)
                    {
                        ctx.Response.ContentType = response.ContentType;
                        await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
                    }
                }
                catch (Exception ex)
                {
                    deliveries.RecordDeadLetter(new WebhookDeadLetterRecord
                    {
                        Entry = new WebhookDeadLetterEntry
                        {
                            Id = $"whdl_{Guid.NewGuid():N}"[..20],
                            Source = "twilio",
                            DeliveryKey = deliveryKey,
                            ChannelId = "sms",
                            SenderId = replayMessage?.SenderId ?? dict.GetValueOrDefault("From"),
                            SessionId = replayMessage?.SessionId,
                            Error = ex.Message,
                            PayloadPreview = bodyText.Length <= 500 ? bodyText : bodyText[..500] + "…"
                        },
                        ReplayMessage = replayMessage
                    });
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("Webhook processing failed.", ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Channels.Telegram.Enabled)
        {
            byte[]? telegramSecretBytes = null;
            if (startup.Config.Channels.Telegram.ValidateSignature)
            {
                var telegramSecret = startup.Config.Channels.Telegram.WebhookSecretToken
                    ?? SecretResolver.Resolve(startup.Config.Channels.Telegram.WebhookSecretTokenRef);
                if (string.IsNullOrWhiteSpace(telegramSecret))
                {
                    throw new InvalidOperationException(
                        "Telegram ValidateSignature is true but WebhookSecretToken/WebhookSecretTokenRef could not be resolved. " +
                        "Set TELEGRAM_WEBHOOK_SECRET or disable ValidateSignature.");
                }

                telegramSecretBytes = Encoding.UTF8.GetBytes(telegramSecret);
            }

            app.MapPost(startup.Config.Channels.Telegram.WebhookPath, async (HttpContext ctx) =>
            {
                if (telegramSecretBytes is not null)
                {
                    var provided = ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
                    var providedBytes = Encoding.UTF8.GetBytes(provided ?? "");
                    if (providedBytes.Length != telegramSecretBytes.Length ||
                        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, telegramSecretBytes))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                var maxRequestSize = Math.Max(4 * 1024, startup.Config.Channels.Telegram.MaxRequestBytes);
                var (bodyOk, bodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                using var document = JsonDocument.Parse(bodyText, new JsonDocumentOptions { MaxDepth = 64 });
                var root = document.RootElement;
                var deliveryKey = root.TryGetProperty("update_id", out var updateId)
                    ? updateId.GetRawText()
                    : WebhookDeliveryStore.HashDeliveryKey(bodyText);
                if (!deliveries.TryBegin("telegram", deliveryKey, TimeSpan.FromHours(6)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.WriteAsync("OK");
                    return;
                }

                InboundMessage? replayMessage = null;

                try
                {
                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("chat", out var chat) &&
                        chat.TryGetProperty("id", out var chatId))
                    {
                        var senderIdStr = chatId.GetRawText();

                        await runtime.RecentSenders.RecordAsync("telegram", senderIdStr, senderName: null, ctx.RequestAborted);

                        var effective = runtime.Allowlists.GetEffective("telegram", new ChannelAllowlistFile
                        {
                            AllowedFrom = startup.Config.Channels.Telegram.AllowedFromUserIds
                        });

                        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderIdStr, runtime.AllowlistSemantics))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        string? text = null;
                        if (message.TryGetProperty("text", out var textNode))
                            text = textNode.GetString();

                        string? marker = null;
                        if (message.TryGetProperty("photo", out var photoNode) && photoNode.ValueKind == JsonValueKind.Array)
                        {
                            string? fileId = null;
                            foreach (var photo in photoNode.EnumerateArray())
                            {
                                if (photo.TryGetProperty("file_id", out var idNode))
                                    fileId = idNode.GetString();
                            }

                            if (!string.IsNullOrWhiteSpace(fileId))
                                marker = $"[IMAGE:telegram:file_id={fileId}]";
                        }

                        if (!string.IsNullOrWhiteSpace(marker))
                        {
                            var caption = message.TryGetProperty("caption", out var capNode) ? capNode.GetString() : null;
                            text = string.IsNullOrWhiteSpace(caption) ? marker : marker + "\n" + caption;
                        }

                        if (!string.IsNullOrWhiteSpace(text) && text.Length > startup.Config.Channels.Telegram.MaxInboundChars)
                            text = text[..startup.Config.Channels.Telegram.MaxInboundChars];

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status200OK;
                            await ctx.Response.WriteAsync("OK");
                            return;
                        }

                        replayMessage = new InboundMessage
                        {
                            ChannelId = "telegram",
                            SenderId = senderIdStr,
                            Text = text
                        };

                        await runtime.Pipeline.InboundWriter.WriteAsync(replayMessage, ctx.RequestAborted);
                    }

                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.WriteAsync("OK");
                }
                catch (Exception ex)
                {
                    deliveries.RecordDeadLetter(new WebhookDeadLetterRecord
                    {
                        Entry = new WebhookDeadLetterEntry
                        {
                            Id = $"whdl_{Guid.NewGuid():N}"[..20],
                            Source = "telegram",
                            DeliveryKey = deliveryKey,
                            ChannelId = "telegram",
                            SenderId = replayMessage?.SenderId,
                            SessionId = replayMessage?.SessionId,
                            Error = ex.Message,
                            PayloadPreview = bodyText.Length <= 500 ? bodyText : bodyText[..500] + "…"
                        },
                        ReplayMessage = replayMessage
                    });
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("Webhook processing failed.", ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Channels.WhatsApp.Enabled &&
            !string.Equals(startup.Config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            var whatsappWebhookHandler = app.Services.GetRequiredService<WhatsAppWebhookHandler>();
            app.MapMethods(startup.Config.Channels.WhatsApp.WebhookPath, ["GET", "POST"], async (HttpContext ctx) =>
            {
                var isPost = HttpMethods.IsPost(ctx.Request.Method);
                string bodyText = "";
                if (isPost)
                {
                    ctx.Request.EnableBuffering();
                    var (bodyOk, requestBodyText) = await EndpointHelpers.TryReadBodyTextAsync(ctx, Math.Max(4 * 1024, startup.Config.Channels.WhatsApp.MaxRequestBytes), ctx.RequestAborted);
                    if (!bodyOk)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                        return;
                    }

                    bodyText = requestBodyText;
                    ctx.Request.Body.Position = 0;
                    var deliveryKey = TryResolveWhatsAppDeliveryKey(bodyText);
                    if (!deliveries.TryBegin("whatsapp", deliveryKey, TimeSpan.FromHours(6)))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        await ctx.Response.WriteAsync("Duplicate ignored.", ctx.RequestAborted);
                        return;
                    }
                }

                InboundMessage? replayMessage = null;
                try
                {
                    var response = await whatsappWebhookHandler.HandleAsync(
                        ctx,
                        (msg, ct) =>
                        {
                            replayMessage = msg;
                            return runtime.Pipeline.InboundWriter.WriteAsync(msg, ct);
                        },
                        ctx.RequestAborted);

                    ctx.Response.StatusCode = response.StatusCode;
                    if (response.Body is not null)
                    {
                        ctx.Response.ContentType = response.ContentType;
                        await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
                    }
                }
                catch (Exception ex)
                {
                    deliveries.RecordDeadLetter(new WebhookDeadLetterRecord
                    {
                        Entry = new WebhookDeadLetterEntry
                        {
                            Id = $"whdl_{Guid.NewGuid():N}"[..20],
                            Source = "whatsapp",
                            DeliveryKey = isPost ? TryResolveWhatsAppDeliveryKey(bodyText) : "",
                            ChannelId = "whatsapp",
                            SenderId = replayMessage?.SenderId,
                            SessionId = replayMessage?.SessionId,
                            Error = ex.Message,
                            PayloadPreview = bodyText.Length <= 500 ? bodyText : bodyText[..500] + "…"
                        },
                        ReplayMessage = replayMessage
                    });
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("Webhook processing failed.", ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Channels.Teams.Enabled)
        {
            var teamsHandler = app.Services.GetRequiredService<TeamsWebhookHandler>();
            var teamsChannel = app.Services.GetRequiredService<TeamsChannel>();
            app.MapPost(startup.Config.Channels.Teams.WebhookPath, async (HttpContext ctx) =>
            {
                InboundMessage? replayMessage = null;
                var deliveryKey = "";
                try
                {
                    var result = await teamsHandler.HandleAsync(
                        ctx,
                        teamsChannel,
                        async (msg, ct2) =>
                        {
                            replayMessage = msg;
                            deliveryKey = msg.MessageId ?? "";
                            if (!string.IsNullOrWhiteSpace(deliveryKey) &&
                                !deliveries.TryBegin("teams", deliveryKey, TimeSpan.FromHours(6)))
                                return;
                            await runtime.Pipeline.InboundWriter.WriteAsync(msg, ct2);
                        },
                        ctx.RequestAborted);

                    ctx.Response.StatusCode = result.StatusCode;
                    if (result.ContentType is not null)
                        ctx.Response.ContentType = result.ContentType;
                    if (result.Body is not null)
                        await ctx.Response.WriteAsync(result.Body, ctx.RequestAborted);
                }
                catch (Exception ex)
                {
                    deliveries.RecordDeadLetter(new WebhookDeadLetterRecord
                    {
                        Entry = new WebhookDeadLetterEntry
                        {
                            Id = $"whdl_{Guid.NewGuid():N}"[..20],
                            Source = "teams",
                            DeliveryKey = deliveryKey,
                            ChannelId = "teams",
                            SenderId = replayMessage?.SenderId,
                            SessionId = replayMessage?.SessionId,
                            Error = ex.Message
                        },
                        ReplayMessage = replayMessage
                    });
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("Webhook processing failed.", ctx.RequestAborted);
                }
            });
        }

        if (startup.Config.Webhooks.Enabled)
        {
            app.MapPost("/webhooks/{name}", async (HttpContext ctx, string name) =>
            {
                if (!startup.Config.Webhooks.Endpoints.TryGetValue(name, out var hookCfg))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var maxRequestSize = Math.Max(4 * 1024, hookCfg.MaxRequestBytes);
                var (bodyOk, body) = await EndpointHelpers.TryReadBodyTextAsync(ctx, maxRequestSize, ctx.RequestAborted);
                if (!bodyOk)
                {
                    ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await ctx.Response.WriteAsync("Request too large.", ctx.RequestAborted);
                    return;
                }

                var bodyForPrompt = body.Length > hookCfg.MaxBodyLength
                    ? body[..hookCfg.MaxBodyLength]
                    : body;

                var headerKey = ctx.Request.Headers["Idempotency-Key"].ToString();
                if (string.IsNullOrWhiteSpace(headerKey))
                    headerKey = ctx.Request.Headers["X-OpenClaw-Delivery-Id"].ToString();
                var deliveryKey = string.IsNullOrWhiteSpace(headerKey)
                    ? WebhookDeliveryStore.HashDeliveryKey($"{name}:{body}")
                    : headerKey;
                if (!deliveries.TryBegin($"webhook:{name}", deliveryKey, TimeSpan.FromHours(6)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                    await ctx.Response.WriteAsync("Webhook already processed.");
                    return;
                }

                if (hookCfg.ValidateHmac)
                {
                    var secret = SecretResolver.Resolve(hookCfg.Secret);
                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    var signatureHeader = ctx.Request.Headers[hookCfg.HmacHeader].ToString();
                    if (!GatewaySecurity.IsHmacSha256SignatureValid(secret, body, signatureHeader))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                var prompt = hookCfg.PromptTemplate.Replace("{body}", bodyForPrompt);
                var msg = new InboundMessage
                {
                    ChannelId = "webhook",
                    SessionId = hookCfg.SessionId ?? $"webhook:{name}",
                    SenderId = "system",
                    Text = prompt
                };

                try
                {
                    await runtime.Pipeline.InboundWriter.WriteAsync(msg, ctx.RequestAborted);
                    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                    await ctx.Response.WriteAsync("Webhook queued.");
                }
                catch (Exception ex)
                {
                    deliveries.RecordDeadLetter(new WebhookDeadLetterRecord
                    {
                        Entry = new WebhookDeadLetterEntry
                        {
                            Id = $"whdl_{Guid.NewGuid():N}"[..20],
                            Source = $"webhook:{name}",
                            DeliveryKey = deliveryKey,
                            EndpointName = name,
                            ChannelId = "webhook",
                            SessionId = msg.SessionId,
                            Error = ex.Message,
                            PayloadPreview = bodyForPrompt.Length <= 500 ? bodyForPrompt : bodyForPrompt[..500] + "…"
                        },
                        ReplayMessage = msg
                    });
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsync("Webhook processing failed.", ctx.RequestAborted);
                }
            });
        }
    }

    private static string TryResolveWhatsAppDeliveryKey(string bodyText)
    {
        try
        {
            using var document = JsonDocument.Parse(bodyText);
            var root = document.RootElement;
            if (root.TryGetProperty("message_id", out var bridgeMessageId) && bridgeMessageId.ValueKind == JsonValueKind.String)
                return bridgeMessageId.GetString() ?? WebhookDeliveryStore.HashDeliveryKey(bodyText);
            if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value))
                            continue;

                        if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray())
                            {
                                if (message.TryGetProperty("id", out var idNode))
                                    return idNode.GetString() ?? WebhookDeliveryStore.HashDeliveryKey(bodyText);
                            }
                        }

                        if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var status in statuses.EnumerateArray())
                            {
                                if (status.TryGetProperty("id", out var idNode))
                                    return idNode.GetString() ?? WebhookDeliveryStore.HashDeliveryKey(bodyText);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return WebhookDeliveryStore.HashDeliveryKey(bodyText);
    }
}
