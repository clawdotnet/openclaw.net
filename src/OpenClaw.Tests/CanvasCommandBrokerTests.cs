using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CanvasCommandBrokerTests
{
    [Fact]
    public async Task SendCommandAsync_SendsToEnvelopeClientAndCompletesOnAck()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), CancellationToken.None);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            CancellationToken.None);

        var sent = await WaitForSentEnvelopeAsync(ws);
        Assert.Equal("canvas_present", sent.Type);
        Assert.Equal("sess", sent.SessionId);

        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = "sess",
            Success = true
        }, CancellationToken.None);

        var result = await task;

        Assert.True(result.Success);
        Assert.Equal(sent.RequestId, result.RequestId);
    }

    [Fact]
    public async Task SendCommandAsync_TimesOutWhenClientDoesNotRespond()
    {
        var config = new GatewayConfig { Canvas = new CanvasConfig { CommandTimeoutSeconds = 1 } };
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel, config);
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), CancellationToken.None);

        var result = await broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsDisconnectedClient()
    {
        var broker = CreateBroker(new WebSocketChannel(new WebSocketConfig()));

        var result = await broker.SendCommandAsync(
            Session("sess", "missing"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not connected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsMissingCapability()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        Assert.True(channel.TryAddConnectionForTest("client", new TestWebSocket(), IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);

        var result = await broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not advertised", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsWrongSessionResult()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), CancellationToken.None);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            CancellationToken.None);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = "other",
            Success = true
        }, CancellationToken.None);

        var result = await task;

        Assert.False(result.Success);
        Assert.Contains("session id", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsOversizedSnapshot()
    {
        var config = new GatewayConfig
        {
            Canvas = new CanvasConfig
            {
                MaxSnapshotBytes = 8
            }
        };
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel, config);
        await broker.HandleClientEnvelopeAsync("client", Ready("snapshot.state"), CancellationToken.None);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_snapshot" },
            "canvas_snapshot_result",
            "snapshot.state",
            CancellationToken.None);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_snapshot_result",
            RequestId = sent.RequestId,
            SessionId = "sess",
            Success = true,
            SnapshotJson = """{"text":"oversized"}"""
        }, CancellationToken.None);

        var result = await task;

        Assert.False(result.Success);
        Assert.Contains("snapshot exceeds", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static CanvasCommandBroker CreateBroker(WebSocketChannel channel, GatewayConfig? config = null)
        => new(
            config ?? new GatewayConfig(),
            channel,
            new RuntimeEventStore(
                Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                NullLogger<RuntimeEventStore>.Instance));

    private static WsClientEnvelope Ready(params string[] capabilities)
        => new()
        {
            Type = "canvas_ready",
            Capabilities = capabilities
        };

    private static Session Session(string id, string senderId)
        => new()
        {
            Id = id,
            ChannelId = "websocket",
            SenderId = senderId
        };

    private static async Task<WsServerEnvelope> WaitForSentEnvelopeAsync(TestWebSocket ws)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (ws.Sent.Count > 0)
            {
                var payload = Encoding.UTF8.GetString(ws.Sent.Last());
                return JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsServerEnvelope)
                    ?? throw new InvalidOperationException("Sent payload was not a websocket envelope.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for websocket send.");
    }
}
