using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class NacosRouterIntegrationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task StaticSlot_UsesOnlyThreeRouterTools_AndHandlesProtocolFailures(bool maf, bool fail, bool plainText)
    {
        var state = new NacosRouterFixtureState { FailUse = fail, PlainTextFailure = plainText };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = ServerConfig(server.Urls.Single() + "/mcp");
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "nacos_mcp_router_add_mcp_server", "nacos_mcp_router_search_mcp_server", "nacos_mcp_router_use_tool" }, reload.AddedTools.Select(t => t.Name).Order().ToArray());
        var search = await reload.AddedTools.Single(t => t.Name.EndsWith("_search_mcp_server")).ExecuteAsync("""{"task_description":"weather city","key_words":"weather,city"}""", TestContext.Current.CancellationToken);
        // Search envelope must mirror the upstream Router prose structure so that
        // contract drift between the fixture and the live Router surfaces as a test
        // failure. Per upstream router.py, search emits a header line plus the
        // candidate list JSON between the two `###` markers.
        Assert.Contains("## 获取weather city的步骤如下：", search);
        Assert.Contains(RouterProseContract.SearchListMarker, search);
        Assert.Contains(RouterProseContract.SearchStepMarker, search);
        Assert.Contains("weather-mcp", search);
        Assert.DoesNotContain("score", search);
        if (fail)
        {
            // Per upstream contract, `params` is a JSON-encoded string the Router
            // forwards via `json.loads` to the inner MCP tool. The fixture mirrors
            // that, so the wire call here must serialize params as a string.
            var direct = await reload.AddedTools.Single(t => t.Name.EndsWith("_use_tool")).ExecuteAsync(
                """{"mcp_server_name":"weather-mcp","mcp_tool_name":"get_weather","params":"{\"city\":\"Oslo\"}"}""", TestContext.Current.CancellationToken);
            Assert.Equal(plainText ? "failed to use tool: get_weather" : "Error: failed to use tool: get_weather", direct);
        }
        state.Calls.Clear();
        var skill = LoadStaticDemo();
        // Use a unique sibling dir under TEMP directly to avoid leaving an empty
        // `nacos-poc-tests` parent behind across CI runs.
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig, new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var session = new Session { Id = "nacos-poc-" + i, SenderId = "test", ChannelId = "test" };
                var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
                // The capability slot normalises both plain-text and protocol use
                // failures into a typed failure code, so the fallback branch fires
                // for both modes.
                Assert.Equal(fail ? "weather-mcp unavailable; check Nacos registration and Router logs." : "Weather for Oslo: sunny", result);
                var run = Assert.Single(session.MetaRunHistory);
                var query = Assert.Single(run.StepResults, step => step.Id == "query");
                if (fail) Assert.Equal("capability_use_tool_failed", query.FailureCode);
                else Assert.Equal("completed", query.Status);
            }
            // The static slot auto-adds once per runtime (idempotent, cached);
            // each invocation still proxies through use_tool.
            Assert.Equal(new[] { "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, state.Calls);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());
            var unchanged = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
            Assert.Empty(unchanged.AddedTools);
            Assert.Empty(unchanged.RemovedToolNames);
        }
        finally
        {
            if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (runtime is IDisposable disposable) disposable.Dispose();
            memory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure(bool maf, bool fail)
    {
        var state = new NacosRouterFixtureState { FailUse = fail, PlainTextFailure = true };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = ServerConfig(server.Urls.Single() + "/mcp");
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        state.Calls.Clear();
        var skill = LoadDynamicDemo();
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig, new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var session = new Session { Id = "nacos-dyn-" + i, SenderId = "test", ChannelId = "test" };
                var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
                Assert.Equal(fail ? "no weather capability bound; check Nacos registration and Router logs." : "Weather for Oslo: sunny", result);
                var run = Assert.Single(session.MetaRunHistory);
                var query = Assert.Single(run.StepResults, step => step.Id == "query");
                if (fail) Assert.Equal("capability_use_tool_failed", query.FailureCode);
                else Assert.Equal("completed", query.Status);
            }
            // Dynamic slots resolve per invocation (search → add → use); the
            // resolution itself is deterministic and model-free.
            Assert.Equal(new[]
            {
                "search", "add:weather-mcp", "use:weather-mcp:get_weather",
                "search", "add:weather-mcp", "use:weather-mcp:get_weather"
            }, state.Calls);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());
        }
        finally
        {
            if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (runtime is IDisposable disposable) disposable.Dispose();
            memory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicSlot_EmptySearch_RoutesToFallback(bool maf)
    {
        var state = new NacosRouterFixtureState { EmptySearch = true };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = ServerConfig(server.Urls.Single() + "/mcp");
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        state.Calls.Clear();
        var skill = LoadDynamicDemo();
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig, new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            var session = new Session { Id = "nacos-empty-" + (maf ? "maf" : "native"), SenderId = "test", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
            // Empty search degrades to the fallback step and completes normally.
            Assert.Equal("no weather capability bound; check Nacos registration and Router logs.", result);
            var run = Assert.Single(session.MetaRunHistory);
            var query = Assert.Single(run.StepResults, step => step.Id == "query");
            Assert.Equal("capability_resolve_failed", query.FailureCode);
            var fallback = Assert.Single(run.StepResults, step => step.Id == "fallback_notice");
            Assert.Equal("completed", fallback.Status);
            Assert.Equal(new[] { "search" }, state.Calls);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());
        }
        finally
        {
            if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (runtime is IDisposable disposable) disposable.Dispose();
            memory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicSlot_FirstCandidateAddFails_BindsToNextCandidate(bool maf)
    {
        var state = new NacosRouterFixtureState();
        state.FailAddNames.Add("weather-mcp");
        state.SucceedUseServers.Add("candidate-1");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = ServerConfig(server.Urls.Single() + "/mcp");
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        state.Calls.Clear();
        var skill = LoadDynamicDemo();
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig, new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            var session = new Session { Id = "nacos-rotate-" + (maf ? "maf" : "native"), SenderId = "test", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
            // The first candidate's add fails; the DAG binds the second candidate.
            Assert.Equal("Weather for Oslo: sunny", result);
            var run = Assert.Single(session.MetaRunHistory);
            var query = Assert.Single(run.StepResults, step => step.Id == "query");
            Assert.Equal("completed", query.Status);
            Assert.Equal(new[] { "search", "add:weather-mcp", "add:candidate-1", "use:candidate-1:get_weather" }, state.Calls);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());
        }
        finally
        {
            if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (runtime is IDisposable disposable) disposable.Dispose();
            memory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicSlot_SameSession_ResolvesOnceThenReusesBinding(bool maf)
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var config = ServerConfig(server.Urls.Single() + "/mcp");
        var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
        state.Calls.Clear();
        var skill = LoadDynamicDemo();
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig,
            new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            // One session, two invocations: the first resolves, the second reuses.
            var session = new Session { Id = "nacos-dyn-cached", SenderId = "test", ChannelId = "test" };
            for (var i = 0; i < 2; i++)
            {
                var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
                Assert.Equal("Weather for Oslo: sunny", result);
                var run = session.MetaRunHistory[i];
                var query = Assert.Single(run.StepResults, step => step.Id == "query");
                Assert.Equal("completed", query.Status);
            }
            Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, state.Calls);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());
        }
        finally
        {
            if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (runtime is IDisposable disposable) disposable.Dispose();
            memory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static bool LiveEnabled => Environment.GetEnvironmentVariable("OPENCLAW_NACOS_LIVE") == "1";

    [Fact(Skip = "Set OPENCLAW_NACOS_LIVE=1 and OPENCLAW_NACOS_ROUTER_URL for a provisioned Router.", SkipUnless = nameof(LiveEnabled))]
    public async Task LiveRouter_SearchFindsRegisteredWeatherServer()
    {
        var url = Environment.GetEnvironmentVariable("OPENCLAW_NACOS_ROUTER_URL");
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https",
            "OPENCLAW_NACOS_ROUTER_URL must be an HTTP(S) MCP endpoint.");
        await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var reload = await registry.ReloadWorkspaceServersAsync(ServerConfig(url!), timeout.Token);
        var search = Assert.Single(reload.AddedTools, tool => tool.Name == "nacos_mcp_router_search_mcp_server");
        var result = await search.ExecuteAsync("""{"task_description":"weather city","key_words":"weather,city"}""", timeout.Token);
        Assert.Contains("weather-mcp", result);
        Assert.DoesNotContain("Error:", result);
    }

    private static (object Runtime, IChatClient Chat, ILlmExecutionService Execution) CreateRuntime(
        bool maf, IReadOnlyList<ITool> tools, IMemoryStore memory, SkillDefinition skill, GatewayConfig gatewayConfig,
        CapabilitySlotExecutor capabilitySlotExecutor)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var chat = Substitute.For<IChatClient>();
        var execution = Substitute.For<ILlmExecutionService>();
        if (maf)
        {
            var options = new MafOptions();
            var runtime = new MafAgentRuntime(new AgentRuntimeFactoryContext
            {
                Services = services, Config = gatewayConfig,
                RuntimeState = new GatewayRuntimeState { RequestedMode = "jit", EffectiveMode = GatewayRuntimeMode.Jit, DynamicCodeSupported = true },
                ChatClient = chat, Tools = tools, MemoryStore = memory,
                RuntimeMetrics = new RuntimeMetrics(), ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = execution, Skills = [skill], SkillsConfig = new SkillsConfig(),
                WorkspacePath = null, PluginSkillDirs = [], Logger = NullLogger.Instance,
                Hooks = [], RequireToolApproval = false, ApprovalRequiredTools = [],
                CapabilitySlotExecutor = capabilitySlotExecutor
            }, options, new MafAgentFactory(Options.Create(options), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(gatewayConfig, Options.Create(options), NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(), NullLogger<MafAgentRuntime>.Instance);
            return (runtime, chat, execution);
        }
        var native = new AgentRuntime(chat, tools, memory, gatewayConfig.Llm, maxHistoryTurns: 5, skills: [skill], capabilitySlotExecutor: capabilitySlotExecutor);
        return (native, chat, execution);
    }

    private static Dictionary<string, McpServerConfig> ServerConfig(string url) => new()
    {
        ["nacos-mcp-router"] = new McpServerConfig { Enabled = true, Transport = "http", Url = url, ToolNamePrefix = "nacos_mcp_router_" }
    };

    private static SkillDefinition LoadStaticDemo() => LoadSkill("nacos-router-weather");

    private static SkillDefinition LoadDynamicDemo() => LoadSkill("nacos-router-weather-dynamic");

    private static SkillDefinition LoadSkill(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "OpenClaw.Net.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var skills = SkillLoader.LoadAll(new SkillsConfig
        {
            Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false, IncludeWorkspace = false,
                ExtraDirs = [Path.Join(root.FullName, "examples", "skills", "nacos-router-weather"),
                    Path.Join(root.FullName, "examples", "skills", "nacos-router-weather-explore"),
                    Path.Join(root.FullName, "examples", "skills", "nacos-router-weather-dynamic")] }
        }, null, NullLogger.Instance);
        Assert.Equal(SkillKind.Standard, Assert.Single(skills, skill => skill.Name == "nacos-router-weather-explore").Kind);
        return Assert.Single(skills, skill => skill.Name == name);
    }
}
