using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HybridChannelDeliveryTests
{
    [Fact]
    public async Task SendAndWaitAsync_WhenChannelSocketFails_ShouldNotReplayOverStdio()
    {
        var bootstrap = Substitute.For<IBridgeTransport>();
        var socket = Substitute.For<IBridgeTransport>();
        socket.SendAndWaitAsync("channel_send", Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BridgeResponse>(new IOException("response lost after send")));
        await using var transport = new HybridBridgeTransport(bootstrap, socket, NullLogger.Instance);
        transport.UseSocketTransport();
        await Assert.ThrowsAsync<IOException>(() => transport.SendAndWaitAsync("channel_send", null, TestContext.Current.CancellationToken));
        await bootstrap.DidNotReceive().SendAndWaitAsync(Arg.Any<string>(), Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());

        // Changing transport is allowed for a later operation, not replaying this send.
        bootstrap.SendAndWaitAsync("status", null, Arg.Any<CancellationToken>()).Returns(new BridgeResponse { Id = "status" });
        Assert.Equal("status", (await transport.SendAndWaitAsync("status", null, TestContext.Current.CancellationToken)).Id);
    }

    [Fact]
    public async Task SendAndWaitAsync_WhenStatusSocketFails_ShouldPreserveExistingFallback()
    {
        var bootstrap = Substitute.For<IBridgeTransport>();
        var socket = Substitute.For<IBridgeTransport>();
        socket.SendAndWaitAsync("status", Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BridgeResponse>(new IOException("socket closed")));
        bootstrap.SendAndWaitAsync("status", null, Arg.Any<CancellationToken>()).Returns(new BridgeResponse { Id = "status" });
        await using var transport = new HybridBridgeTransport(bootstrap, socket, NullLogger.Instance);
        transport.UseSocketTransport();
        Assert.Equal("status", (await transport.SendAndWaitAsync("status", null, TestContext.Current.CancellationToken)).Id);
    }
}
