using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WebSocketChannelTests
{
    [Fact]
    public async Task SendAsync_RoutesOnlyToRecipient()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());

        var ws1 = new TestWebSocket();
        var ws2 = new TestWebSocket();

        Assert.True(channel.TryAddConnectionForTest("a", ws1, IPAddress.Loopback, useJsonEnvelope: false));
        Assert.True(channel.TryAddConnectionForTest("b", ws2, IPAddress.Loopback, useJsonEnvelope: false));

        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "websocket",
            RecipientId = "a",
            Text = "hello"
        }, CancellationToken.None);

        Assert.Single(ws1.Sent);
        Assert.Empty(ws2.Sent);
    }

    [Fact]
    public async Task HandleConnectionAsync_ReassemblesFragmentedMessage()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        ws.QueueReceiveBytes(System.Text.Encoding.UTF8.GetBytes("hel"), endOfMessage: false);
        ws.QueueReceiveBytes(System.Text.Encoding.UTF8.GetBytes("lo"), endOfMessage: true);
        ws.QueueClose();

        InboundMessage? received = null;
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("hello", received!.Text);
    }

    [Fact]
    public async Task HandleConnectionAsync_AcceptsLegacyContentEnvelope()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        var payload = """{"type":"user_message","content":"legacy"}""";
        ws.QueueReceiveBytes(System.Text.Encoding.UTF8.GetBytes(payload), endOfMessage: true);
        ws.QueueClose();

        InboundMessage? received = null;
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("legacy", received!.Text);
    }

    [Fact]
    public async Task HandleConnectionAsync_PreservesEnvelopeSessionId()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        var payload = """{"type":"user_message","text":"hello","sessionId":"sess-restart"}""";
        ws.QueueReceiveBytes(System.Text.Encoding.UTF8.GetBytes(payload), endOfMessage: true);
        ws.QueueClose();

        InboundMessage? received = null;
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("sess-restart", received!.SessionId);
        Assert.Equal("hello", received.Text);
    }

    [Fact]
    public async Task HandleConnectionAsync_IgnoresPrematureRemoteCloseDuringReceive()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        ws.QueueReceiveException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        var observed = false;
        channel.OnMessageReceived += (_, _) =>
        {
            observed = true;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.False(observed);
    }

    [Fact]
    public async Task SendAsync_UsesJsonEnvelopeWhenClientOptedIn()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();

        Assert.True(channel.TryAddConnectionForTest("a", ws, IPAddress.Loopback, useJsonEnvelope: true));

        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "websocket",
            RecipientId = "a",
            Text = "hello",
            ReplyToMessageId = "m1"
        }, CancellationToken.None);

        var payload = System.Text.Encoding.UTF8.GetString(ws.Sent.Single());
        var env = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsServerEnvelope);
        Assert.Equal("assistant_message", env!.Type);
        Assert.Equal("hello", env.Text);
        Assert.Equal("m1", env.InReplyToMessageId);
    }

    [Fact]
    public async Task HandleConnectionAsync_SendsStructuredErrorBeforeClosingWhenRateLimited()
    {
        var channel = new WebSocketChannel(new WebSocketConfig
        {
            MaxMessageBytes = 1024,
            MessagesPerMinutePerConnection = 1
        });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"user_message","text":"first"}""");
        ws.QueueReceiveText("""{"type":"user_message","text":"second"}""");

        var receivedCount = 0;
        channel.OnMessageReceived += (_, _) =>
        {
            receivedCount++;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(1, receivedCount);
        Assert.NotEmpty(ws.Sent);

        var payload = System.Text.Encoding.UTF8.GetString(ws.Sent.Last());
        var env = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsServerEnvelope);
        Assert.Equal("error", env!.Type);
        Assert.Equal("Rate limit exceeded", env.Text);
    }

    [Fact]
    public async Task HandleConnectionAsync_ClosesOnReceiveTimeout()
    {
        var channel = new WebSocketChannel(new WebSocketConfig
        {
            MaxMessageBytes = 1024,
            ReceiveTimeoutSeconds = 1
        });
        var ws = new TestWebSocket();
        ws.BlockReceiveUntilCancelled();

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(WebSocketState.Closed, ws.State);
    }
}
