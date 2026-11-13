using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TelegramDeliveryTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task SendAsync_WhenBotApiRejects_ShouldReportFailure(int httpStatus)
    {
        using var http = new HttpClient(new Handler(_ => Response(httpStatus, """{"ok":false,"error_code":400,"description":"rejected"}""")));
        await using var adapter = CreateChannel(http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message("hello"), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, error.DeliveredParts);
        Assert.False(error.CanRetry);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverAsync_WhenMultipartSendFails_ShouldNotResendDeliveredParts(bool media)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => ++calls == 1
            ? Response(200, """{"ok":true,"result":{"message_id":1}}""")
            : Response(429, """{"ok":false,"error_code":429,"description":"rate limited"}""")));
        await using var adapter = CreateChannel(http);
        var text = media ? "[IMAGE_URL:https://example.test/one.png]\n[IMAGE_URL:https://example.test/two.png]" : new string('a', 5000);
        var delivered = await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message(text), NullLogger.Instance,
            TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask);
        Assert.False(delivered);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DeliverAsync_WhenTransportOutcomeUnknown_ShouldNotRetryOrReportSuccess()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("connection lost"); }));
        await using var adapter = CreateChannel(http);
        Assert.False(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message("hello"), NullLogger.Instance,
            TestContext.Current.CancellationToken, (_, _) => Task.CompletedTask));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DeliverAsync_WhenFirstAttemptIsExplicitlyRejected_ShouldRetryAndReportSuccess()
    {
        var calls = 0;
        TimeSpan? observedDelay = null;
        using var http = new HttpClient(new Handler(_ => ++calls == 1
            ? Response(429, """{"ok":false,"error_code":429,"description":"rate limited","parameters":{"retry_after":3}}""")
            : Response(200, """{"ok":true,"result":{"message_id":1}}""")));
        await using var adapter = CreateChannel(http);
        Assert.True(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message("hello"), NullLogger.Instance,
            TestContext.Current.CancellationToken, (delay, _) => { observedDelay = delay; return Task.CompletedTask; }));
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(3), observedDelay);
    }

    [Fact]
    public async Task DeliverAsync_WhenRetryAfterIsLong_ShouldHonorDelay()
    {
        var calls = 0;
        TimeSpan? observedDelay = null;
        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? ValueTask.FromException(new ChannelDeliveryException(
                    "rate limited", canRetry: true, retryAfter: TimeSpan.FromSeconds(60)))
                : ValueTask.CompletedTask);

        Assert.True(await GatewayOutboundDeliveryWorker.DeliverAsync(
            adapter, Message("hello"), NullLogger.Instance, TestContext.Current.CancellationToken,
            (delay, _) => { observedDelay = delay; return Task.CompletedTask; }));
        Assert.Equal(TimeSpan.FromSeconds(60), observedDelay);
    }

    [Fact]
    public async Task DeliverAsync_WhenSendIsCanceledWithoutShutdown_ShouldNotRetry()
    {
        var calls = 0;
        var adapter = Substitute.For<IChannelAdapter>();
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return ValueTask.FromException(new OperationCanceledException("transport timeout"));
            });

        Assert.False(await GatewayOutboundDeliveryWorker.DeliverAsync(
            adapter, Message("hello"), NullLogger.Instance, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SendAsync_WhenSuccessBodyInvalid_ShouldReportUnknownOutcome()
    {
        using var http = new HttpClient(new Handler(_ => Response(200, "invalid json")));
        await using var adapter = CreateChannel(http);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message("hello"), TestContext.Current.CancellationToken).AsTask());
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }

    [Fact]
    public async Task DeliverAsync_WhenShutdownCancelsSend_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }));
        await using var adapter = CreateChannel(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message("hello"), NullLogger.Instance, cts.Token));
    }

    [Fact]
    public async Task OutboundWorker_WhenTelegramRejectsHeartbeat_ShouldLeaveDeliveryTimestampUnset()
    {
        var root = Path.Join(Path.GetTempPath(), "openclaw-telegram-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
            var memory = new FileMemoryStore(root, 4);
            var sessions = new SessionManager(memory, config, NullLogger.Instance);
            var heartbeat = new HeartbeatService(config, memory, sessions, NullLogger<HeartbeatService>.Instance);
            heartbeat.RecordResult(new Session { Id = "heartbeat:default", ChannelId = "telegram", SenderId = "1234" },
                "Alert", suppressed: false, inputTokenDelta: 0, outputTokenDelta: 0);
            var nextMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var http = new HttpClient(new Handler(_ =>
            {
                if (++calls == 1)
                    return Response(400, """{"ok":false,"error_code":400,"description":"rejected"}""");
                nextMessage.TrySetResult();
                return Response(200, """{"ok":true,"result":{"message_id":1}}""");
            }));
            await using var adapter = CreateChannel(http);
            var pipeline = new MessagePipeline();
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(TestContext.Current.CancellationToken);
            new GatewayOutboundDeliveryWorker().Start(lifetime, NullLogger.Instance, 1, pipeline,
                new Dictionary<string, IChannelAdapter> { ["telegram"] = adapter }, heartbeat);
            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
            {
                ChannelId = "telegram", RecipientId = "1234", Text = "Alert",
                SessionId = "heartbeat:default", CronJobName = "heartbeat.default"
            }, TestContext.Current.CancellationToken);
            await pipeline.OutboundWriter.WriteAsync(Message("sentinel"), TestContext.Current.CancellationToken);
            pipeline.OutboundWriter.Complete();
            await nextMessage.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(heartbeat.LoadStatus()!.LastDeliveredAtUtc);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OutboundWorker_WhenHeartbeatPersistenceFails_ShouldContinueDelivering()
    {
        var root = Path.Join(Path.GetTempPath(), "openclaw-heartbeat-persistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
            var memory = new FileMemoryStore(root, 4);
            var sessions = new SessionManager(memory, config, NullLogger.Instance);
            var heartbeat = new HeartbeatService(config, memory, sessions, NullLogger<HeartbeatService>.Instance);
            heartbeat.RecordResult(new Session { Id = "heartbeat:default", ChannelId = "telegram", SenderId = "1234" },
                "Alert", suppressed: false, inputTokenDelta: 0, outputTokenDelta: 0);
            File.Delete(heartbeat.StatusPath);
            Directory.CreateDirectory(heartbeat.StatusPath);

            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adapter = Substitute.For<IChannelAdapter>();
            var calls = 0;
            adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    if (++calls == 2)
                        delivered.TrySetResult();
                    return ValueTask.CompletedTask;
                });
            var pipeline = new MessagePipeline();
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(TestContext.Current.CancellationToken);
            new GatewayOutboundDeliveryWorker().Start(lifetime, NullLogger.Instance, 1, pipeline,
                new Dictionary<string, IChannelAdapter> { ["telegram"] = adapter }, heartbeat);
            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
            {
                ChannelId = "telegram", RecipientId = "1234", Text = "Alert",
                SessionId = "heartbeat:default", CronJobName = "heartbeat.default"
            }, TestContext.Current.CancellationToken);
            await pipeline.OutboundWriter.WriteAsync(Message("sentinel"), TestContext.Current.CancellationToken);
            pipeline.OutboundWriter.Complete();

            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TelegramChannel CreateChannel(HttpClient http)
        => new(new TelegramChannelConfig { BotToken = "fixture" }, NullLogger<TelegramChannel>.Instance, http);

    private static OutboundMessage Message(string text) => new() { ChannelId = "telegram", RecipientId = "1234", Text = text };
    private static HttpResponseMessage Response(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
}
