using System.Net.WebSockets;
using OpenClaw.Client;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OpenClawWebSocketClientTests
{
    [Fact]
    public async Task SendUserMessageAsync_DisconnectWaitsForInFlightSend()
    {
        var client = new OpenClawWebSocketClient();
        var ws = new TestWebSocket();
        ws.BlockSendUntilReleased();
        client.SetConnectedSocketForTest(ws);

        var sendTask = client.SendUserMessageAsync("hello", "m1", replyToMessageId: null, TestContext.Current.CancellationToken);
        await ws.WaitForSendToStartAsync();

        var disconnectTask = client.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.False(disconnectTask.IsCompleted);

        ws.ReleaseBlockedSend();

        await Task.WhenAll(sendTask, disconnectTask);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ReceiveLoop_CallbackException_RaisesOnErrorAndContinues()
    {
        var client = new OpenClawWebSocketClient();
        var ws = new TestWebSocket();
        ws.QueueReceiveText("first");
        ws.QueueReceiveText("second");
        ws.QueueClose();

        var received = new List<string>();
        string? error = null;
        var throwFirst = true;

        client.OnTextMessage += text =>
        {
            received.Add(text);
            if (throwFirst)
            {
                throwFirst = false;
                throw new InvalidOperationException("boom");
            }
        };
        client.OnError += message => error = message;

        await client.RunReceiveLoopForTest(ws, TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], received);
        Assert.Equal("boom", error);
    }

    [Fact]
    public async Task ReceiveLoop_WhenServerCloses_ShouldRaiseOnClosedWithStatusAndReason()
    {
        var client = new OpenClawWebSocketClient();
        var ws = new TestWebSocket();
        ws.QueueClose(WebSocketCloseStatus.PolicyViolation, "This action requires the operator role.");
        (WebSocketCloseStatus? Status, string? Reason)? closed = null;
        client.OnClosed += (status, reason) => closed = (status, reason);

        await client.RunReceiveLoopForTest(ws, TestContext.Current.CancellationToken);

        Assert.Equal((WebSocketCloseStatus.PolicyViolation, "This action requires the operator role."), closed);
    }

    [Fact]
    public async Task ReceiveLoop_WhenCancelledByClient_ShouldNotRaiseOnClosed()
    {
        var client = new OpenClawWebSocketClient();
        var ws = new TestWebSocket();
        ws.BlockReceiveUntilCancelled();
        var raised = false;
        client.OnClosed += (_, _) => raised = true;
        using var cts = new CancellationTokenSource();

        var loop = client.RunReceiveLoopForTest(ws, cts.Token);
        await cts.CancelAsync();
        await loop;

        Assert.False(raised);
    }
}
