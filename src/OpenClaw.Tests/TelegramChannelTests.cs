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
    public async Task StartAsync_WebhookMode_DoesNotCallTelegramApi()
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
                BotTokenRef = "raw:test-token",
                UpdateMode = "webhook"
            },
            NullLogger<TelegramChannel>.Instance,
            http);

        await channel.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(called);
    }

    [Fact]
    public async Task StartAsync_LongPolling_RemovesWebhookProcessesUpdateAndAdvancesOffset()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = new List<string>();
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);

            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
            {
                return JsonResponse("""{"ok":true,"result":true}""");
            }

            getUpdatesCount++;
            if (getUpdatesCount == 1)
            {
                return JsonResponse(
                    """
                    {
                      "ok": true,
                      "result": [
                        {
                          "update_id": 1000,
                          "message": {
                            "message_id": 7,
                            "chat": { "id": 12345, "type": "private" },
                            "text": "hello polling"
                          }
                        }
                      ]
                    }
                    """);
            }

            cts.Cancel();
            return JsonResponse("""{"ok":true,"result":[]}""");
        }));

        var payloads = new List<string>();
        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token",
                UpdateMode = "long-polling",
                PollingTimeoutSeconds = 30,
                DropPendingUpdatesOnStart = false
            },
            NullLogger<TelegramChannel>.Instance,
            http,
            (payload, _, _) =>
            {
                payloads.Add(payload);
                return ValueTask.CompletedTask;
            });

        await channel.StartAsync(cts.Token);

        Assert.Single(payloads);
        Assert.Contains(requests, url => url.Contains("/deleteWebhook?drop_pending_updates=false", StringComparison.Ordinal));
        Assert.Contains(requests, url => url.Contains("/getUpdates?offset=1001&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_LongPolling_DropsPendingUpdatesWhenConfigured()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = new List<string>();
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);

            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            cts.Cancel();
            return JsonResponse("""{"ok":true,"result":[]}""");
        }));

        await using var channel = new TelegramChannel(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token",
                UpdateMode = "long-polling",
                DropPendingUpdatesOnStart = true
            },
            NullLogger<TelegramChannel>.Instance,
            http,
            static (_, _, _) => ValueTask.CompletedTask);

        await channel.StartAsync(cts.Token);

        Assert.Contains(requests, url => url.Contains("/deleteWebhook?drop_pending_updates=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_LongPolling_ProcessorFailureRetriesFailedUpdateWithoutAdvancingOffset()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requests = new List<string>();
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);

            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            getUpdatesCount++;
            return getUpdatesCount == 1
                ? JsonResponse(
                    """
                    {
                      "ok": true,
                      "result": [
                        { "update_id": 1000 },
                        { "update_id": 1001 }
                      ]
                    }
                    """)
                : JsonResponse("""{"ok":true,"result":[{"update_id":1001}]}""");
        }));

        var processedUpdateIds = new List<long>();
        var failedOnce = false;
        var delays = new List<TimeSpan>();
        await using var channel = CreateLongPollingChannel(
            http,
            (payload, _, _) =>
            {
                using var update = JsonDocument.Parse(payload);
                var updateId = update.RootElement.GetProperty("update_id").GetInt64();
                processedUpdateIds.Add(updateId);
                if (updateId == 1001 && !failedOnce)
                {
                    failedOnce = true;
                    throw new InvalidOperationException("Simulated processor failure.");
                }

                if (updateId == 1001)
                    cts.Cancel();
                return ValueTask.CompletedTask;
            },
            delays);

        await channel.StartAsync(cts.Token);

        Assert.Equal([1000, 1001, 1001], processedUpdateIds);
        Assert.Single(delays);
        Assert.Contains(requests, url => url.Contains("/getUpdates?offset=1001&", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, url => url.Contains("/getUpdates?offset=1002&", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_LongPolling_RateLimitUsesTelegramRetryAfter()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            getUpdatesCount++;
            if (getUpdatesCount == 1)
            {
                return JsonResponse(
                    """
                    {
                      "ok": false,
                      "error_code": 429,
                      "description": "Too Many Requests: retry after 17",
                      "parameters": { "retry_after": 17 }
                    }
                    """,
                    HttpStatusCode.TooManyRequests);
            }

            cts.Cancel();
            return JsonResponse("""{"ok":true,"result":[]}""");
        }));

        var delays = new List<TimeSpan>();
        await using var channel = CreateLongPollingChannel(
            http,
            static (_, _, _) => ValueTask.CompletedTask,
            delays);

        await channel.StartAsync(cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(17), Assert.Single(delays));
        Assert.Equal(2, getUpdatesCount);
    }

    [Fact]
    public async Task StartAsync_LongPolling_ConflictUsesMinimumRetryDelay()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            getUpdatesCount++;
            if (getUpdatesCount == 1)
            {
                return JsonResponse(
                    """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""",
                    HttpStatusCode.Conflict);
            }

            cts.Cancel();
            return JsonResponse("""{"ok":true,"result":[]}""");
        }));

        var delays = new List<TimeSpan>();
        await using var channel = CreateLongPollingChannel(
            http,
            static (_, _, _) => ValueTask.CompletedTask,
            delays);

        await channel.StartAsync(cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
        Assert.Equal(2, getUpdatesCount);
    }

    [Fact]
    public async Task StartAsync_LongPolling_TemporaryErrorsUseExponentialBackoff()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            getUpdatesCount++;
            if (getUpdatesCount <= 2)
            {
                return JsonResponse(
                    """{"ok":false,"error_code":503,"description":"Service temporarily unavailable"}""",
                    HttpStatusCode.ServiceUnavailable);
            }

            cts.Cancel();
            return JsonResponse("""{"ok":true,"result":[]}""");
        }));

        var delays = new List<TimeSpan>();
        await using var channel = CreateLongPollingChannel(
            http,
            static (_, _, _) => ValueTask.CompletedTask,
            delays);

        await channel.StartAsync(cts.Token);

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
        Assert.Equal(3, getUpdatesCount);
    }

    [Fact]
    public async Task StartAsync_LongPolling_UnauthorizedErrorStopsWithoutRetrying()
    {
        var getUpdatesCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/deleteWebhook", StringComparison.Ordinal))
                return JsonResponse("""{"ok":true,"result":true}""");

            getUpdatesCount++;
            return JsonResponse(
                """{"ok":false,"error_code":401,"description":"Unauthorized"}""",
                HttpStatusCode.Unauthorized);
        }));

        var delays = new List<TimeSpan>();
        await using var channel = CreateLongPollingChannel(
            http,
            static (_, _, _) => ValueTask.CompletedTask,
            delays);

        var exception = await Assert.ThrowsAsync<TelegramBotApiException>(
            () => channel.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(401, exception.ErrorCode);
        Assert.Empty(delays);
        Assert.Equal(1, getUpdatesCount);
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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

        Assert.False(called);
    }

    [Fact]
    public async Task TelegramWebhookHandler_ChannelPost_EnqueuesChatMessage()
    {
        var root = Path.Join(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
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
                TestContext.Current.CancellationToken);

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
        var root = Path.Join(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new TelegramWebhookHandler(
                new TelegramChannelConfig { Enabled = true },
                new AllowlistManager(root, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance),
                AllowlistSemantics.Legacy,
                NullLogger<TelegramWebhookHandler>.Instance);

            var result = await handler.HandleAsync("{", (_, _) => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

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

    private static TelegramChannel CreateLongPollingChannel(
        HttpClient http,
        TelegramUpdateProcessor updateProcessor,
        ICollection<TimeSpan> delays)
        => new(
            new TelegramChannelConfig
            {
                Enabled = true,
                BotTokenRef = "raw:test-token",
                UpdateMode = "long-polling",
                PollingRetryDelaySeconds = 5
            },
            NullLogger<TelegramChannel>.Instance,
            http,
            updateProcessor,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
