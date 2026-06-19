using System.Text.Json;
using OpenClaw.Client;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OpenClawLiveClientTests
{
    [Fact]
    public async Task SendTextAsync_DisconnectWaitsForInFlightSend()
    {
        var client = new OpenClawLiveClient();
        var ws = new TestWebSocket();
        ws.BlockSendUntilReleased();
        client.SetConnectedSocketForTest(ws);

        var sendTask = client.SendTextAsync("hello", turnComplete: true, TestContext.Current.CancellationToken);
        await ws.WaitForSendToStartAsync();

        var disconnectTask = client.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.False(disconnectTask.IsCompleted);

        ws.ReleaseBlockedSend();

        await Task.WhenAll(sendTask, disconnectTask);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ReceiveLoop_RaisesEnvelopeAndTextChunk()
    {
        var client = new OpenClawLiveClient();
        var ws = new TestWebSocket();
        ws.QueueReceiveText(JsonSerializer.Serialize(new LiveServerEnvelope
        {
            Type = "text",
            Text = "chunk-one"
        }, CoreJsonContext.Default.LiveServerEnvelope));
        ws.QueueReceiveText(JsonSerializer.Serialize(new LiveServerEnvelope
        {
            Type = "turn_complete",
            TurnComplete = true
        }, CoreJsonContext.Default.LiveServerEnvelope));
        ws.QueueClose();

        var chunks = new List<string>();
        var types = new List<string>();
        client.OnTextChunk += chunks.Add;
        client.OnEnvelopeReceived += envelope => types.Add(envelope.Type);

        await client.RunReceiveLoopForTest(ws, TestContext.Current.CancellationToken);

        Assert.Equal(["chunk-one"], chunks);
        Assert.Equal(["text", "turn_complete"], types);
    }
}
