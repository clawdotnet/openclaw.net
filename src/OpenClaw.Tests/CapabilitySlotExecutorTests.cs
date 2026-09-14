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
        return new RouterFixture { State = state, Executor = new CapabilitySlotExecutor(registry, new CapabilityBindingCache()), Server = server, Registry = registry };
    }

    [Fact]
    public async Task Execute_Static_AddsOncePerExecutorInstance_ThenUsesToolEachCall()
    {
        await using var fixture = await CreateFixtureAsync();

        for (var i = 0; i < 2; i++)
        {
            var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
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

        var failed = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, failed.ResultStatus);
        Assert.Equal("capability_add_failed", failed.FailureCode);

        // The failure must not be cached: a later healthy Router still gets an add.
        fixture.State.FailAdd = false;
        var retried = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
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

        var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_use_tool_failed", result.FailureCode);
        Assert.Contains("failed to use tool", result.ResultText);
    }

    [Fact]
    public async Task Execute_Static_ProtocolUseFailure_ReturnsCapabilityUseToolFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailUse = true;

        var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_use_tool_failed", result.FailureCode);
    }

    [Fact]
    public async Task Execute_NoRouterClient_ReturnsCapabilityRouterUnavailable()
    {
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var executor = new CapabilitySlotExecutor(registry, new CapabilityBindingCache());

        var result = await executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_router_unavailable", result.FailureCode);
    }

    [Fact]
    public async Task Execute_Dynamic_ResolvesThenUsesTool()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        Assert.Equal("Weather for Oslo: sunny", result.ResultText);
        Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_ExactNamePolicyNoMatch_ReturnsCapabilityResolveFailed()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(
            DynamicRef(task: "ghost-city", policy: "exact_name"), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_resolve_failed", result.FailureCode);
        Assert.Contains("selection_policy_no_match", result.FailureMessage);
        Assert.Equal(new[] { "search" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_FirstCandidateAddFails_RotatesToNextCandidate()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAddNames.Add("weather-mcp");
        fixture.State.SucceedUseServers.Add("candidate-1");

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        Assert.Equal("Weather for Oslo: sunny", result.ResultText);
        // The first candidate's add fails; rotation binds the next candidate.
        Assert.Equal(new[] { "search", "add:weather-mcp", "add:candidate-1", "use:candidate-1:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_AllCandidateAddsFail_ReturnsCapabilityResolveFailedWithAllAddsFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAdd = true;

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_resolve_failed", result.FailureCode);
        Assert.Contains("all_adds_failed", result.FailureMessage);
        // Every Top-5 candidate is attempted in rank order before giving up.
        Assert.Equal(new[]
        {
            "search",
            "add:weather-mcp", "add:candidate-1", "add:candidate-2", "add:candidate-3", "add:candidate-4"
        }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_NoCandidates_ReturnsCapabilityResolveFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.EmptySearch = true;

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, result.ResultStatus);
        Assert.Equal("capability_resolve_failed", result.FailureCode);
        Assert.Contains("no_candidates", result.FailureMessage);
        Assert.Equal(new[] { "search" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_SameSessionSecondCall_SkipsResolve()
    {
        await using var fixture = await CreateFixtureAsync();

        for (var i = 0; i < 2; i++)
        {
            var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
            Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
            Assert.Equal("Weather for Oslo: sunny", result.ResultText);
        }

        // Same session: the binding is cached, so the second call only proxies.
        Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, fixture.State.Calls);
    }

    [Fact]
    public async Task Execute_Dynamic_DifferentSession_ResolvesIndependently()
    {
        await using var fixture = await CreateFixtureAsync();

        await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-2", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "search", "add:weather-mcp", "use:weather-mcp:get_weather" }, fixture.State.Calls);
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

    [Fact]
    public async Task Execute_Dynamic_RecordsBindingTrajectoryOnResult()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        var trajectory = Assert.IsType<CapabilityBindingTrajectory>(result.BindingTrajectory);
        Assert.Equal("dynamic", trajectory.Binding);
        Assert.Equal("weather city", trajectory.TaskDescription);
        Assert.Equal("weather", trajectory.KeyWords);
        Assert.Equal("first", trajectory.SelectionPolicy);
        Assert.Equal(CapabilityBindingCache.ComputeIntentKey("weather city", "weather", "First"), trajectory.IntentKey);
        Assert.False(trajectory.CacheHit);
        Assert.Equal("weather-mcp", trajectory.Server);
        Assert.Equal("get_weather", trajectory.Tool);
        Assert.True(trajectory.ElapsedMs >= 0);
        Assert.Equal(
            new[] { "weather-mcp", "candidate-1", "candidate-2", "candidate-3", "candidate-4" },
            trajectory.Candidates.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, trajectory.Candidates.Select(c => c.Rank).ToArray());
        var attempted = Assert.Single(trajectory.Attempted);
        Assert.Equal("weather-mcp", attempted.Name);
        Assert.Equal(1, attempted.Rank);
    }

    [Fact]
    public async Task Execute_Dynamic_CacheHit_RecordsCacheHitTrajectory()
    {
        await using var fixture = await CreateFixtureAsync();

        var first = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.False(first.BindingTrajectory!.CacheHit);
        var callsAfterFirst = fixture.State.Calls.Count;

        var second = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Bergen"}""", "sess-1", TestContext.Current.CancellationToken);

        var trajectory = Assert.IsType<CapabilityBindingTrajectory>(second.BindingTrajectory);
        Assert.True(trajectory.CacheHit);
        Assert.Equal("weather-mcp", trajectory.Server);
        Assert.Equal("get_weather", trajectory.Tool);
        Assert.Empty(trajectory.Candidates);
        Assert.Empty(trajectory.Attempted);
        // A cache hit resolves without touching the Router; only use_tool fires.
        Assert.Equal(new[] { "use:weather-mcp:get_weather" }, fixture.State.Calls.Skip(callsAfterFirst).ToArray());
    }

    [Fact]
    public async Task Execute_Dynamic_FirstAddFails_RecordsAttemptedCandidatesInRankOrder()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAddNames.Add("weather-mcp");
        fixture.State.SucceedUseServers.Add("candidate-1");

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        var trajectory = Assert.IsType<CapabilityBindingTrajectory>(result.BindingTrajectory);
        Assert.Equal("candidate-1", trajectory.Server);
        Assert.Equal(5, trajectory.Candidates.Count);
        Assert.Equal(new[] { "weather-mcp", "candidate-1" }, trajectory.Attempted.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Execute_Dynamic_AllAddsFail_RecordsAttemptedCandidatesWithoutServer()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAdd = true;

        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);

        Assert.Equal("capability_resolve_failed", result.FailureCode);
        var trajectory = Assert.IsType<CapabilityBindingTrajectory>(result.BindingTrajectory);
        Assert.Null(trajectory.Server);
        Assert.Null(trajectory.Tool);
        Assert.False(trajectory.CacheHit);
        Assert.Equal(5, trajectory.Candidates.Count);
        Assert.Equal(5, trajectory.Attempted.Count);
    }

    [Fact]
    public async Task Execute_Static_RecordsStaticBindingTrajectory()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);

        var trajectory = Assert.IsType<CapabilityBindingTrajectory>(result.BindingTrajectory);
        Assert.Equal("static", trajectory.Binding);
        Assert.Null(trajectory.TaskDescription);
        Assert.Null(trajectory.IntentKey);
        Assert.False(trajectory.CacheHit);
        Assert.Equal("weather-mcp", trajectory.Server);
        Assert.Equal("get_weather", trajectory.Tool);
        Assert.Empty(trajectory.Candidates);
        Assert.Empty(trajectory.Attempted);
    }

    // Issue #238: Nacos event subscription must invalidate the runtime-level
    // "_addedServers" cache as well as the session-level binding cache. Watcher
    // reload calls ClearRuntimeCache; subsequent slot executions re-add.

    [Fact]
    public void ClearRuntimeCache_InitialCount_IsZero()
    {
        var executor = new CapabilitySlotExecutor(
            new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance),
            new CapabilityBindingCache());
        Assert.Equal(0, executor.AddedServerCount);
    }

    [Fact]
    public async Task ClearRuntimeCache_WipesAddedServers_AndNextExecutionReAdds()
    {
        await using var fixture = await CreateFixtureAsync();

        // First successful execution populates _addedServers with weather-mcp.
        var first = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, first.ResultStatus);
        Assert.True(fixture.Executor.AddedServerCount >= 1);

        fixture.Executor.ClearRuntimeCache();
        Assert.Equal(0, fixture.Executor.AddedServerCount);

        // Next execution must re-add (cache wiped) — verify by call log.
        var second = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, second.ResultStatus);
        var addCalls = fixture.State.Calls.Count(c => c.StartsWith("add:weather-mcp", StringComparison.Ordinal));
        Assert.Equal(2, addCalls); // 1 before ClearRuntimeCache + 1 after
        Assert.True(fixture.Executor.AddedServerCount >= 1);
    }

    [Fact]
    public async Task ClearRuntimeCache_PreservesFailureNotCachedContract()
    {
        await using var fixture = await CreateFixtureAsync();

        // Failure path: add NOT cached (per existing #231 contract).
        fixture.State.FailAdd = true;
        var failed = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Failed, failed.ResultStatus);
        Assert.Equal(0, fixture.Executor.AddedServerCount);

        fixture.State.FailAdd = false;
        fixture.Executor.ClearRuntimeCache(); // no-op semantics on empty cache, but must not throw
        Assert.Equal(0, fixture.Executor.AddedServerCount);

        // Healthy retry succeeds and adds.
        var succeeded = await fixture.Executor.ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, succeeded.ResultStatus);
        Assert.True(fixture.Executor.AddedServerCount >= 1);
    }
}
