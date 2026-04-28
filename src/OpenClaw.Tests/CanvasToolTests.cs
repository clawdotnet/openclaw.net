using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
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

    private static CanvasCommandBroker CreateBroker(GatewayConfig? config = null)
        => new(
            config ?? new GatewayConfig(),
            new WebSocketChannel(new WebSocketConfig()),
            new RuntimeEventStore(
                Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                NullLogger<RuntimeEventStore>.Instance));

    private static ToolExecutionContext Context(string channelId = "websocket")
        => new()
        {
            Session = new Session
            {
                Id = "sess",
                ChannelId = channelId,
                SenderId = "client"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess",
                ChannelId = channelId
            }
        };
}
