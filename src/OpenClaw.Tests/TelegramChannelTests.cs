using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class TelegramChannelTests
{
    [Fact]
    public async Task Constructor_ResolvesRawBotTokenRef()
    {
        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance);

        Assert.Equal("telegram", channel.ChannelId);
    }

    [Fact]
    public async Task Constructor_ResolvesEnvBotTokenRef()
    {
        const string envName = "OPENCLAW_TEST_TELEGRAM_TOKEN";
        Environment.SetEnvironmentVariable(envName, "env-token");

        try
        {
            await using var channel = new TelegramChannel(
                new TelegramChannelConfig
                {
                    Enabled = true,
                    BotTokenRef = $"env:{envName}"
                },
                NullLogger<TelegramChannel>.Instance);

            Assert.Equal("telegram", channel.ChannelId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public async Task SendAsync_ChannelUsernameAndDocumentMarker_SendsDocumentWithReply()
    {
        var requests = new List<(string Url, string Body)>();
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            requests.Add((request.RequestUri!.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "telegram",
                RecipientId = "@openclaw_updates",
                Text = "[DOCUMENT_URL:https://cdn.example.test/report.pdf]\nReport ready",
                ReplyToMessageId = "42"
            },
            CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.EndsWith("/sendDocument", request.Url, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("@openclaw_updates", root.GetProperty("chat_id").GetString());
        Assert.Equal("https://cdn.example.test/report.pdf", root.GetProperty("document").GetString());
        Assert.Equal("Report ready", root.GetProperty("caption").GetString());
        Assert.Equal(42, root.GetProperty("reply_to_message_id").GetInt32());
    }

    [Fact]
    public async Task SendAsync_LongMediaCaption_StaysWithinTelegramCaptionLimit()
    {
        var requests = new List<(string Url, string Body)>();
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            requests.Add((request.RequestUri!.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "telegram",
                RecipientId = "-1001234567890",
                Text = "[IMAGE_URL:https://cdn.example.test/cat.png]\n" + new string('a', 1030)
            },
            CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.EndsWith("/sendPhoto", requests[0].Url, StringComparison.Ordinal);
        Assert.EndsWith("/sendMessage", requests[1].Url, StringComparison.Ordinal);

        using var photoDocument = JsonDocument.Parse(requests[0].Body);
        var caption = photoDocument.RootElement.GetProperty("caption").GetString();
        Assert.NotNull(caption);
        Assert.Equal(1024, caption!.Length);
        Assert.EndsWith("…", caption, StringComparison.Ordinal);

        using var messageDocument = JsonDocument.Parse(requests[1].Body);
        Assert.False(messageDocument.RootElement.TryGetProperty("reply_to_message_id", out _));
        Assert.Equal(-1001234567890, messageDocument.RootElement.GetProperty("chat_id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(messageDocument.RootElement.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task SendAsync_StickerWithText_SendsTextFollowUp()
    {
        var requests = new List<(string Url, string Body)>();
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            requests.Add((request.RequestUri!.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "telegram",
                RecipientId = "12345",
                Text = "[STICKER_URL:https://cdn.example.test/sticker.webp]\nsticker caption"
            },
            CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.EndsWith("/sendSticker", requests[0].Url, StringComparison.Ordinal);
        Assert.EndsWith("/sendMessage", requests[1].Url, StringComparison.Ordinal);

        using var stickerDocument = JsonDocument.Parse(requests[0].Body);
        Assert.Equal("https://cdn.example.test/sticker.webp", stickerDocument.RootElement.GetProperty("sticker").GetString());
        Assert.False(stickerDocument.RootElement.TryGetProperty("caption", out _));

        using var messageDocument = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("sticker caption", messageDocument.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_BareUsername_DoesNotCallTelegramApi()
    {
        var called = false;
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token"
            },
            NullLogger<TelegramChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "telegram",
                RecipientId = "openclaw_updates",
                Text = "hello"
            },
            CancellationToken.None);

        Assert.False(called);
    }

    [Fact]
    public async Task TelegramWebhookHandler_ChannelPost_EnqueuesChatMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new TelegramWebhookHandler(
                new TelegramChannelConfig
                {
                    Enabled = true,
                    AllowedFromUserIds = ["-1001234567890"]
                },
                new AllowlistManager(root, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance),
                AllowlistSemantics.Strict,
                NullLogger<TelegramWebhookHandler>.Instance);

            InboundMessage? captured = null;
            var result = await handler.HandleAsync(
                """
                {
                  "update_id": 1000,
                  "channel_post": {
                    "message_id": 7,
                    "chat": {
                      "id": -1001234567890,
                      "title": "OpenClaw Updates",
                      "type": "channel"
                    },
                    "text": "hello channel"
                  }
                }
                """,
                (message, _) =>
                {
                    captured = message;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(captured);
            Assert.Equal("-1001234567890", captured!.SenderId);
            Assert.Equal("OpenClaw Updates", captured.SenderName);
            Assert.Equal("7", captured.MessageId);
            Assert.Equal("hello channel", captured.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TelegramWebhookHandler_InvalidJson_ReturnsBadRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new TelegramWebhookHandler(
                new TelegramChannelConfig { Enabled = true },
                new AllowlistManager(root, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance),
                AllowlistSemantics.Legacy,
                NullLogger<TelegramWebhookHandler>.Instance);

            var result = await handler.HandleAsync("{", (_, _) => ValueTask.CompletedTask, CancellationToken.None);

            Assert.Equal(400, result.StatusCode);
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
