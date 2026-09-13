using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// PoC integration coverage for GitHub issue #229 (child #1 of epic #228).
/// These tests drive the Gateway / MetaSkill runtime against the in-process
/// <see cref="FakeNacosRouterMcpTools"/> fixture that emulates the live
/// Nacos MCP Router contract. They cover the four acceptance criteria that
/// do not require a live Nacos + Router environment.
///
/// T0 (probing the real Router contract at 127.0.0.1:8080 / 8848 / 9848 /
/// 8000) is documented as BLOCKED-EXTERNAL in docs/nacos-mcp-router.md.
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class NacosRouterIntegrationTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    [Fact]
    public void FakeNacosRouterMcpTools_SearchMcpServer_ReturnsTopFiveByTokenOverlap()
    {
        var fixture = new FakeNacosRouterMcpTools();

        var payload = fixture.SearchMcpServer("weather forecast", limit: 10);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Assert.Equal("weather forecast", root.GetProperty("query").GetString());
        Assert.Equal(5, root.GetProperty("limit").GetInt32());
        Assert.Equal(8, root.GetProperty("total_candidates").GetInt32());

        var candidates = root.GetProperty("candidates").EnumerateArray()
            .ToList();
        Assert.Equal(5, candidates.Count);

        // The first match should mention weather (it appears in the catalog
        // description), and scores should be non-increasing.
        var previousScore = double.MaxValue;
        foreach (var entry in candidates)
        {
            var score = entry.GetProperty("score").GetDouble();
            Assert.True(score <= previousScore,
                $"Scores must be non-increasing; got {score} after {previousScore}.");
            previousScore = score;
        }

        var firstName = candidates[0].GetProperty("name").GetString();
        Assert.Contains("weather", firstName!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FakeNacosRouterMcpTools_UseTool_WithoutAdd_ThrowsAndBumpsFallback()
    {
        var fixture = new FakeNacosRouterMcpTools();

        var ex = Assert.Throws<InvalidOperationException>(
            () => fixture.UseTool("weather-mcp", "get_current_weather", "{}"));
        Assert.Contains("not added", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Bindings);
        Assert.Equal(0, fixture.SuccessfulUseToolCalls);
    }

    [Fact]
    public void FakeNacosRouterMcpTools_AddThenUse_ReturnsStructuredPayload()
    {
        var fixture = new FakeNacosRouterMcpTools();

        var added = fixture.AddMcpServer("weather-mcp");
        using (var addDoc = JsonDocument.Parse(added))
        {
            Assert.Equal("weather-mcp", addDoc.RootElement.GetProperty("mcp_server_name").GetString());
            Assert.Equal(FakeNacosRouterMcpTools.DefaultToolName,
                addDoc.RootElement.GetProperty("tool_name").GetString());
            Assert.Equal("added", addDoc.RootElement.GetProperty("status").GetString());
        }

        var used = fixture.UseTool("weather-mcp", FakeNacosRouterMcpTools.DefaultToolName,
            "{\"city\":\"Beijing\"}");
        using var useDoc = JsonDocument.Parse(used);
        Assert.Equal("weather-mcp", useDoc.RootElement.GetProperty("mcp_server_name").GetString());
        Assert.Equal(FakeNacosRouterMcpTools.DefaultToolName,
            useDoc.RootElement.GetProperty("tool_name").GetString());
        Assert.Equal("Beijing",
            useDoc.RootElement.GetProperty("params_received").GetProperty("city").GetString());

        Assert.Equal(1, fixture.SuccessfulUseToolCalls);
        Assert.Single(fixture.Bindings);
    }

    [Fact]
    public async Task RegistryAgainstFakeRouter_DiscoversExactlyThreeNacosRouterTools()
    {
        var (routerUrl, router) = await StartFakeRouterAsync();
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        using var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["nacos-mcp-router"] = new()
                    {
                        Enabled = true,
                        Transport = "http",
                        Url = routerUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        // Acceptance criterion #1: exactly three `nacos-mcp-router_*` tools
        // are registered. The registry prefixes every tool with the server
        // id (e.g. `nacos-mcp-router.` → `nacos-mcp-router_`), so the local
        // names pick up the hyphenated server id verbatim. Acceptance
        // criterion #4 (no "overwriting") is also covered: re-calling
        // RegisterToolsAsync must not duplicate the entries.
        var routerToolNames = nativeRegistry.Tools
            .Where(t => t.Name.StartsWith("nacos-mcp-router_", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();

        Assert.Equal(3, routerToolNames.Count);
        Assert.Contains("nacos-mcp-router_search_mcp_server", routerToolNames);
        Assert.Contains("nacos-mcp-router_add_mcp_server", routerToolNames);
        Assert.Contains("nacos-mcp-router_use_tool", routerToolNames);

        // Re-registering should be a no-op (the registry uses a `_registered`
        // guard), so the tool count must still be 3 — no duplicates.
        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);
        routerToolNames = nativeRegistry.Tools
            .Where(t => t.Name.StartsWith("nacos-mcp-router_", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToList();
        Assert.Equal(3, routerToolNames.Count);
    }

    [Fact]
    public async Task MetaSkill_FullHappyPath_BindQueryAnswer_AllStepsCompleted()
    {
        // Acceptance criterion #2: the PoC MetaSkill runs through the DAG
        // bind → query → answer end-to-end with all steps `completed` and
        // emits a structured final_text.
        var (routerUrl, router) = await StartFakeRouterAsync();
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        using var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["nacos-mcp-router"] = new()
                    {
                        Enabled = true,
                        Transport = "http",
                        Url = routerUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tools = nativeRegistry.Tools.ToList();
        // emit_text is a built-in AgentRuntime tool that backs MetaSkill
        // tool_call steps. Include it explicitly so the query_fallback
        // step can dispatch to it when on_failure fires.
        tools.Add(new OpenClaw.Agent.Tools.EmitTextTool());
        var skill = BuildWeatherSkill();

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));

        var runtime = new AgentRuntime(
            chatClient,
            tools,
            Substitute.For<IMemoryStore>(),
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            skills: [skill]);

        var session = new Session { Id = "meta-sess-happy", SenderId = "user1", ChannelId = "test-channel" };
        var result = await InvokeMetaSkillAsync(runtime, session, skill.Name, "Beijing",
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result);
        var byStep = LatestStepResults(session);
        Assert.Equal(ToolResultStatuses.Completed, byStep["bind"].Status);
        Assert.Equal(ToolResultStatuses.Completed, byStep["query"].Status);
        Assert.Equal(ToolResultStatuses.Completed, byStep["answer"].Status);

        Assert.Single(router.Bindings);
        Assert.Equal(1, router.SuccessfulUseToolCalls);
    }

    [Fact]
    public async Task MetaSkill_OutputContractFailure_TriggersOnFailureFallbackBranch()
    {
        // Acceptance criterion #3 (adapted for the MCP server transport):
        // the upstream `McpNativeTool` translates any RPC failure into a
        // string result, so to exercise the MetaSkill `on_failure` path
        // we attach an `OutputContract` to the query step that the router's
        // happy-path payload does not satisfy. The runtime then marks the
        // step as Failed, activates the on_failure branch, and the
        // fallback `emit_text` step runs to completion.
        var (routerUrl, router) = await StartFakeRouterAsync();
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        using var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["nacos-mcp-router"] = new()
                    {
                        Enabled = true,
                        Transport = "http",
                        Url = routerUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tools = nativeRegistry.Tools.ToList();
        // emit_text is a built-in AgentRuntime tool that backs MetaSkill
        // tool_call steps. Include it explicitly so the query_fallback
        // step can dispatch to it.
        tools.Add(new OpenClaw.Agent.Tools.EmitTextTool());
        // Use a contract-enforcing variant: the fixture's response has no
        // `expected_runtime_field`, so the validation will fail.
        var skill = BuildWeatherSkillWithOutputContract();

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "FALLBACK_REPLY")));

        var runtime = new AgentRuntime(
            chatClient,
            tools,
            Substitute.For<IMemoryStore>(),
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            skills: [skill]);

        var session = new Session { Id = "meta-sess-failure", SenderId = "user1", ChannelId = "test-channel" };
        var result = await InvokeMetaSkillAsync(runtime, session, skill.Name, "Beijing",
            TestContext.Current.CancellationToken);

        var byStep = LatestStepResults(session);

        // The query step should have failed (status Failed) with a non-null
        // failure code; the on_failure branch (query_fallback) should have
        // executed and emitted its text.
        Assert.Equal(ToolResultStatuses.Failed, byStep["query"].Status);
        Assert.False(string.IsNullOrWhiteSpace(byStep["query"].FailureCode),
            "Failure step must report a failure_code.");
        Assert.Equal(ToolResultStatuses.Completed, byStep["query_fallback"].Status);

        // The runtime still produces a final answer because the answer
        // step depends on `query`, and the failure_aliases mechanism
        // mirrors the fallback output back into `outputs.query`.
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Single(router.Bindings);
    }

    [Fact]
    public async Task MetaSkill_HappyPath_ParsesTopFiveSearchCandidates()
    {
        // Acceptance criterion for the explore skill: the search step
        // returns a JSON payload with up to 5 candidates that includes
        // the weather server.
        var (routerUrl, _) = await StartFakeRouterAsync();
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());
        using var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["nacos-mcp-router"] = new()
                    {
                        Enabled = true,
                        Transport = "http",
                        Url = routerUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tools = nativeRegistry.Tools.ToList();
        var searchTool = tools.Single(t => t.Name == "nacos-mcp-router_search_mcp_server");
        var raw = await searchTool.ExecuteAsync(
            "{\"query\":\"weather\",\"limit\":5}",
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(raw);
        var candidates = doc.RootElement.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(5, candidates.Count);
        Assert.Contains(candidates, c =>
            string.Equals(c.GetProperty("name").GetString(), "weather-mcp", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    private async Task<(string RouterUrl, FakeNacosRouterMcpTools Router)> StartFakeRouterAsync()
    {
        var fixture = new FakeNacosRouterMcpTools();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "nacos-mcp-router-mock",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "search_mcp_server",
                        Description = "Search the Nacos MCP catalog.",
                        InputSchema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["query"] = new { type = "string", description = "Free-text search query." },
                                ["limit"] = new { type = "integer", description = "Maximum number of candidates (default 5, hard cap 5)." }
                            },
                            required = new[] { "query" }
                        })
                    },
                    new Tool
                    {
                        Name = "add_mcp_server",
                        Description = "Bind a Nacos MCP server into the local session.",
                        InputSchema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["mcp_server_name"] = new { type = "string", description = "Logical Nacos MCP server name." },
                                ["tool_name"] = new { type = "string", description = "Optional explicit tool name." }
                            },
                            required = new[] { "mcp_server_name" }
                        })
                    },
                    new Tool
                    {
                        Name = "use_tool",
                        Description = "Invoke a tool on a previously added Nacos MCP server.",
                        InputSchema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["mcp_server_name"] = new { type = "string" },
                                ["tool_name"] = new { type = "string" },
                                ["params"] = new { type = "string", description = "JSON object with the tool's parameters." }
                            },
                            required = new[] { "mcp_server_name", "tool_name" }
                        })
                    }
                ]
            }))
            .WithCallToolHandler(CallToolHandler);

        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/mcp", fixture);
    }

    private static ValueTask<CallToolResult> CallToolHandler(
        RequestContext<CallToolRequestParams> context,
        CancellationToken ct)
    {
        var fixture = context.Services!.GetRequiredService<FakeNacosRouterMcpTools>();
        var name = context.Params?.Name;
        var args = context.Params?.Arguments;

        try
        {
            string result = name switch
            {
                "search_mcp_server" => fixture.SearchMcpServer(
                    ReadString(args, "query") ?? string.Empty,
                    ReadInt(args, "limit")),
                "add_mcp_server" => fixture.AddMcpServer(
                    ReadString(args, "mcp_server_name") ?? string.Empty,
                    ReadString(args, "tool_name")),
                "use_tool" => fixture.UseTool(
                    ReadString(args, "mcp_server_name") ?? string.Empty,
                    ReadString(args, "tool_name") ?? string.Empty,
                    ReadString(args, "params")),
                _ => throw new InvalidOperationException($"Unknown tool '{name}'.")
            };
            return ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = result }]
            });
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }]
            });
        }
    }

    private static string? ReadString(IDictionary<string, JsonElement>? args, string name)
    {
        if (args is null) return null;
        if (!args.TryGetValue(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        // For object/array/number values, serialize back to a JSON string so
        // the fixture's @params argument (which expects a string) receives a
        // valid payload.
        return element.GetRawText();
    }

    private static int? ReadInt(IDictionary<string, JsonElement>? args, string name)
    {
        if (args is null) return null;
        if (!args.TryGetValue(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.Number) return null;
        return element.TryGetInt32(out var value) ? value : null;
    }

    private static Dictionary<string, object> BuildSchema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var schema = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = properties.ToDictionary(
                p => p.Name,
                p => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = p.Type,
                    ["description"] = p.Description
                },
                StringComparer.Ordinal),
            ["required"] = properties.Where(p => p.Required).Select(p => p.Name).ToArray()
        };
        return schema;
    }

    private static SkillDefinition BuildWeatherSkill()
        => new()
        {
            Name = "nacos-router-weather",
            Description = "PoC Router weather flow",
            Instructions = "Bind and query the Nacos Router weather tool.",
            Location = "/skills/nacos-router-weather",
            Kind = SkillKind.Meta,
            FinalTextMode = "step:answer",
            Composition = new MetaSkillComposition
            {
                Steps =
                [
                    new MetaSkillStepDefinition
                    {
                        Id = "bind",
                        Kind = "tool_call",
                        Tool = "nacos-mcp-router_add_mcp_server",
                        ToolArgsJson = "{\"mcp_server_name\":\"weather-mcp\"}"
                    },
                    new MetaSkillStepDefinition
                    {
                        Id = "query",
                        Kind = "tool_call",
                        DependsOn = ["bind"],
                        Tool = "nacos-mcp-router_use_tool",
                        ToolArgsJson =
                            "{\"mcp_server_name\":\"weather-mcp\"," +
                            "\"tool_name\":\"" + FakeNacosRouterMcpTools.DefaultToolName + "\"," +
                            "\"params\":{\"city\":\"{{ input }}\"}}",
                        OnFailure = "query_fallback"
                    },
                    new MetaSkillStepDefinition
                    {
                        Id = "query_fallback",
                        Kind = "tool_call",
                        Tool = "emit_text",
                        ToolArgsJson = "{\"text\":\"weather-mcp 不可用（PoC fallback）\"}"
                    },
                    new MetaSkillStepDefinition
                    {
                        Id = "answer",
                        Kind = "llm_chat",
                        DependsOn = ["query"],
                        WithJson = "{\"text\":\"Summarize: {{ input }} {{ outputs.query }}\"}"
                    }
                ]
            }
        };

    private static SkillDefinition BuildWeatherSkillWithOutputContract()
    {
        var skill = BuildWeatherSkill();
        // The fixture's response has no `expected_runtime_field` — the
        // contract therefore fails validation, forcing the step into the
        // Failed state so the on_failure branch executes.
        var newSteps = skill.Composition!.Steps
            .Select(s =>
            {
                if (s.Id != "query") return s;
                return new MetaSkillStepDefinition
                {
                    Id = s.Id,
                    Kind = s.Kind,
                    Skill = s.Skill,
                    Tool = s.Tool,
                    SkillExecEntrypoint = s.SkillExecEntrypoint,
                    SkillExecArgs = s.SkillExecArgs,
                    SkillExecStdin = s.SkillExecStdin,
                    SkillExecCwd = s.SkillExecCwd,
                    SkillExecParseMode = s.SkillExecParseMode,
                    WithJson = s.WithJson,
                    When = s.When,
                    ToolArgsJson = s.ToolArgsJson,
                    ToolAllowlist = s.ToolAllowlist,
                    OutputChoices = s.OutputChoices,
                    Clarify = s.Clarify,
                    Routes = s.Routes,
                    DependsOn = s.DependsOn,
                    OnFailure = s.OnFailure,
                    TimeoutSeconds = s.TimeoutSeconds,
                    Retry = s.Retry,
                    OutputContract = new MetaStepOutputContract
                    {
                        Format = "json",
                        RequiredProperties = ["expected_runtime_field"]
                    },
                    Iterable = s.Iterable,
                    FanOutMaxConcurrency = s.FanOutMaxConcurrency,
                    FanOutTemplate = s.FanOutTemplate,
                    FanOutMergeMode = s.FanOutMergeMode
                };
            })
            .ToList();

        return new SkillDefinition
        {
            Name = skill.Name,
            Description = skill.Description,
            Instructions = skill.Instructions,
            Location = skill.Location,
            Source = skill.Source,
            Metadata = skill.Metadata,
            Kind = skill.Kind,
            Triggers = skill.Triggers,
            MetaPriority = skill.MetaPriority,
            FinalTextMode = skill.FinalTextMode,
            Composition = new MetaSkillComposition
            {
                ToolArgsJson = skill.Composition.ToolArgsJson,
                Steps = newSteps
            },
            UserInvocable = skill.UserInvocable,
            DisableModelInvocation = skill.DisableModelInvocation,
            CommandDispatch = skill.CommandDispatch,
            CommandTool = skill.CommandTool,
            CommandArgMode = skill.CommandArgMode,
            Resources = skill.Resources,
            ProjectionContracts = skill.ProjectionContracts,
            ArtifactContract = skill.ArtifactContract,
            ProjectionDiscovery = skill.ProjectionDiscovery
        };
    }

    private static async Task<string> InvokeMetaSkillAsync(
        AgentRuntime runtime,
        Session session,
        string skillName,
        string input,
        CancellationToken ct)
    {
        var method = typeof(AgentRuntime).GetMethod(
            "ExecuteMetaSkillAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(runtime, [session, skillName, input, ct]) as Task<string>;
        Assert.NotNull(task);
        return await task!;
    }

    private static Dictionary<string, SessionMetaStepResult> LatestStepResults(Session session)
    {
        var last = session.MetaRunHistory.LastOrDefault();
        Assert.NotNull(last);
        return last!.StepResults.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }
}