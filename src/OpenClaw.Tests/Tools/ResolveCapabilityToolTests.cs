using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class ResolveCapabilityToolTests
{
    [Fact]
    public async Task HappyPath_ReturnsBinding_AndCallsSearchThenAddOnce()
    {
        var (tool, _, state, server) = await BuildAsync();
        await using (server)
        {
            var args = """
                {"task_description":"weather city","key_words":"weather,city","selection_policy":"first"}
                """;
            var result = await tool.ExecuteAsync(args, CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            Assert.Equal("weather-mcp", root.GetProperty("server").GetString());
            Assert.Equal("get_weather", root.GetProperty("tool").GetString());
            Assert.Equal(1, root.GetProperty("tried").GetArrayLength());

            // No use_tool call ever happens in the resolver.
            Assert.DoesNotContain(state.Calls, c => c.StartsWith("use:"));
            // search then add for the chosen candidate.
            Assert.Contains("search", state.Calls);
            Assert.Contains("add:weather-mcp", state.Calls);
        }
    }

    [Fact]
    public async Task EmptySearch_ReturnsNoCandidatesFailure()
    {
        var (tool, _, state, serverLifetime) = await BuildEmptyAsync();
        await using var _ = serverLifetime;
        var args = """{"task_description":"absurdly_unlikely_intent"}""";
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("no_candidates", doc.RootElement.GetProperty("failure_code").GetString());
        Assert.Empty(state.Calls.FindAll(c => c.StartsWith("add:")));
    }

    [Fact]
    public async Task AllAddsFail_ReturnsAllAddsFailed_WithTriedList()
    {
        var (tool, _, _, serverLifetime) = await BuildAllFailAsync();
        await using var _ = serverLifetime;
        var args = """{"task_description":"weather city","key_words":"weather"}""";
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("all_bindings_failed", doc.RootElement.GetProperty("failure_code").GetString());
        var tried = doc.RootElement.GetProperty("tried").EnumerateArray().ToList();
        Assert.NotEmpty(tried);
    }

    [Fact]
    public async Task MalformedToolList_ReturnsAllAddsFailed_InsteadOfThrowing()
    {
        var (tool, _, state, serverLifetime) = await BuildMalformedAsync();
        await using var _ = serverLifetime;
        // The fixture's add response is 安装完成 + a tool list whose first object has
        // no "name" property; pre-fix TryExtractTool threw KeyNotFoundException here.
        var args = """{"task_description":"weather city","key_words":"weather"}""";
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("all_bindings_failed", doc.RootElement.GetProperty("failure_code").GetString());
        var tried = doc.RootElement.GetProperty("tried").EnumerateArray().ToList();
        var attempted = Assert.Single(tried);
        Assert.Equal("weather-mcp", attempted.GetProperty("name").GetString());
        Assert.Contains("add:weather-mcp", state.Calls);
    }

    [Fact]
    public async Task ExactNamePolicy_CaseInsensitiveMatch_ReturnsBinding()
    {
        var (tool, _, state, server) = await BuildAsync();
        await using (server)
        {
            var args = """{"task_description":"WEATHER-MCP","selection_policy":"exact_name"}""";
            var result = await tool.ExecuteAsync(args, CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            Assert.Equal("weather-mcp", root.GetProperty("server").GetString());
            Assert.Equal("get_weather", root.GetProperty("tool").GetString());
            Assert.Equal(1, root.GetProperty("tried").GetArrayLength());
            Assert.Contains("add:weather-mcp", state.Calls);
        }
    }

    [Fact]
    public async Task ExactNamePolicy_ZeroMatch_ReturnsSelectionPolicyNoMatch_WithEmptyTried()
    {
        var (tool, _, state, server) = await BuildAsync();
        await using (server)
        {
            var args = """{"task_description":"nonexistent-mcp","selection_policy":"exact_name"}""";
            var result = await tool.ExecuteAsync(args, CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            // Zero match under exact_name is a selection failure, not an add
            // failure: no add was ever attempted, so "all_bindings_failed" would lie.
            Assert.Equal("selection_policy_no_match", doc.RootElement.GetProperty("failure_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("tried").EnumerateArray().ToList());
            Assert.Empty(state.Calls.FindAll(c => c.StartsWith("add:")));
        }
    }

    [Fact]
    public async Task SearchError_ReturnsRouterUnavailable_InsteadOfNoCandidates()
    {
        var (tool, _, _, server) = await BuildWithAsync<SearchErrorFakeNacosRouter>();
        await using (server)
        {
            var result = await tool.ExecuteAsync("""{"task_description":"weather city"}""", CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            // A protocol-level search failure is a router failure, not "no candidates".
            Assert.Equal("provider_unavailable", doc.RootElement.GetProperty("failure_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("tried").EnumerateArray().ToList());
        }
    }

    [Fact]
    public async Task AddError_IsErrorBeatsInstallProse_ReturnsAllAddsFailed()
    {
        var (tool, _, _, server) = await BuildWithAsync<AddErrorFakeNacosRouter>();
        await using (server)
        {
            var result = await tool.ExecuteAsync("""{"task_description":"weather city","key_words":"weather"}""", CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            // The add response carries IsError=true even though its prose contains
            // "安装完成" and a parseable tool list — the protocol error must win
            // over prose inspection, otherwise a failed install binds successfully.
            Assert.Equal("all_bindings_failed", doc.RootElement.GetProperty("failure_code").GetString());
            var tried = doc.RootElement.GetProperty("tried").EnumerateArray().ToList();
            Assert.Single(tried);
        }
    }

    [Fact]
    public async Task TransportFailure_OnSearch_ReturnsRouterUnavailable_InsteadOfThrowing()
    {
        var (tool, _, _, server) = await BuildAsync();
        await using (server)
        {
            // Reload succeeded while the server was up (the registry connects
            // eagerly); stopping it afterwards simulates the Router dying
            // mid-session, which must surface as a structured envelope.
            await server.StopAsync(TestContext.Current.CancellationToken);
            var result = await tool.ExecuteAsync("""{"task_description":"weather city"}""", CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("provider_unavailable", doc.RootElement.GetProperty("failure_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("tried").EnumerateArray().ToList());
        }
    }

    [Fact]
    public async Task LiveCapturedAddEnvelope_BindsQueryWeather()
    {
        var (tool, _, _, server) = await BuildWithAsync<LiveCapturedAddEnvelopeFakeNacosRouter>();
        await using (server)
        {
            var result = await tool.ExecuteAsync("""{"task_description":"weather city","key_words":"weather"}""", CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            Assert.Equal("cn.pianam.mcp/weather-mcp-china", root.GetProperty("server").GetString());
            Assert.Equal("query_weather", root.GetProperty("tool").GetString());
            using var schema = JsonDocument.Parse(root.GetProperty("schema").GetString()!);
            Assert.Contains("city",
                schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        }
    }

    [Fact]
    public async Task MetaSkill_ResolveCapabilityOnly_ZeroLlmRoundtrips()
    {
        var (resolveTool, _, _, server) = await BuildAsync();
        await using (server)
        {
            var tools = new ITool[] { resolveTool, new EmitTextTool() };

            var chat = Substitute.For<IChatClient>();
            var execution = Substitute.For<ILlmExecutionService>();
            var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            using var memory = new FileMemoryStore(root, 4);

            var session = new Session { Id = "resolve-cap-test", SenderId = "test", ChannelId = "test" };
            var skill = new SkillDefinition
            {
                Name = "resolve-cap-fixture",
                Description = "resolve capability fixture",
                Instructions = "resolve capability fixture",
                Location = "/skills/resolve-cap-fixture",
                Kind = SkillKind.Meta,
                FinalTextMode = "step:answer",
                Composition = new MetaSkillComposition
                {
                    Steps = new[]
                    {
                        new MetaSkillStepDefinition
                        {
                            Id = "resolve", Kind = "tool_call",
                            Tool = "resolve_capability",
                            ToolArgsJson = """{"task_description":"weather city","key_words":"weather"}""",
                        },
                        new MetaSkillStepDefinition
                        {
                            Id = "answer", Kind = "tool_call",
                            Tool = "emit_text",
                            ToolArgsJson = """{"text":"{{ outputs.resolve | xml_escape }}"}""",
                            DependsOn = new[] { "resolve" },
                        },
                    },
                },
            };

            var runtime = new AgentRuntime(chat, tools, memory, new GatewayConfig().Llm, maxHistoryTurns: 5,
                skills: [skill], llmExecutionService: execution);

            var method = typeof(AgentRuntime).GetMethod("ExecuteMetaSkillAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string>)method.Invoke(runtime,
                [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;

            Assert.Contains("weather-mcp", result);
            Assert.Empty(chat.ReceivedCalls());
            Assert.Empty(execution.ReceivedCalls());

            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ToolName_IsResolveCapability()
    {
        Assert.Equal("resolve_capability", new ResolveCapabilityTool(
            new CapabilityProviderRegistry([])).Name);
    }

    [Fact]
    public void ParameterSchema_DeclaresIntentFields()
    {
        var schema = new ResolveCapabilityTool(
            new CapabilityProviderRegistry([])).ParameterSchema;
        using var doc = JsonDocument.Parse(schema);
        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("task_description", required);
        Assert.Contains("selection_policy", doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    private sealed class RouterLifetime(WebApplication server, McpServerToolRegistry registry) : IAsyncDisposable
    {
        public Task StopAsync(CancellationToken ct) => server.StopAsync(ct);
        public async ValueTask DisposeAsync()
        {
            try { await registry.DisposeAsync(); }
            finally { await server.DisposeAsync(); }
        }
    }

    private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state, RouterLifetime server)> BuildAsync()
        => await BuildWithAsync<FakeNacosRouterMcpTools>();

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        RouterLifetime server)>
        BuildWithAsync<TTools>() where TTools : class
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<TTools>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        // Real registry: configured above via ReloadWorkspaceServersAsync against the test MCP server.
        var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        var lifetime = new RouterLifetime(server, registry);
        try
        {
            await server.StartAsync(TestContext.Current.CancellationToken);
            await registry.ReloadWorkspaceServersAsync(
                new Dictionary<string, McpServerConfig>
                {
                    ["nacos-mcp-router"] = new()
                    {
                        Enabled = true,
                        Transport = "http",
                        Url = server.Urls.Single() + "/mcp",
                        ToolNamePrefix = "nacos_mcp_router_",
                    },
                }, TestContext.Current.CancellationToken);
            return (new ResolveCapabilityTool(NacosTestProviders.Create(registry)), registry, state, lifetime);
        }
        catch
        {
            await lifetime.DisposeAsync();
            throw;
        }
    }

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        RouterLifetime server)>
        BuildEmptyAsync()
        => await BuildWithAsync<EmptyFakeNacosRouter>();

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        RouterLifetime server)>
        BuildAllFailAsync()
        => await BuildWithAsync<AllFailFakeNacosRouter>();

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        RouterLifetime server)>
        BuildMalformedAsync()
        => await BuildWithAsync<MalformedToolListFakeNacosRouter>();

    // Live capture from a real nacos-mcp-router 0.2.2 add response (2026-09-14,
    // local test bed, server cn.pianam.mcp/weather-mcp-china): spaced JSON,
    // nested inputSchema, slash in the server name.
    [McpServerToolType]
    private sealed class LiveCapturedAddEnvelopeFakeNacosRouter
    {
        [McpServerTool(Name = "search_mcp_server")]
        public string Search(string task_description, string key_words) =>
            "## 获取weather city的步骤如下：\n"
            + RouterProseContract.SearchListMarker
            + """{"cn.pianam.mcp/weather-mcp-china": {"name": "cn.pianam.mcp/weather-mcp-china", "description": "MCP server for current weather and multi-day forecasts worldwide, Chinese city names and output."}}"""
            + "\n" + RouterProseContract.SearchStepMarker
            + "从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

        [McpServerTool(Name = "add_mcp_server")]
        public string Add(string mcp_server_name) =>
            "1. " + mcp_server_name + RouterProseContract.AddSuccessMarker + ", " + RouterProseContract.AddToolListMarker
            + """[{"name": "query_weather", "description": "查询全球城市的实时天气和未来几天预报。city城市名中英文均可，days预报天数1~7默认3天。主源Open-Meteo，备源wttr.in。", "inputSchema": {"properties": {"city": {"title": "City", "type": "string"}, "days": {"default": 3, "title": "Days", "type": "integer"}}, "required": ["city"], "title": "query_weatherArguments", "type": "object"}}]"""
            + "\n2." + mcp_server_name + "的工具需要通过nacos-mcp-router的use_tool工具代理使用";
    }

    [McpServerToolType]
    private sealed class EmptyFakeNacosRouter
    {
        [McpServerTool(Name = "search_mcp_server")]
        public string Search(string task_description, string key_words) =>
            RouterProseContract.SearchListMarker + "{}\n" + RouterProseContract.SearchStepMarker;
    }

    [McpServerToolType]
    private sealed class SearchErrorFakeNacosRouter
    {
        [McpServerTool(Name = "search_mcp_server")]
        public CallToolResult Search(string task_description, string key_words) =>
            new() { IsError = true, Content = [new TextContentBlock { Text = "search failed" }] };
    }

    [McpServerToolType]
    private sealed class AddErrorFakeNacosRouter
    {
        [McpServerTool(Name = "search_mcp_server")]
        public string Search(string task_description, string key_words) =>
            "## 获取weather city的步骤如下：\n"
            + RouterProseContract.SearchListMarker
            + """{"weather-mcp":{"name":"weather-mcp","description":"weather city"}}"""
            + "\n" + RouterProseContract.SearchStepMarker
            + "从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

        // IsError=true even though the prose says 安装完成 and carries a valid
        // tool list: protocol error must win over prose inspection.
        [McpServerTool(Name = "add_mcp_server")]
        public CallToolResult Add(string mcp_server_name) =>
            new()
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = "1. " + mcp_server_name + RouterProseContract.AddSuccessMarker + ", " + RouterProseContract.AddToolListMarker
                            + "[{\"name\":\"get_weather\",\"description\":\"weather city\",\"inputSchema\":{\"type\":\"object\"}}]\n2. 后续通过use_tool代理使用",
                    },
                ],
            };
    }
}
