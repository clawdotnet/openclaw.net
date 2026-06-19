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
    public void WsClientEnvelope_RoundTripsA2UiV09Fields()
    {
        var envelope = new WsClientEnvelope
        {
            Type = "a2ui_operation",
            ProtocolVersion = "0.9",
            Operation = "invoke",
            CatalogId = "catalog-main",
            SupportedCatalogIds = ["catalog-main", "catalog-fallback"],
            Components = ["{\"type\":\"Text\",\"id\":\"button\"}"],
            DataModelJson = "{\"count\":1}",
            SurfaceTitle = "Main Surface",
            SurfaceKind = "panel",
            ParentSurfaceId = "root",
            Action = "submit",
            ParametersJson = "{\"value\":true}",
            SyncMode = "delta",
            DiagnosticCode = "diag.ok"
        };

        var json = JsonSerializer.Serialize(envelope, CoreJsonContext.Default.WsClientEnvelope);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.WsClientEnvelope);

        Assert.NotNull(roundTripped);
        Assert.Equal("0.9", roundTripped!.ProtocolVersion);
        Assert.Equal("invoke", roundTripped.Operation);
        Assert.Equal("catalog-main", roundTripped.CatalogId);
        Assert.Equal(["catalog-main", "catalog-fallback"], roundTripped.SupportedCatalogIds ?? []);
        Assert.Equal(["{\"type\":\"Text\",\"id\":\"button\"}"], roundTripped.Components ?? []);
        Assert.Equal("{\"count\":1}", roundTripped.DataModelJson);
        Assert.Equal("Main Surface", roundTripped.SurfaceTitle);
        Assert.Equal("panel", roundTripped.SurfaceKind);
        Assert.Equal("root", roundTripped.ParentSurfaceId);
        Assert.Equal("submit", roundTripped.Action);
        Assert.Equal("{\"value\":true}", roundTripped.ParametersJson);
        Assert.Equal("delta", roundTripped.SyncMode);
        Assert.Equal("diag.ok", roundTripped.DiagnosticCode);
    }

    [Fact]
    public void WsServerEnvelope_RoundTripsA2UiV09Fields()
    {
        var envelope = new WsServerEnvelope
        {
            Type = "a2ui_operation_result",
            ProtocolVersion = "0.9",
            Operation = "render",
            CatalogId = "catalog-main",
            SupportedCatalogIds = ["catalog-main", "catalog-fallback"],
            Components = ["{\"type\":\"Card\",\"id\":\"card\"}"],
            DataModelJson = "{\"status\":\"ready\"}",
            SurfaceTitle = "Details",
            SurfaceKind = "modal",
            ParentSurfaceId = "main",
            Action = "refresh",
            ParametersJson = "{\"force\":false}",
            SyncMode = "full",
            DiagnosticCode = "diag.warning"
        };

        var json = JsonSerializer.Serialize(envelope, CoreJsonContext.Default.WsServerEnvelope);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.WsServerEnvelope);

        Assert.NotNull(roundTripped);
        Assert.Equal("0.9", roundTripped!.ProtocolVersion);
        Assert.Equal("render", roundTripped.Operation);
        Assert.Equal("catalog-main", roundTripped.CatalogId);
        Assert.Equal(["catalog-main", "catalog-fallback"], roundTripped.SupportedCatalogIds ?? []);
        Assert.Equal(["{\"type\":\"Card\",\"id\":\"card\"}"], roundTripped.Components ?? []);
        Assert.Equal("{\"status\":\"ready\"}", roundTripped.DataModelJson);
        Assert.Equal("Details", roundTripped.SurfaceTitle);
        Assert.Equal("modal", roundTripped.SurfaceKind);
        Assert.Equal("main", roundTripped.ParentSurfaceId);
        Assert.Equal("refresh", roundTripped.Action);
        Assert.Equal("{\"force\":false}", roundTripped.ParametersJson);
        Assert.Equal("full", roundTripped.SyncMode);
        Assert.Equal("diag.warning", roundTripped.DiagnosticCode);
    }

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
        }, TestContext.Current.CancellationToken);

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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal("sess-restart", received!.SessionId);
        Assert.Equal("hello", received.Text);
    }

    [Fact]
    public async Task HandleConnectionAsync_RoutesCanvasReadyWithoutUserMessage()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"canvas_ready","capabilities":["a2ui.v0_8","snapshot.state"]}""");
        ws.QueueClose();

        WsClientEnvelope? canvasEnvelope = null;
        var messageObserved = false;
        channel.OnCanvasClientEnvelopeReceived += (_, envelope, _) =>
        {
            canvasEnvelope = envelope;
            return ValueTask.CompletedTask;
        };
        channel.OnMessageReceived += (_, _) =>
        {
            messageObserved = true;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.NotNull(canvasEnvelope);
        Assert.Equal("canvas_ready", canvasEnvelope!.Type);
        Assert.Contains("a2ui.v0_8", canvasEnvelope.Capabilities ?? [], StringComparer.Ordinal);
        Assert.False(messageObserved);
    }

    [Fact]
    public async Task HandleConnectionAsync_RoutesCanvasAckAndResultsWithoutUserMessage()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 2048 });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"canvas_ack","requestId":"req1","success":true}""");
        ws.QueueReceiveText("""{"type":"canvas_snapshot_result","requestId":"req2","snapshotJson":"{}","success":true}""");
        ws.QueueReceiveText("""{"type":"canvas_eval_result","requestId":"req3","valueJson":"1","success":true}""");
        ws.QueueReceiveText("""{"type":"a2ui_error","sessionId":"sess","surfaceId":"main","error":"failed"}""");
        ws.QueueReceiveText("""{"type":"a2ui_sync_result","sessionId":"sess","surfaceId":"main","success":true}""");
        ws.QueueClose();

        var canvasTypes = new List<string>();
        var messageObserved = false;
        channel.OnCanvasClientEnvelopeReceived += (_, envelope, _) =>
        {
            canvasTypes.Add(envelope.Type);
            return ValueTask.CompletedTask;
        };
        channel.OnMessageReceived += (_, _) =>
        {
            messageObserved = true;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.Equal(["canvas_ack", "canvas_snapshot_result", "canvas_eval_result", "a2ui_error", "a2ui_sync_result"], canvasTypes);
        Assert.False(messageObserved);
    }

    [Fact]
    public async Task HandleConnectionAsync_A2UiEventDoesNotUseActionAsEventFallback()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"a2ui_event","sessionId":"sess","surfaceId":"main","componentId":"btn","action":"click","valueJson":"true","sequence":7}""");
        ws.QueueClose();

        InboundMessage? received = null;
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Null(received!.Event);
        Assert.Contains("\"event\": \"\"", received.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleConnectionAsync_ConvertsA2UiEventToSessionMessage()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"a2ui_event","sessionId":"sess","surfaceId":"main","componentId":"btn","event":"click","valueJson":"true","sequence":7}""");
        ws.QueueClose();

        WsClientEnvelope? canvasEnvelope = null;
        InboundMessage? received = null;
        channel.OnCanvasClientEnvelopeReceived += (_, envelope, _) =>
        {
            canvasEnvelope = envelope;
            return ValueTask.CompletedTask;
        };
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.NotNull(canvasEnvelope);
        Assert.NotNull(received);
        Assert.Equal("a2ui_event", received!.Type);
        Assert.Equal("sess", received.SessionId);
        Assert.Equal("btn", received.ComponentId);
        Assert.Equal("click", received.Event);
        Assert.Equal("true", received.ValueJson);
        Assert.Equal(7, received.Sequence);
        Assert.Contains("\"componentId\": \"btn\"", received.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleConnectionAsync_ConvertsA2UiActionToSessionMessage()
    {
        var channel = new WebSocketChannel(new WebSocketConfig { MaxMessageBytes = 2048 });
        var ws = new TestWebSocket();

        ws.QueueReceiveText("""{"type":"a2ui_action","sessionId":"sess","surfaceId":"main","componentId":"submit-button","action":"submit","valueJson":"{\"accepted\":true}","dataModelJson":"{\"name\":\"Ada\"}","sequence":42}""");
        ws.QueueClose();

        WsClientEnvelope? canvasEnvelope = null;
        InboundMessage? received = null;
        channel.OnCanvasClientEnvelopeReceived += (_, envelope, _) =>
        {
            canvasEnvelope = envelope;
            return ValueTask.CompletedTask;
        };
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.NotNull(canvasEnvelope);
        Assert.NotNull(received);
        Assert.Equal("a2ui_action", received!.Type);
        Assert.Equal("sess", received.SessionId);
        Assert.Equal("main", received.SurfaceId);
        Assert.Equal("submit-button", received.ComponentId);
        Assert.Equal("submit", received.Event);
        Assert.Equal("{\"accepted\":true}", received.ValueJson);
        Assert.Equal(42, received.Sequence);
        Assert.Contains("\"surfaceId\": \"main\"", received.Text, StringComparison.Ordinal);
        Assert.Contains("\"componentId\": \"submit-button\"", received.Text, StringComparison.Ordinal);
        Assert.Contains("\"action\": \"submit\"", received.Text, StringComparison.Ordinal);
        Assert.Contains("\"value\": {\"accepted\":true}", received.Text, StringComparison.Ordinal);
        Assert.Contains("\"dataModel\": {\"name\":\"Ada\"}", received.Text, StringComparison.Ordinal);
        Assert.Contains("\"sequence\": 42", received.Text, StringComparison.Ordinal);
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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

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

        await channel.HandleConnectionAsync(ws, "client", IPAddress.Loopback, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Closed, ws.State);
    }

    [Fact]
    public async Task SendAsync_RemoveConnectionWhileSecondSendWaits_DoesNotThrow()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        ws.BlockSendUntilReleased();

        Assert.True(channel.TryAddConnectionForTest("a", ws, IPAddress.Loopback, useJsonEnvelope: false));

        var firstSend = channel.SendAsync(new OutboundMessage
        {
            ChannelId = "websocket",
            RecipientId = "a",
            Text = "first"
        }, TestContext.Current.CancellationToken).AsTask();

        await ws.WaitForSendToStartAsync();

        var secondSend = channel.SendAsync(new OutboundMessage
        {
            ChannelId = "websocket",
            RecipientId = "a",
            Text = "second"
        }, TestContext.Current.CancellationToken).AsTask();

        channel.RemoveConnectionForTest("a");
        ws.ReleaseBlockedSend();

        await Task.WhenAll(firstSend, secondSend);
    }
}
