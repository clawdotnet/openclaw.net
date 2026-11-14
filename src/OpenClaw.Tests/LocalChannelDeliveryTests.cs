using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LocalChannelDeliveryTests
{
    private static OutboundMessage Message => new() { ChannelId = "fixture", RecipientId = "fixture", Text = "hello" };

    [Theory]
    [InlineData("email")]
    [InlineData("signal")]
    [InlineData("cron")]
    [InlineData("websocket")]
    public async Task SendAsync_WhenAlreadyCanceled_ShouldPropagateWithoutDispatch(string name)
    {
        var root = Path.Join(Path.GetTempPath(), "delivery-local", Guid.NewGuid().ToString("N"));
        await using IChannelAdapter adapter = name switch
        {
            "email" => new EmailChannel(new EmailConfig(), NullLogger<EmailChannel>.Instance),
            "signal" => new SignalChannel(new SignalChannelConfig { AccountPhoneNumber = "+15550000000" }, NullLogger<SignalChannel>.Instance),
            "cron" => new CronChannel(root, NullLogger<CronChannel>.Instance),
            _ => new WebSocketChannel(new WebSocketConfig())
        };
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.SendAsync(Message, canceled.Token).AsTask());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SendAsync_WhenSignalCliFails_ShouldReportUnknownWithoutRetry()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var adapter = new SignalChannel(new SignalChannelConfig
        {
            AccountPhoneNumber = "+15550000000", Driver = "signal-cli", SignalCliPath = "/usr/bin/false"
        }, NullLogger<SignalChannel>.Instance);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message, TestContext.Current.CancellationToken).AsTask());
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }

    [Fact]
    public async Task DeliverAsync_WhenLegacyAdapterThrows_ShouldNotReplayUnclassifiedFailure()
    {
        var adapter = Substitute.For<IChannelAdapter>();
        var calls = 0;
        adapter.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return ValueTask.FromException(new IOException("connection lost"));
        });
        Assert.False(await GatewayOutboundDeliveryWorker.DeliverAsync(adapter, Message, NullLogger.Instance, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTimeoutAfterAcceptedPart_ShouldPreservePartialUnknownOutcome()
    {
        var delivery = new ChannelDeliveryAttempt();
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => delivery.ExecuteAsync(() =>
        {
            delivery.RecordAccepted();
            throw new OperationCanceledException("provider timed out");
        }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, error.DeliveredParts);
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }
    [Fact]
    public async Task SendAsync_WhenCronAppendSucceedsOrFails_ShouldReportActualOutcome()
    {
        var root = Path.Join(Path.GetTempPath(), "cron-delivery", Guid.NewGuid().ToString("N"));
        try
        {
            await using var adapter = new CronChannel(root, NullLogger<CronChannel>.Instance);
            await adapter.SendAsync(Message, TestContext.Current.CancellationToken);
            var output = Assert.Single(Directory.GetFiles(Path.Join(root, "cron")));
            Assert.Contains("hello", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Directory.Delete(Path.Join(root, "cron"), recursive: true);
            await File.WriteAllTextAsync(Path.Join(root, "cron"), "not a directory", TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message, TestContext.Current.CancellationToken).AsTask());
            Assert.True(error.OutcomeUnknown);
            Assert.False(error.CanRetry);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SendAsync_WhenSignaldOnlyAcceptsSocketWrite_ShouldReportUnknown()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Join("/tmp", $"signal-{Guid.NewGuid():N}.sock");
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            await using var adapter = new SignalChannel(new SignalChannelConfig
            {
                AccountPhoneNumber = "+15550000000", Driver = "signald", SocketPath = path
            }, NullLogger<SignalChannel>.Instance);
            var accept = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();
            var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => adapter.SendAsync(Message, TestContext.Current.CancellationToken).AsTask());
            using var peer = await accept;
            var bytes = new byte[4096];
            Assert.True(await peer.ReceiveAsync(bytes, TestContext.Current.CancellationToken) > 0);
            Assert.True(error.OutcomeUnknown);
            Assert.False(error.CanRetry);
        }
        finally { File.Delete(path); }
    }
}
