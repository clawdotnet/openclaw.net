using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WebhookDeliveryReservationTests
{
    [Theory]
    [InlineData("slack")]
    [InlineData("twilio")]
    [InlineData("official")]
    [InlineData("bridge")]
    public async Task ProviderWebhook_WhenInvalidRequestPrecedesRetry_ShouldReserveOnlyAuthenticatedDelivery(string provider)
    {
        var root = Path.Combine(Path.GetTempPath(), "webhook-reservation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var allowlists = new AllowlistManager(root, NullLogger<AllowlistManager>.Instance);
            var recent = new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance);
            var ct = TestContext.Current.CancellationToken;
            var reservations = 0;
            var deliveries = 0;
            bool TryBegin() => ++reservations == 1;
            ValueTask Enqueue(InboundMessage _, CancellationToken token)
            {
                deliveries++;
                return ValueTask.CompletedTask;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var valid = attempt > 0;
                int status;
                if (provider == "slack")
                {
                    const string body = """{"type":"event_callback","event_id":"one","event":{"type":"message","user":"U1","channel":"C1","text":"hello"}}""";
                    var handler = new SlackWebhookHandler(new SlackChannelConfig { ValidateSignature = true, SigningSecret = "fixture" },
                        allowlists, recent, AllowlistSemantics.Legacy, NullLogger<SlackWebhookHandler>.Instance);
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var signature = valid ? "v0=" + GatewaySecurity.ComputeHmacSha256Hex("fixture", $"v0:{timestamp}:{body}") : "invalid";
                    status = (await handler.HandleEventAsync(body, timestamp, signature, Enqueue, ct, TryBegin)).StatusCode;
                }
                else if (provider == "twilio")
                {
                    var config = new TwilioSmsConfig
                    {
                        Enabled = true, ValidateSignature = true, WebhookPublicBaseUrl = "https://example.test",
                        AllowedFromNumbers = ["+15551234567"], AllowedToNumbers = ["+15557654321"]
                    };
                    var handler = new TwilioSmsWebhookHandler(config, "fixture", new FileContactStore(root),
                        allowlists, recent, AllowlistSemantics.Strict);
                    var form = new Dictionary<string, string>
                    {
                        ["From"] = "+15551234567", ["To"] = "+15557654321", ["Body"] = "hello", ["MessageSid"] = "one"
                    };
                    var signature = valid ? TwilioWebhookVerifier.ComputeSignature(handler.PublicWebhookUrl, form, "fixture") : "invalid";
                    status = (await handler.HandleAsync(form, signature, Enqueue, ct, TryBegin)).StatusCode;
                }
                else
                {
                    var handler = new WhatsAppWebhookHandler(new WhatsAppChannelConfig
                    {
                        Enabled = true, Type = provider, ValidateSignature = true, WebhookAppSecret = "fixture", BridgeToken = "fixture"
                    }, allowlists, recent, AllowlistSemantics.Legacy, NullLogger<WhatsAppWebhookHandler>.Instance);
                    var body = provider == "official"
                        ? """{"entry":[{"changes":[{"value":{"messages":[{"from":"15551234567","id":"one","type":"text","text":{"body":"hello"}}]}}]}]}"""
                        : """{"from":"15551234567","text":"hello","message_id":"one"}""";
                    var context = new DefaultHttpContext();
                    context.Request.Method = "POST";
                    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                    context.Request.Headers["X-Hub-Signature-256"] = valid
                        ? "sha256=" + GatewaySecurity.ComputeHmacSha256Hex("fixture", body) : "invalid";
                    context.Request.Headers.Authorization = valid ? "Bearer fixture" : "Bearer invalid";
                    status = (await handler.HandleAsync(context, Enqueue, ct, TryBegin)).StatusCode;
                }

                Assert.Equal(valid ? 200 : 401, status);
                Assert.Equal(attempt, reservations);
                Assert.Equal(valid ? 1 : 0, deliveries);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
