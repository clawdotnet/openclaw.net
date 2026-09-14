using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

// Issue #237: a protocol-level IsError=true from an MCP server must
// surface as a typed ToolOutcomeException ("mcp_tool_error") through the
// IToolWithContext route so meta-skill tool_call steps fail and on_failure
// substitutes fire. These tests pin the adapter boundary directly, before
// any capability-slot normalization (#231/#233) or fallback translation.
[Collection(EnvironmentVariableCollection.Name)]
public sealed class McpNativeToolErrorTests
{
    private const string UseArgs =
        """{"mcp_server_name":"weather-mcp","mcp_tool_name":"get_weather","params":"{\"city\":\"Oslo\"}"}""";

    private sealed class Fixture(
        McpServerToolRegistry registry,
        IReadOnlyList<ITool> addedTools,
        WebApplication server) : IAsyncDisposable
    {
        public IReadOnlyList<ITool> AddedTools { get; } = addedTools;

        public async ValueTask DisposeAsync()
        {
            await registry.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(NacosRouterFixtureState? state = null)
    {
        state ??= new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = new Dictionary<string, McpServerConfig>
        {
            ["nacos-mcp-router"] = new()
            {
                Enabled = true,
                Transport = "http",
                Url = server.Urls.Single() + "/mcp",
                ToolNamePrefix = "nacos_mcp_router_"
            }
        };
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        return new Fixture(registry, reload.AddedTools, server);
    }

    private static ToolExecutionContext CreateContext() => new()
    {
        Session = new Session { Id = "mcp-iserror", SenderId = "test", ChannelId = "test" },
        TurnContext = new TurnContext()
    };

    [Fact]
    public async Task ExecuteAsync_WithContext_ProtocolIsError_ThrowsTypedToolOutcome()
    {
        await using var fixture = await CreateFixtureAsync(new NacosRouterFixtureState { FailUse = true });
        var use = (IToolWithContext)fixture.AddedTools.Single(t => t.Name.EndsWith("_use_tool"));

        var ex = await Assert.ThrowsAsync<ToolOutcomeException>(async () =>
        {
            await use.ExecuteAsync(UseArgs, CreateContext(), TestContext.Current.CancellationToken);
        });

        Assert.Equal("failed", ex.ResultStatus);
        Assert.Equal("mcp_tool_error", ex.FailureCode);
        Assert.Equal("failed to use tool: get_weather", ex.FailureMessage);
        Assert.Equal("Error: failed to use tool: get_weather", ex.Result);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_Success_ReturnsPlainText()
    {
        await using var fixture = await CreateFixtureAsync();
        var use = (IToolWithContext)fixture.AddedTools.Single(t => t.Name.EndsWith("_use_tool"));

        var result = await use.ExecuteAsync(UseArgs, CreateContext(), TestContext.Current.CancellationToken);

        Assert.Equal("Weather for Oslo: sunny", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_PlainTextFailure_ReturnsTextUnchangedWithoutThrowing()
    {
        await using var fixture = await CreateFixtureAsync(
            new NacosRouterFixtureState { FailUse = true, PlainTextFailure = true });
        var use = (IToolWithContext)fixture.AddedTools.Single(t => t.Name.EndsWith("_use_tool"));

        // IsError=false text passes through unchanged — the "Error: " prefix
        // and the typed exception are reserved for protocol-level failures.
        var result = await use.ExecuteAsync(UseArgs, CreateContext(), TestContext.Current.CancellationToken);

        Assert.Equal("failed to use tool: get_weather", result);
    }

    [Fact]
    public async Task ExecuteAsync_NoContext_ProtocolIsError_ReturnsErrorString()
    {
        await using var fixture = await CreateFixtureAsync(new NacosRouterFixtureState { FailUse = true });
        var use = fixture.AddedTools.Single(t => t.Name.EndsWith("_use_tool"));

        var result = await use.ExecuteAsync(UseArgs, TestContext.Current.CancellationToken);

        Assert.Equal("Error: failed to use tool: get_weather", result);
    }
}
