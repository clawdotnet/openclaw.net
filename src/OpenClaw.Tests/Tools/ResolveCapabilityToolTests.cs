using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
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
        Assert.Equal("all_adds_failed", doc.RootElement.GetProperty("failure_code").GetString());
        var tried = doc.RootElement.GetProperty("tried").EnumerateArray().ToList();
        Assert.NotEmpty(tried);
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
            new McpServerToolRegistry(
                new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance)).Name);
    }

    [Fact]
    public void ParameterSchema_DeclaresIntentFields()
    {
        var schema = new ResolveCapabilityTool(
            new McpServerToolRegistry(
                new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance)).ParameterSchema;
        using var doc = JsonDocument.Parse(schema);
        var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("task_description", required);
        Assert.Contains("selection_policy", doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state, WebApplication server)> BuildAsync()
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FakeNacosRouterMcpTools>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        // Real registry: configured above via ReloadWorkspaceServersAsync against the test MCP server.
        var reload = await registry.ReloadWorkspaceServersAsync(
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
        var tool = new ResolveCapabilityTool(registry);
        return (tool, registry, state, server);
    }

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        WebApplication server)>
        BuildEmptyAsync()
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<EmptyFakeNacosRouter>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>
            {
                ["nacos-mcp-router"] = new()
                {
                    Enabled = true, Transport = "http",
                    Url = server.Urls.Single() + "/mcp",
                    ToolNamePrefix = "nacos_mcp_router_",
                },
            }, TestContext.Current.CancellationToken);
        return (new ResolveCapabilityTool(registry), registry, state, server);
    }

    private static async Task<(
        ResolveCapabilityTool tool,
        McpServerToolRegistry registry,
        NacosRouterFixtureState state,
        WebApplication server)>
        BuildAllFailAsync()
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AllFailFakeNacosRouter>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>
            {
                ["nacos-mcp-router"] = new()
                {
                    Enabled = true, Transport = "http",
                    Url = server.Urls.Single() + "/mcp",
                    ToolNamePrefix = "nacos_mcp_router_",
                },
            }, TestContext.Current.CancellationToken);
        return (new ResolveCapabilityTool(registry), registry, state, server);
    }

    [McpServerToolType]
    private sealed class EmptyFakeNacosRouter
    {
        [McpServerTool(Name = "search_mcp_server")]
        public string Search(string task_description, string key_words) => "### 1. 当前可用的mcp server列表为：{}\n### 2. ";
    }
}
