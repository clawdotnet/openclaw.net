using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Canvas;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CanvasToolTests
{
    [Fact]
    public async Task CanvasPresent_RejectsNonWebSocketSession()
    {
        var tool = new CanvasPresentTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("{}", Context(channelId: "cli"), CancellationToken.None);

        Assert.Contains("websocket session", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    public async Task CanvasNavigate_RejectsRemoteWebpageUrls(string url)
    {
        var tool = new CanvasNavigateTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync($$"""{"url":"{{url}}"}""", Context(), CancellationToken.None);

        Assert.Contains("only supports about:blank and openclaw-canvas://", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UiPush_RejectsCreateSurfaceV09()
    {
        var tool = new A2UiPushTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"frames":"{\"type\":\"createSurface\",\"id\":\"main\"}"}""",
            Context(),
            CancellationToken.None);

        Assert.Contains("createSurface", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A2UiPush_MissingFramesReturnsToolError()
    {
        var tool = new A2UiPushTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("{}", Context(), CancellationToken.None);

        Assert.Contains("'frames' is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A2UiPush_UsesEnvelopeReserveForPayloadLimit()
    {
        var config = new GatewayConfig
        {
            Canvas = new CanvasConfig
            {
                MaxCommandBytes = 4_200
            }
        };
        var tool = new A2UiPushTool(CreateBroker(config: config), config);
        var frames = $$"""{"type":"text","id":"large","text":"{{new string('x', 200)}}"}""";

        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new { frames }), Context(), CancellationToken.None);

        Assert.Contains("exceeds 104 bytes", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A2UiEval_RespectsEvalDisableConfig()
    {
        var config = new GatewayConfig
        {
            Canvas = new CanvasConfig
            {
                EnableEval = false
            }
        };
        var tool = new A2UiEvalTool(CreateBroker(config: config), config);

        var result = await tool.ExecuteAsync("""{"script":"return 1"}""", Context(), CancellationToken.None);

        Assert.Contains("eval is disabled", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UiCreateSurface_MinimalSurfaceIdSendsV09Envelope()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());

        var executeTask = tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1" }),
            Context(senderId: "client"),
            CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await executeTask;

        Assert.Contains("Canvas command accepted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_create_surface", sent.Type);
        Assert.Equal("createSurface", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
        Assert.Equal(A2UiCatalogRegistry.AGenUiCatalogId, sent.CatalogId);
        Assert.Null(sent.SurfaceTitle);
        Assert.Null(sent.ParametersJson);
        Assert.Null(sent.Components);
        Assert.Null(sent.DataModelJson);
    }

    [Fact]
    public async Task A2UiCreateSurface_OmittedCatalogRejectsUnsupportedComponentBeforeSend()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.OpenClawV08CatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());
        var component = """{"type":"Icon","name":"home"}""";

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", components = new[] { component } }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("unsupported component type 'Icon'", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task A2UiCreateSurface_RejectsMalformedOptionalComponentString()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", components = new[] { "not-json" } }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("not valid JSON", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task A2UiCreateSurface_RejectsNonStringComponentArray()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"surfaceId":"surface-1","components":[123]}""",
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("must be a JSON string array", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    public async Task A2UiCreateSurface_RejectsInvalidOrNonObjectOptionalDataModelJson(string dataModelJson, string expectedError)
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", dataModelJson }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains(expectedError, result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task A2UiCreateSurface_SendsV09EnvelopeWithOptionalCatalogTitleMetadataComponentsAndDataModel()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());
        var metadata = """{"theme":"dark"}""";
        var dataModelJson = """{"count":1}""";
        var component = """{"type":"Text","text":"Hello"}""";
        var argumentsJson = JsonSerializer.Serialize(new
        {
            surfaceId = "surface-1",
            catalogId = A2UiCatalogRegistry.AGenUiCatalogId,
            title = "Dashboard",
            metadata,
            components = new[] { component },
            dataModelJson
        });

        var executeTask = tool.ExecuteAsync(argumentsJson, Context(senderId: "client"), CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await executeTask;

        Assert.Contains("Canvas command accepted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_create_surface", sent.Type);
        Assert.Equal("createSurface", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
        Assert.Equal(A2UiCatalogRegistry.AGenUiCatalogId, sent.CatalogId);
        Assert.Equal("Dashboard", sent.SurfaceTitle);
        Assert.Equal(metadata, sent.ParametersJson);
        Assert.NotNull(sent.Components);
        Assert.Equal([component], sent.Components);
        Assert.Equal(dataModelJson, sent.DataModelJson);
    }

    [Fact]
    public async Task A2UiV09Tools_MissingRequiredFieldsReturnToolErrors()
    {
        var broker = CreateBroker();
        var config = new GatewayConfig();
        var context = Context(senderId: "client");

        Assert.Contains("'surfaceId' is required", await new A2UiCreateSurfaceTool(broker, config).ExecuteAsync("{}", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'surfaceId' is required", await new A2UiUpdateComponentsTool(broker, config).ExecuteAsync("{}", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'components' is required", await new A2UiUpdateComponentsTool(broker, config).ExecuteAsync("""{"surfaceId":"surface-1"}""", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'surfaceId' is required", await new A2UiUpdateDataModelTool(broker, config).ExecuteAsync("{}", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'dataModelJson' is required", await new A2UiUpdateDataModelTool(broker, config).ExecuteAsync("""{"surfaceId":"surface-1"}""", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'surfaceId' is required", await new A2UiDeleteSurfaceTool(broker, config).ExecuteAsync("{}", context, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("'surfaceId' is required", await new A2UiSyncUiToDataTool(broker, config).ExecuteAsync("{}", context, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A2UiCreateSurface_MissingV09CapabilityReturnsError()
    {
        var (broker, _) = CreateConnectedBroker(["a2ui.v0_8"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiCreateSurfaceTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new
            {
                surfaceId = "surface-1",
                catalogId = A2UiCatalogRegistry.AGenUiCatalogId,
                title = "Dashboard",
                metadata = "{}"
            }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("a2ui.v0_9", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UiV09UpdateDeleteAndSyncTools_MissingV09CapabilityReturnsError()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_8"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var config = new GatewayConfig();
        var context = Context(senderId: "client");
        var component = """{"type":"Text","text":"Hello"}""";

        Assert.Contains("a2ui.v0_9", await new A2UiUpdateComponentsTool(broker, config).ExecuteAsync(JsonSerializer.Serialize(new { surfaceId = "surface-1", components = new[] { component } }), context, CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a2ui.v0_9", await new A2UiUpdateDataModelTool(broker, config).ExecuteAsync(JsonSerializer.Serialize(new { surfaceId = "surface-1", dataModelJson = "{}" }), context, CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a2ui.v0_9", await new A2UiDeleteSurfaceTool(broker, config).ExecuteAsync("""{"surfaceId":"surface-1"}""", context, CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a2ui.v0_9", await new A2UiSyncUiToDataTool(broker, config).ExecuteAsync("""{"surfaceId":"surface-1"}""", context, CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Fact]
    public async Task A2UiUpdateComponents_RejectsInvalidComponentJsonAndAcceptsValidStringArray()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiUpdateComponentsTool(broker, new GatewayConfig());

        var invalidResult = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", components = new[] { "not-json" } }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("not valid JSON", invalidResult, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);

        var component = """{"type":"Text","text":"Hello"}""";
        var executeTask = tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", components = new[] { component } }),
            Context(senderId: "client"),
            CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await executeTask;

        Assert.Contains("Canvas command accepted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_update_components", sent.Type);
        Assert.Equal("updateComponents", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
        Assert.NotNull(sent.Components);
        Assert.Equal([component], sent.Components);
    }

    [Fact]
    public async Task A2UiUpdateComponents_RejectsNonStringComponentArray()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiUpdateComponentsTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"surfaceId":"surface-1","components":[true]}""",
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains("must be a JSON string array", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.Sent);
    }

    [Theory]
    [InlineData("not-json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    public async Task A2UiUpdateDataModel_RejectsInvalidOrNonObjectJson(string dataModelJson, string expectedError)
    {
        var (broker, _) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiUpdateDataModelTool(broker, new GatewayConfig());

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", dataModelJson }),
            Context(senderId: "client"),
            CancellationToken.None);

        Assert.Contains(expectedError, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UiUpdateDataModel_SendsV09Envelope()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiUpdateDataModelTool(broker, new GatewayConfig());
        var dataModelJson = """{"count":2}""";

        var executeTask = tool.ExecuteAsync(
            JsonSerializer.Serialize(new { surfaceId = "surface-1", dataModelJson }),
            Context(senderId: "client"),
            CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await executeTask;

        Assert.Contains("Canvas command accepted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_update_data_model", sent.Type);
        Assert.Equal("updateDataModel", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
        Assert.Equal(dataModelJson, sent.DataModelJson);
    }

    [Fact]
    public async Task A2UiDeleteSurface_SendsV09Envelope()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiDeleteSurfaceTool(broker, new GatewayConfig());

        var executeTask = tool.ExecuteAsync("""{"surfaceId":"surface-1"}""", Context(senderId: "client"), CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", sent, "sess");
        var result = await executeTask;

        Assert.Contains("Canvas command accepted", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_delete_surface", sent.Type);
        Assert.Equal("deleteSurface", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
    }

    [Fact]
    public async Task A2UiSyncUiToData_SendsV09EnvelopeWithOptionalFields()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var tool = new A2UiSyncUiToDataTool(broker, new GatewayConfig());

        var executeTask = tool.ExecuteAsync(
            JsonSerializer.Serialize(new
            {
                surfaceId = "surface-1",
                componentId = "component-1",
                syncMode = "merge"
            }),
            Context(senderId: "client"),
            CancellationToken.None);
        var sent = await WaitForSentEnvelopeAsync(ws);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "a2ui_sync_result",
            RequestId = sent.RequestId,
            SessionId = "sess",
            SurfaceId = "surface-1",
            Success = true,
            ValueJson = "{\"value\":\"updated\"}"
        }, CancellationToken.None);
        var result = await executeTask;

        Assert.Equal("{\"value\":\"updated\"}", result);
        Assert.Equal("a2ui_sync_ui_to_data", sent.Type);
        Assert.Equal("syncUIToData", sent.Operation);
        Assert.Equal("surface-1", sent.SurfaceId);
        Assert.Equal("component-1", sent.ComponentId);
        Assert.Equal("merge", sent.SyncMode);
        Assert.Null(sent.DataModelJson);
    }

    [Fact]
    public async Task A2UiV09Lifecycle_WebSocketRoundTrip_CoversCreateUpdateSnapshotSyncAndDelete()
    {
        var (broker, ws) = CreateConnectedBroker(["a2ui.v0_9", "snapshot.state"], [A2UiCatalogRegistry.AGenUiCatalogId]);
        var config = new GatewayConfig();
        var context = Context(senderId: "client");

        var createTool = new A2UiCreateSurfaceTool(broker, config);
        var updateModelTool = new A2UiUpdateDataModelTool(broker, config);
        var updateComponentsTool = new A2UiUpdateComponentsTool(broker, config);
        var snapshotTool = new CanvasSnapshotTool(broker, config);
        var syncTool = new A2UiSyncUiToDataTool(broker, config);
        var deleteTool = new A2UiDeleteSurfaceTool(broker, config);

        var createTask = createTool.ExecuteAsync(
            """{"surfaceId":"details","components":["{\"type\":\"Text\",\"id\":\"title\",\"text\":\"ok\"}"],"dataModelJson":"{\"status\":\"draft\"}"}""",
            context,
            CancellationToken.None);
        var createSent = await WaitForSentEnvelopeAsync(ws);
        await AckAsync(broker, "client", createSent, "sess");
        var createResult = await createTask;

        Assert.Contains("Canvas command accepted", createResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_create_surface", createSent.Type);
        Assert.Equal("createSurface", createSent.Operation);
        Assert.Equal("details", createSent.SurfaceId);

        var snapshotTask1 = snapshotTool.ExecuteAsync("""{"surfaceId":"details"}""", context, CancellationToken.None);
        var snapshotSent1 = await WaitForSentEnvelopeAsync(ws, skip: 1);
        await RespondWithSnapshotAsync(
            broker,
            requestId: snapshotSent1.RequestId!,
            surfaceId: "details",
            snapshotJson: """{"dataModelJson":"{\"status\":\"draft\"}","components":[{"type":"Text","id":"title","text":"ok"}],"frameCount":1,"diagnostics":[]}""");
        var snapshotResult1 = await snapshotTask1;

        CanvasSnapshotAssertions.AssertV09Snapshot(
            snapshotResult1,
            expectedFrameCount: 1,
            expectedComponentCount: 1,
            expectedDataModelFragment: "status",
            expectedDiagnosticContains: null);

        var updateModelTask = updateModelTool.ExecuteAsync(
            """{"surfaceId":"details","dataModelJson":"{\"status\":\"approved\"}"}""",
            context,
            CancellationToken.None);
        var updateModelSent = await WaitForSentEnvelopeAsync(ws, skip: 2);
        await AckAsync(broker, "client", updateModelSent, "sess");
        var updateModelResult = await updateModelTask;

        Assert.Contains("Canvas command accepted", updateModelResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_update_data_model", updateModelSent.Type);

        var updateComponentsTask = updateComponentsTool.ExecuteAsync(
            """{"surfaceId":"details","components":["{\"type\":\"Text\",\"id\":\"title\",\"text\":\"updated\"}"]}""",
            context,
            CancellationToken.None);
        var updateComponentsSent = await WaitForSentEnvelopeAsync(ws, skip: 3);
        await AckAsync(broker, "client", updateComponentsSent, "sess");
        var updateComponentsResult = await updateComponentsTask;

        Assert.Contains("Canvas command accepted", updateComponentsResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_update_components", updateComponentsSent.Type);
        Assert.Equal("updateComponents", updateComponentsSent.Operation);

        var snapshotTask2 = snapshotTool.ExecuteAsync("""{"surfaceId":"details"}""", context, CancellationToken.None);
        var snapshotSent2 = await WaitForSentEnvelopeAsync(ws, skip: 4);
        await RespondWithSnapshotAsync(
            broker,
            requestId: snapshotSent2.RequestId!,
            surfaceId: "details",
            snapshotJson: """{"dataModelJson":"{\"status\":\"approved\"}","components":[{"type":"Text","id":"title","text":"updated"}],"frameCount":1,"diagnostics":["surface validated"]}""");
        var snapshotResult2 = await snapshotTask2;

        CanvasSnapshotAssertions.AssertV09Snapshot(
            snapshotResult2,
            expectedFrameCount: 1,
            expectedComponentCount: 1,
            expectedDataModelFragment: "approved",
            expectedDiagnosticContains: "validated");

        var syncTask = syncTool.ExecuteAsync(
            """{"surfaceId":"details","syncMode":"full"}""",
            context,
            CancellationToken.None);
        var syncSent = await WaitForSentEnvelopeAsync(ws, skip: 5);
        await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "a2ui_sync_result",
            RequestId = syncSent.RequestId,
            SessionId = "sess",
            SurfaceId = "details",
            Success = true,
            ValueJson = "{\"status\":\"approved\"}"
        }, CancellationToken.None);
        var syncResult = await syncTask;

        Assert.Equal("{\"status\":\"approved\"}", syncResult);
        Assert.Equal("a2ui_sync_ui_to_data", syncSent.Type);

        var deleteTask = deleteTool.ExecuteAsync("""{"surfaceId":"details"}""", context, CancellationToken.None);
        var deleteSent = await WaitForSentEnvelopeAsync(ws, skip: 6);
        await AckAsync(broker, "client", deleteSent, "sess");
        var deleteResult = await deleteTask;

        Assert.Contains("Canvas command accepted", deleteResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a2ui_delete_surface", deleteSent.Type);
        Assert.Equal("deleteSurface", deleteSent.Operation);
    }

    private static CanvasCommandBroker CreateBroker(GatewayConfig? config = null)
        => new(
            config ?? new GatewayConfig(),
            new WebSocketChannel(new WebSocketConfig()),
            new RuntimeEventStore(
                Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                NullLogger<RuntimeEventStore>.Instance));

    private static async Task AckAsync(CanvasCommandBroker broker, string clientId, WsServerEnvelope sent, string sessionId)
        => await broker.HandleClientEnvelopeAsync(clientId, new WsClientEnvelope
        {
            Type = "canvas_ack",
            RequestId = sent.RequestId,
            SessionId = sessionId,
            Success = true
        }, CancellationToken.None);

    private static async Task RespondWithSnapshotAsync(
        CanvasCommandBroker broker,
        string requestId,
        string surfaceId,
        string snapshotJson)
        => await broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_snapshot_result",
            RequestId = requestId,
            SessionId = "sess",
            SurfaceId = surfaceId,
            Success = true,
            SnapshotJson = snapshotJson
        }, CancellationToken.None);

    private static (CanvasCommandBroker Broker, TestWebSocket WebSocket) CreateConnectedBroker(
        string[] capabilities,
        string[] supportedCatalogIds,
        GatewayConfig? config = null)
    {
        var channel = new WebSocketChannel(new WebSocketConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client", ws, IPAddress.Loopback, useJsonEnvelope: true));
        var broker = new CanvasCommandBroker(
            config ?? new GatewayConfig(),
            channel,
            new RuntimeEventStore(
                Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                NullLogger<RuntimeEventStore>.Instance));
        broker.HandleClientEnvelopeAsync("client", new WsClientEnvelope
        {
            Type = "canvas_ready",
            Capabilities = capabilities,
            SupportedCatalogIds = supportedCatalogIds
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return (broker, ws);
    }

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

    private static ToolExecutionContext Context(string channelId = "websocket", string senderId = "client")
        => new()
        {
            Session = new Session
            {
                Id = "sess",
                ChannelId = channelId,
                SenderId = senderId
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess",
                ChannelId = channelId
            }
        };
}
