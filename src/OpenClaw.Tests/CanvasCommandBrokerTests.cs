using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Canvas;
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
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        Assert.Equal("canvas_present", sent.Type);
        Assert.Equal("sess", sent.SessionId);

        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = "sess",
            Success = true
        }, TestContext.Current.CancellationToken);

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
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), TestContext.Current.CancellationToken);

        var result = await broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
        await broker.HandleClientEnvelopeAsync("client", Ready("canvas.present"), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_present" },
            "canvas_ack",
            "canvas.present",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = "other",
            Success = true
        }, TestContext.Current.CancellationToken);

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
        await broker.HandleClientEnvelopeAsync("client", Ready("snapshot.state"), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "canvas_snapshot" },
            "canvas_snapshot_result",
            "snapshot.state",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_snapshot_result",
            RequestId = sent.RequestId,
            SessionId = "sess",
            Success = true,
            SnapshotJson = """{"text":"oversized"}"""
        }, TestContext.Current.CancellationToken);

        var result = await task;

        Assert.False(result.Success);
        Assert.Contains("snapshot exceeds", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleClientEnvelopeAsync_StoresSupportedCatalogIdsFromReadyAndLaterEnvelopes()
    {
        var broker = CreateBroker(new WebSocketChannel(new WebSocketConfig()));

        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
        [
            " canvas.present ",
            "CANVAS.PRESENT"
        ],
        [
            " urn:a2ui:catalog:agenui_catalog ",
            "URN:A2UI:CATALOG:AGenUI_CATALOG",
            "urn:a2ui:catalog:openclaw_v0_8"
        ]), TestContext.Current.CancellationToken);

        Assert.Equal(["canvas.present"], broker.GetClientCapabilities("client"));
        Assert.Equal([
            A2UiCatalogRegistry.AGenUiCatalogId,
            A2UiCatalogRegistry.OpenClawV08CatalogId
        ], broker.GetClientSupportedCatalogIds("client"));

        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ack",
            SupportedCatalogIds = ["urn:a2ui:catalog:openclaw_v0_8"]
        }, TestContext.Current.CancellationToken);

        Assert.Equal([A2UiCatalogRegistry.OpenClawV08CatalogId], broker.GetClientSupportedCatalogIds("client"));
    }

    [Fact]
    public async Task TryChooseCatalog_ChoosesCompatibleCatalogAndRejectsUnsupportedRequestedCatalog()
    {
        var broker = CreateBroker(new WebSocketChannel(new WebSocketConfig()));
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId]), TestContext.Current.CancellationToken);

        Assert.True(broker.TryChooseCatalog("client", null, out var chosen, out var error));
        Assert.Equal(A2UiCatalogRegistry.OpenClawV08CatalogId, chosen?.CatalogId);
        Assert.Null(error);

        Assert.False(broker.TryChooseCatalog("client", A2UiCatalogRegistry.AGenUiCatalogId, out chosen, out error));
        Assert.Null(chosen);
        Assert.Contains("does not support catalog", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryChooseCatalog_PrefersAGenUiWhenBothFirstPartyCatalogsAreSupported()
    {
        var broker = CreateBroker(new WebSocketChannel(new WebSocketConfig()));
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);

        Assert.True(broker.TryChooseCatalog("client", null, out var chosen, out var error));
        Assert.Equal(A2UiCatalogRegistry.AGenUiCatalogId, chosen?.CatalogId);
        Assert.Null(error);
    }

    [Fact]
    public async Task SendCommandAsync_LegacyA2UiPushSendsWithoutCatalogNegotiation()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", Ready("a2ui.v0_8"), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "a2ui_push" },
            "canvas_ack",
            "a2ui.v0_8",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await task;

        Assert.True(result.Success);
        Assert.Equal("a2ui_push", sent.Type);
        Assert.Empty(broker.GetClientSupportedCatalogIds("client"));
    }

    [Fact]
    public async Task SendCommandAsync_LegacyA2UiPushSendsWhenRequestedCatalogIsUnsupported()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["a2ui.v0_8"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId]), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_push",
                CatalogId = A2UiCatalogRegistry.AGenUiCatalogId
            },
            "canvas_ack",
            "a2ui.v0_8",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await task;

        Assert.True(result.Success);
        Assert.Equal("a2ui_push", sent.Type);
        Assert.Equal(A2UiCatalogRegistry.AGenUiCatalogId, sent.CatalogId);
    }

    [Fact]
    public async Task SendCommandAsync_SuccessfulCreateSurfaceLocksCatalogForSurface()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId]), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_create_surface",
                SurfaceId = "surface-1",
                CatalogId = A2UiCatalogRegistry.OpenClawV08CatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await task;

        Assert.True(result.Success);
        Assert.True(broker.TryGetSurfaceCatalogId("client", "sess", "surface-1", out var catalogId));
        Assert.Equal(A2UiCatalogRegistry.OpenClawV08CatalogId, catalogId);
    }

    [Fact]
    public async Task SendCommandAsync_SuccessfulUpdateComponentsLocksCatalogForSurface()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_update_components",
                SurfaceId = "surface-1",
                CatalogId = A2UiCatalogRegistry.OpenClawV08CatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await task;

        Assert.True(result.Success);
        Assert.True(broker.TryGetSurfaceCatalogId("client", "sess", "surface-1", out var catalogId));
        Assert.Equal(A2UiCatalogRegistry.OpenClawV08CatalogId, catalogId);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsConflictingCatalogUpdateBeforeSending()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);
        await LockSurfaceCatalogThroughCreateAsync(broker, ws, "client", "sess", "surface-1", A2UiCatalogRegistry.OpenClawV08CatalogId);

        var result = await broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_update_components",
                SurfaceId = "surface-1",
                CatalogId = A2UiCatalogRegistry.AGenUiCatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("catalog", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(ws.Sent);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsConflictingCatalogUpdateWithMixedCaseSurfaceIdBeforeSending()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);
        await LockSurfaceCatalogThroughCreateAsync(broker, ws, "client", "sess", "Main", A2UiCatalogRegistry.OpenClawV08CatalogId);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_update_components",
                SurfaceId = "main",
                CatalogId = A2UiCatalogRegistry.AGenUiCatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        if (await Task.WhenAny(task, Task.Delay(200)) != task)
        {
            var sent = await WaitForSentEnvelopeAsync(ws, skip: 1);
            await AckAsync(broker, "client", sent, "sess");
        }

        var result = await task;
        Assert.False(result.Success);
        Assert.Contains("catalog", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(ws.Sent);
    }

    [Fact]
    public async Task SendCommandAsync_RejectsConflictingSyncCatalogBeforeSending()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);
        await LockSurfaceCatalogThroughCreateAsync(broker, ws, "client", "sess", "surface-1", A2UiCatalogRegistry.OpenClawV08CatalogId);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_sync_ui_to_data",
                SurfaceId = "surface-1",
                CatalogId = A2UiCatalogRegistry.AGenUiCatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        if (await Task.WhenAny(task, Task.Delay(200)) != task)
        {
            var sent = await WaitForSentEnvelopeAsync(ws, skip: 1);
            await AckAsync(broker, "client", sent, "sess");
        }

        var result = await task;
        Assert.False(result.Success);
        Assert.Contains("catalog", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(ws.Sent);
    }

    [Fact]
    public async Task SendCommandAsync_AllowsLockedCatalogUpdateWithoutCatalogId()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId]), TestContext.Current.CancellationToken);
        await LockSurfaceCatalogThroughCreateAsync(broker, ws, "client", "sess", "surface-1", A2UiCatalogRegistry.OpenClawV08CatalogId);

        var task = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_update_components",
                SurfaceId = "surface-1"
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);

        var sent = await WaitForSentEnvelopeAsync(ws, skip: 1);
        await AckAsync(broker, "client", sent, "sess");
        var result = await task;

        Assert.True(result.Success);
        Assert.Null(sent.CatalogId);
    }

    [Fact]
    public async Task SendCommandAsync_SuccessfulDeleteSurfaceClearsCatalogLockAndAllowsNewCatalog()
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = CreateBroker(channel);
        await broker.HandleClientEnvelopeAsync("client", ReadyWithCatalogs(
            ["canvas.a2ui"],
            [A2UiCatalogRegistry.OpenClawV08CatalogId, A2UiCatalogRegistry.AGenUiCatalogId]), TestContext.Current.CancellationToken);
        await LockSurfaceCatalogThroughCreateAsync(broker, ws, "client", "sess", "surface-1", A2UiCatalogRegistry.OpenClawV08CatalogId);

        var deleteTask = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope { Type = "a2ui_delete_surface", SurfaceId = "surface-1" },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);
        var deleteSent = await WaitForSentEnvelopeAsync(ws, skip: 1);
        await AckAsync(broker, "client", deleteSent, "sess");
        Assert.True((await deleteTask).Success);
        Assert.False(broker.TryGetSurfaceCatalogId("client", "sess", "surface-1", out _));

        var createTask = broker.SendCommandAsync(
            Session("sess", "client"),
            new WsServerEnvelope
            {
                Type = "a2ui_create_surface",
                SurfaceId = "surface-1",
                CatalogId = A2UiCatalogRegistry.AGenUiCatalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);
        var createSent = await WaitForSentEnvelopeAsync(ws, skip: 2);
        await AckAsync(broker, "client", createSent, "sess");
        Assert.True((await createTask).Success);
        Assert.True(broker.TryGetSurfaceCatalogId("client", "sess", "surface-1", out var catalogId));
        Assert.Equal(A2UiCatalogRegistry.AGenUiCatalogId, catalogId);
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

    private static WsClientEnvelope ReadyWithCatalogs(string[] capabilities, string[] supportedCatalogIds)
        => new()
        {
            Type = "canvas_ready",
            Capabilities = capabilities,
            SupportedCatalogIds = supportedCatalogIds
        };

    private static async Task LockSurfaceCatalogThroughCreateAsync(
        CanvasCommandBroker broker,
        TestWebSocket ws,
        string clientId,
        string sessionId,
        string surfaceId,
        string catalogId)
    {
        var createTask = broker.SendCommandAsync(
            Session(sessionId, clientId),
            new WsServerEnvelope
            {
                Type = "a2ui_create_surface",
                SurfaceId = surfaceId,
                CatalogId = catalogId
            },
            "canvas_ack",
            "canvas.a2ui",
            TestContext.Current.CancellationToken);
        var createSent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, clientId, createSent, sessionId);
        Assert.True((await createTask).Success);
    }

    private static async Task AckAsync(CanvasCommandBroker broker, string clientId, WsServerEnvelope sent, string sessionId)
        => await broker.HandleClientEnvelopeAsync(clientId, new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = sessionId,
            Success = true
        }, TestContext.Current.CancellationToken);

    private static Session Session(string id, string senderId)
        => new()
        {
            Id = id,
            ChannelId = "websocket",
            SenderId = senderId
        };

    private static async Task<WsServerEnvelope> WaitForSentEnvelopeAsync(TestWebSocket ws, int skip = 0)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (ws.Sent.Count > skip)
            {
                var payload = Encoding.UTF8.GetString(ws.Sent.Skip(skip).Last());
                return JsonSerializer.Deserialize(payload, CoreJsonContext.Default.WsServerEnvelope)
                    ?? throw new InvalidOperationException("Sent payload was not a websocket envelope.");
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for websocket send.");
    }
}
