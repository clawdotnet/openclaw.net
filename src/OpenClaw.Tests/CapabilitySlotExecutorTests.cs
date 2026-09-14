using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Unit tests for the #231 capability slot executor: static auto-add caching,
/// dynamic resolve-then-use, typed failure codes, and use_tool repr-shell
/// stripping (live capture fixture).
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class CapabilitySlotExecutorTests
{
    private static MetaCapabilityRefDefinition StaticRef(string server = "weather-mcp", string tool = "get_weather") => new()
    {
        Binding = "static",
        Static = new MetaCapabilityStaticBinding { McpServerName = server, ToolName = tool },
    };

    private static MetaCapabilityRefDefinition DynamicRef(string task = "weather city", string policy = "first") => new()
    {
        Binding = "dynamic",
        Intent = new MetaCapabilityIntent { TaskDescription = task, Keywords = ["weather"] },
        SelectionPolicy = policy,
    };

    private sealed class RouterFixture : IAsyncDisposable
    {
        public required NacosRouterFixtureState State { get; init; }
        public required CapabilitySlotExecutor Executor { get; init; }
        public required WebApplication Server { get; init; }
        public required McpServerToolRegistry Registry { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Registry.DisposeAsync();
        }
    }

    private static async Task<RouterFixture> CreateFixtureAsync(NacosRouterFixtureState? state = null)
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
        await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>
        {
            ["nacos-mcp-router"] = new() { Enabled = true, Transport = "http", Url = server.Urls.Single() + "/mcp", ToolNamePrefix = "nacos_mcp_router_" }
        }, TestContext.Current.CancellationToken);
        return new RouterFixture { State = state, Executor = new CapabilitySlotExecutor(registry), Server = server, Registry = registry };
    }

    [Fact]
    public async Task Execute_Static_AddsOncePerExecutorInstance_ThenUsesToolEachCall()
    {
        await using var fixture = await CreateFixtureAsync();

        for (var i = 0; i < 2; i++)
        {
            var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
            Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
            Assert.Equal("Weather for Oslo: sunny", result.ResultText);
        }

        Assert.Equal(new[] { "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Static_AddFailure_ReturnsCapabilityAddFailed_AndDoesNotPoisonCache()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAdd = true;

        var failed = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, failed.ResultStatus);
        Assert.Equal("capability_add_failed", failed.FailureCode);

        // The failure must not be cached: a later healthy Router still gets an add.
        fixture.State.FailAdd = false;
        var retried = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, retried.ResultStatus);
        Assert.Equal("Weather for Oslo: sunny", retried.ResultText);
        Assert.Equal(new[] { "add:weather-mcp", "add:weather-mcp", "use:weather-mcp:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Static_PlainTextUseFailure_ReturnsCapabilityUseToolFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailUse = true;
        fixture.State.PlainTextFailure = true;

        var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_use_tool_failed", result.FailureCode);
        Assert.Contains("failed to use tool", result.ResultText);
    }

    [Fact]
    public async Task Execute_Static_ProtocolUseFailure_ReturnsCapabilityUseToolFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailUse = true;

        var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_use_tool_failed", result.FailureCode);
    }

    [Fact]
    public async Task Execute_NoRouterClient_ReturnsCapabilityRouterUnavailable()
    {
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var executor = new CapabilitySlotExecutor(registry);

        var result = await executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_router_unavailable", result.FailureCode);
    }

    [Fact]
    public async Task Execute_Dynamic_ResolvesThenUsesTool()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        Assert.Equal("Weather for Oslo: sunny", result.ResultText);
        Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_ExactNamePolicyNoMatch_ReturnsCapabilityResolveFailed()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(
            DynamicRef(task: "ghost-city", policy: "exact_name"), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_resolve_failed", result.FailureCode);
        Assert.Contains("selection_policy_no_match", result.FailureMessage);
        Assert.Equal(new[] { "search" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_NoCandidates_ReturnsCapabilityResolveFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.EmptySearch = true;

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_resolve_failed", result.FailureCode);
        Assert.Contains("no_candidates", result.FailureMessage);
        Assert.Equal(new[] { "search" }, fixture.State.Calls);
    }

    [Theory]
    [InlineData(
        "[TextContent(type='text', text='{\"city\":\"Oslo\"}', annotations=None, meta=None)]",
        "{\"city\":\"Oslo\"}")]
    [InlineData(
        "[TextContent(type='text', text='Error processing mcp-server-time query: Unknown tool: get_weather', annotations=None, meta=None)]",
        "Error processing mcp-server-time query: Unknown tool: get_weather")]
    [InlineData(
        "[TextContent(type='text', text=\"it's sunny\", annotations=None, meta=None)]",
        "it's sunny")]
    [InlineData("Weather for Oslo: sunny", "Weather for Oslo: sunny")]
    [InlineData("", "")]
    public void StripUseToolShell_StripsLiveCapturedEnvelope(string raw, string expected)
    {
        Assert.Equal(expected, CapabilitySlotExecutor.StripUseToolShell(raw));
    }
}
