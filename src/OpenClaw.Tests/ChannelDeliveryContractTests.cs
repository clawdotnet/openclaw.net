using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChannelDeliveryContractTests
{
    public static TheoryData<string> HttpAdapters => new()
    {
        "slack", "discord", "teams", "feishu", "dingtalk", "wecom", "whatsapp", "bridge", "sms"
    };

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task SendAsync_WhenProviderRejects_ShouldReportKnownFailure(string name)
    {
        using var http = Client(_ => Reply(400, "{}"));
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.False(error.CanRetry);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(0, error.DeliveredParts);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task SendAsync_WhenProviderAccepts_ShouldComplete(string name)
    {
        using var http = Client(_ => Success(name));
        await using var adapter = Create(name, http);
        await adapter.SendAsync(Message(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task DeliverAsync_WhenRateLimitedBeforeAcceptance_ShouldHonorRetryDelay(string name)
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            if (++calls > 1) return Success(name);
            var response = Reply(429, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        await using var adapter = Create(name, http);
        TimeSpan? delay = null;
        Assert.True(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message(), NullLogger.Instance,
            TestContext.Current.CancellationToken, (duration, _) => { delay = duration; return Task.CompletedTask; }));
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task DeliverAsync_WhenTransportFails_ShouldNotReplayUnknownOutcome(string name)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; throw new HttpRequestException("connection lost"); });
        await using var adapter = Create(name, http);
        Assert.False(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message(), NullLogger.Instance,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task SendAsync_WhenCallerCancelsDuringSend_ShouldPropagateCancellation(string name)
    {
        using var cancellation = new CancellationTokenSource();
        using var http = Client(_ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        await using var adapter = Create(name, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.SendAsync(Message(), cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData("slack", "{\"ok\":false}")]
    [InlineData("feishu", "{\"code\":999}")]
    [InlineData("wecom", "{\"errcode\":40014}")]
    [InlineData("dingtalk", "{\"code\":\"Forbidden\"}")]
    public async Task SendAsync_WhenSuccessfulHttpContainsRejection_ShouldNotReportSuccess(string name, string body)
    {
        using var http = Client(_ => Reply(200, body));
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.False(error.CanRetry);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData("discord", 429)]
    [InlineData("teams", 429)]
    [InlineData("discord", 500)]
    [InlineData("teams", 500)]
    public async Task SendAsync_WhenLaterChunkFails_ShouldPreserveAcceptedCountAndStop(string name, int status)
    {
        var calls = 0;
        using var http = Client(_ => ++calls == 1 ? Success(name) : Reply(status, "{}"));
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(new string('a', 5000)), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, error.DeliveredParts);
        Assert.False(error.CanRetry);
        Assert.Equal(status == 500, error.OutcomeUnknown);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("feishu")]
    [InlineData("dingtalk")]
    [InlineData("wecom")]
    public async Task SendAsync_WhenMediaFailsAfterText_ShouldReportPartialDelivery(string name)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Success(name); });
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(
            Message("hello\n[FILE_PATH:/nonexistent/channel-delivery-fixture.dat]"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, error.DeliveredParts);
        Assert.False(error.CanRetry);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DeliverAsync_WhenSmsSuppressed_ShouldNotSendOrReportSuccess()
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Success("sms"); });
        await using var adapter = new TwilioSmsChannel(new TwilioSmsConfig { AllowedToNumbers = ["+19999999999"] }, "token", SubstituteContacts(), http);
        await Assert.ThrowsAsync<ChannelDeliverySuppressedException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.False(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message(), NullLogger.Instance, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SendAsync_WhenWebSocketRecipientMissing_ShouldReject()
    {
        await using var adapter = new WebSocketChannel(new WebSocketConfig());
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task SendAsync_WhenServerOutcomeIsAmbiguous_ShouldReportUnknown(string name)
    {
        using var http = Client(_ => Reply(503, "{}"));
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("feishu")]
    [InlineData("dingtalk")]
    [InlineData("wecom")]
    public async Task SendAsync_WhenAcknowledgmentIsMalformed_ShouldReportUnknown(string name)
    {
        using var http = Client(_ => Reply(200, "not json"));
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(), TestContext.Current.CancellationToken).AsTask());
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("discord")]
    [InlineData("whatsapp")]
    public async Task SendAsync_WhenContentWouldBeOmitted_ShouldRejectBeforeDispatch(string name)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Success(name); });
        await using var adapter = Create(name, http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message(
            "hello\n[IMAGE_URL:https://fixture.test/one.png]\n[IMAGE_URL:https://fixture.test/two.png]"), TestContext.Current.CancellationToken).AsTask());
        Assert.False(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
        Assert.Equal(0, calls);
    }

    private static OutboundMessage Message(string text = "hello") => new() { ChannelId = "fixture", RecipientId = "+15551234567", Text = text };
    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static HttpResponseMessage Success(string name) => Reply(200, name switch
    {
        "slack" => "{\"ok\":true}", "feishu" => "{\"code\":0}", "wecom" => "{\"errcode\":0}", "dingtalk" => "{\"code\":0}", _ => "{}"
    });
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private static IContactStore SubstituteContacts() => NSubstitute.Substitute.For<IContactStore>();

    private static IChannelAdapter Create(string name, HttpClient http)
    {
        IChannelAdapter adapter = name switch
        {
            "slack" => new SlackChannel(new SlackChannelConfig { BotToken = "fixture" }, NullLogger<SlackChannel>.Instance, http),
            "discord" => new DiscordChannel(new DiscordChannelConfig { BotToken = "fixture" }, NullLogger<DiscordChannel>.Instance, http),
            "teams" => new TeamsChannel(new TeamsChannelConfig { AppId = "fixture", AppPassword = "fixture", TenantId = "fixture", TextChunkLimit = 2000 }, http, NullLogger<TeamsChannel>.Instance),
            "feishu" => new FeishuChannel(new FeishuChannelConfig(), NullLogger<FeishuChannel>.Instance, http),
            "dingtalk" => new DingTalkChannel(new DingTalkChannelConfig(), NullLogger<DingTalkChannel>.Instance, http),
            "wecom" => new WeComChannel(new WeComChannelConfig(), NullLogger<WeComChannel>.Instance, http),
            "whatsapp" => new WhatsAppChannel(new WhatsAppChannelConfig { PhoneNumberId = "fixture", CloudApiToken = "fixture" }, http, NullLogger<WhatsAppChannel>.Instance),
            "bridge" => new WhatsAppBridgeChannel(new WhatsAppChannelConfig { BridgeUrl = "https://fixture.test/send", BridgeSuppressSendExceptions = true }, http, NullLogger<WhatsAppBridgeChannel>.Instance),
            "sms" => new TwilioSmsChannel(new TwilioSmsConfig { AccountSid = "fixture", FromNumber = "+15550000000", AllowedToNumbers = ["+15551234567"] }, "fixture", SubstituteContacts(), http),
            _ => throw new ArgumentException(name)
        };
        // Seed only credential caches, keeping tests focused on outbound transport rather than startup listeners.
        foreach (var field in new[] { "_appAccessToken", "_accessToken", "_cachedToken", "_corpId", "_corpSecret" })
            adapter.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(adapter, "fixture");
        adapter.GetType().GetField("_tokenExpiry", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(adapter, DateTimeOffset.UtcNow.AddHours(2));
        if (adapter is TeamsChannel teams)
            teams.StoreConversationReference("+15551234567", new TeamsConversationReference { ServiceUrl = "https://fixture.test", ConversationId = "fixture" });
        return adapter;
    }
}
