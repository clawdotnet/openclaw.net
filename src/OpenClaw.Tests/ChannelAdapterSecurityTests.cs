using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChannelAdapterSecurityTests
{
    [Fact]
    public void Ed25519Verify_AcceptsValidSignature()
    {
        var privateKey = Enumerable.Range(0, 32).Select(static i => (byte)(i + 1)).ToArray();
        var publicKey = new Ed25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();
        var message = Encoding.UTF8.GetBytes("1234567890{\"type\":1}");

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();

        Assert.True(Ed25519Verify.Verify(signature, message, publicKey));
    }

    [Fact]
    public async Task DiscordWebhookHandler_RejectsDisallowedGuild()
    {
        var handler = new DiscordWebhookHandler(
            new DiscordChannelConfig
            {
                ValidateSignature = false,
                AllowedGuildIds = ["allowed-guild"]
            },
            NullLogger<DiscordWebhookHandler>.Instance);

        var payload = """
            {
              "id":"1",
              "type":2,
              "guild_id":"blocked-guild",
              "channel_id":"channel-1",
              "member":{"user":{"id":"user-1","username":"tester"}},
              "data":{"name":"claw","options":[{"name":"message","value":"hello"}]}
            }
            """;
        var enqueued = false;

        var result = await handler.HandleAsync(
            payload,
            signatureHeader: null,
            timestampHeader: null,
            (msg, ct) =>
            {
                enqueued = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
        Assert.False(enqueued);
    }

    [Fact]
    public async Task SlackWebhookHandler_SlashCommand_RejectsDisallowedWorkspace()
    {
        var handler = new SlackWebhookHandler(
            new SlackChannelConfig
            {
                ValidateSignature = false,
                AllowedWorkspaceIds = ["allowed-workspace"]
            },
            new AllowlistManager(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                NullLogger<AllowlistManager>.Instance),
            new RecentSendersStore(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                NullLogger<RecentSendersStore>.Instance),
            AllowlistSemantics.Legacy,
            NullLogger<SlackWebhookHandler>.Instance);

        var result = await handler.HandleSlashCommandAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user_id"] = "user-1",
                ["team_id"] = "blocked-workspace",
                ["channel_id"] = "C123",
                ["command"] = "/claw",
                ["text"] = "hello"
            },
            timestampHeader: null,
            signatureHeader: null,
            rawBody: "user_id=user-1",
            (msg, ct) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task DiscordChannel_SendAsync_RecreatesRequestAfterRateLimit()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);
        var requestCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            return responses.Dequeue();
        }));

        var channel = new DiscordChannel(
            new DiscordChannelConfig
            {
                BotToken = "token",
                RegisterSlashCommands = false
            },
            NullLogger<DiscordChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "discord",
                RecipientId = "123",
                Text = "hello"
            },
            CancellationToken.None);

        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task WhatsAppChannel_SendAsync_MediaMarkerBuildsCloudPayload()
    {
        string? capturedPayload = null;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            capturedPayload = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var channel = new WhatsAppChannel(
            new WhatsAppChannelConfig
            {
                PhoneNumberId = "phone-1",
                CloudApiToken = "cloud-token"
            },
            http,
            NullLogger<WhatsAppChannel>.Instance);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "whatsapp",
                RecipientId = "15551234567",
                Text = "[IMAGE_URL:https://cdn.example.test/cat.png]\ncaption",
                ReplyToMessageId = "msg-1"
            },
            CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var payload = JsonDocument.Parse(capturedPayload!).RootElement;
        Assert.Equal("image", payload.GetProperty("type").GetString());
        Assert.Equal("15551234567", payload.GetProperty("to").GetString());
        Assert.Equal("https://cdn.example.test/cat.png", payload.GetProperty("image").GetProperty("link").GetString());
        Assert.Equal("caption", payload.GetProperty("image").GetProperty("caption").GetString());
        Assert.Equal("msg-1", payload.GetProperty("context").GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task WhatsAppChannel_SendAsync_NonSuccessResponseThrows()
    {
        using var http = new HttpClient(new CallbackHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var channel = new WhatsAppChannel(
            new WhatsAppChannelConfig
            {
                PhoneNumberId = "phone-1",
                CloudApiToken = "cloud-token"
            },
            http,
            NullLogger<WhatsAppChannel>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "whatsapp",
                RecipientId = "15551234567",
                Text = "hello"
            },
            CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhatsAppBridgeChannel_SendAsync_MarkerOnlyMessagePreservesAttachments()
    {
        string? capturedPayload = null;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            capturedPayload = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var channel = new WhatsAppBridgeChannel(
            new WhatsAppChannelConfig
            {
                BridgeUrl = "https://bridge.example.test/send"
            },
            http,
            NullLogger<WhatsAppBridgeChannel>.Instance);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "whatsapp",
                RecipientId = "group-1@g.us",
                Text = "[VIDEO_URL:https://cdn.example.test/clip.mp4]"
            },
            CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var payload = JsonDocument.Parse(capturedPayload!).RootElement;
        Assert.Equal("", payload.GetProperty("text").GetString());
        var attachment = Assert.Single(payload.GetProperty("attachments").EnumerateArray());
        Assert.Equal("video", attachment.GetProperty("type").GetString());
        Assert.Equal("https://cdn.example.test/clip.mp4", attachment.GetProperty("url").GetString());
    }

    [Fact]
    public async Task WhatsAppWebhookHandler_OfficialWebhookRecordsSenderName()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recentSenders = new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance);
            var allowlists = new AllowlistManager(root, NullLogger<AllowlistManager>.Instance);
            var handler = new WhatsAppWebhookHandler(
                new WhatsAppChannelConfig
                {
                    Enabled = true,
                    Type = "official",
                    ValidateSignature = false
                },
                allowlists,
                recentSenders,
                AllowlistSemantics.Legacy,
                NullLogger<WhatsAppWebhookHandler>.Instance);

            var body =
                """
                {
                  "entry": [
                    {
                      "changes": [
                        {
                          "value": {
                            "contacts": [
                              {
                                "wa_id": "15551234567",
                                "profile": { "name": "Alice" }
                              }
                            ],
                            "messages": [
                              {
                                "from": "15551234567",
                                "id": "wamid-1",
                                "type": "text",
                                "text": { "body": "hello" }
                              }
                            ]
                          }
                        }
                      ]
                    }
                  ]
                }
                """;

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

            var result = await handler.HandleAsync(context, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
            var latest = recentSenders.TryGetLatest("whatsapp");

            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(latest);
            Assert.Equal("Alice", latest!.SenderName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
