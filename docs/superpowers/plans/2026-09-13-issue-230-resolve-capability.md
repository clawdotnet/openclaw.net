# Issue #230 — Native `resolve_capability` Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a deterministic `resolve_capability` native tool that turns the model-driven three-step Router chain (search → add → use) into a single code-path call, returning a `binding { server, tool, schema }` for downstream DAG `use_tool` nodes — zero LLM round-trips per call.

**Architecture:** A new `ITool` implementation (`ResolveCapabilityTool`) lives in `OpenClaw.Agent` and accesses the Router client through `McpServerToolRegistry.GetClientByServerId` (already public at `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs:86`). The tool sequentially invokes `search_mcp_server` then `add_mcp_server` against the Router client and returns either a `binding` payload or a structured failure envelope. It **never** invokes `use_tool` — that stays the DAG node's responsibility. Search candidates are trimmed to `{name, description, score}` before being returned (TokenJuice pattern from the architecture doc).

**Tech Stack:** C# / .NET 10, `ModelContextProtocol` SDK 2.0, `OpenClaw.Agent`, `OpenClaw.Gateway` composition root, xUnit + NSubstitute.

**Spec:**
- Issue: https://github.com/clawdotnet/openclaw.net/issues/230
- Parent epic: https://github.com/clawdotnet/openclaw.net/issues/228 (Capability slots & dynamic binding)
- Architectural context: `docs/nacos-mcp-router.md` (already merged in PR #236) and `Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` (architecture doc §7.1)
- Wire-contract reference: `https://github.com/nacos-group/nacos-mcp-router-python/blob/0ee95f4f353d6f66184dafdb3e0ffd342c4edb09/src/nacos_mcp_router/router.py` (the pinned Router commit used by `FakeNacosRouterMcpTools`)

## Global Constraints

- **Tool surface**: `resolve_capability(intent)` where intent has `task_description` (required), `key_words` (optional comma-separated string), `selection_policy` (optional enum: `first` / `exact_name`).
- **Zero LLM round-trips per call** — the tool must not invoke any `IChatClient` or `ILlmExecutionService`; tests assert this via NSubstitute `ReceivedCalls()`.
- **Router wire-contract conformance** (per #229 review):
  - `search_mcp_server(task_description, key_words)` — `key_words` is a single comma-separated string, NOT a list.
  - `add_mcp_server(mcp_server_name)` — returns prose envelope; tool list is a JSON array embedded in text (no separate `IsError` flag for install/unhealthy failures).
  - `params` for downstream `use_tool` is a JSON-encoded STRING (verified in PR #236 follow-up `cc8f6df`); this tool returns the schema to the DAG node which is responsible for serialising params correctly.
- **Output trimming**: search-derived candidates exposed to the caller carry only `name`, `description`, `score`. The score comes from the upstream's deterministic candidate ordering (top-N); we assign `score = 1.0 / rank` so the field is monotonic but not invented.
- **Structured failure envelope** (returned as JSON, not thrown): `{ "failure_code": "no_candidates" | "all_adds_failed", "tried": [{"server": "..."}, ...] }`. The `failure_code` string is part of the public contract.
- **No `use_tool` proxy**: `ResolveCapabilityTool` must never call `use_tool`. The DAG node downstream of the resolver is what executes the bound tool.
- **Out of scope** (covered by sibling issues): binding cache (#232), capabilityRef schema (#231), ranking/version metadata beyond the rank-based score.
- **Pre-existing failures unrelated to this plan** (verified in commit `7db4d76` baseline):
  - `PluginCommandsTests.InstallPreparedDirectoryAsync_NativePluginDoesNotRunNpmLifecycleScripts` (no Node.js in sandbox).
  - `CompanionCanvasUiTests.Keyboard_PaletteAndComposer_PreserveDraftUntilSend` (Windows CRLF line ending).
  Both are acceptable to leave as pre-existing failures throughout this plan.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/OpenClaw.Core/Skills/Meta/CapabilityResolverTypes.cs` | DTOs: `ResolveCapabilityRequest`, `ResolveCapabilityBinding`, `ResolveCapabilityFailure`, `ResolveCapabilitySelectionPolicy` | Create |
| `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs` | `ITool` implementation; orchestrates search→add via Router MCP client | Create |
| `src/OpenClaw.Agent/Tools/RouterCandidateParser.cs` | Static helper that parses the upstream `search_mcp_server` prose envelope into a trimmed `IReadOnlyList<RouterCandidate>` | Create |
| `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs` | Register `ResolveCapabilityTool` in `CreateBuiltInTools` (line 139 area) | Modify |
| `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs` | Unit tests for the tool against `FakeNacosRouterMcpTools` and an in-process MCP server | Create |
| `src/OpenClaw.Tests/Tools/RouterCandidateParserTests.cs` | Unit tests for the prose-envelope parser | Create |
| `docs/nacos-mcp-router.md` | Add a "Capability resolver (issue #230)" subsection explaining the deterministic path | Modify |
| `eng/test_resolve_capability_contract.py` | Optional: smoke-gate script mirroring the desktop release pattern | Create |

---

## Task 1: Capability Resolver DTOs

**Files:**
- Create: `src/OpenClaw.Core/Skills/Meta/CapabilityResolverTypes.cs`
- Test: covered indirectly through Task 4 tests

**Interfaces:**
- Produces:
  - `public enum ResolveCapabilitySelectionPolicy { First, ExactName }`
  - `public sealed record ResolveCapabilityRequest(string TaskDescription, string? KeyWords, ResolveCapabilitySelectionPolicy SelectionPolicy);`
  - `public sealed record RouterCandidate(string Name, string Description, double Score);`
  - `public sealed record ResolveCapabilityBinding(string Server, string Tool, string Schema, IReadOnlyList<RouterCandidate> TriedCandidates);`
  - `public sealed record ResolveCapabilityFailure(string FailureCode, IReadOnlyList<RouterCandidate> TriedCandidates);`

- [ ] **Step 1: Create the DTO file**

Write to `src/OpenClaw.Core/Skills/Meta/CapabilityResolverTypes.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Skills.Meta;

[JsonConverter(typeof(JsonStringEnumConverter<ResolveCapabilitySelectionPolicy>))]
public enum ResolveCapabilitySelectionPolicy
{
    First = 0,
    ExactName = 1,
}

public sealed record ResolveCapabilityRequest(
    [property: JsonPropertyName("task_description")] string TaskDescription,
    [property: JsonPropertyName("key_words")] string? KeyWords,
    [property: JsonPropertyName("selection_policy")] ResolveCapabilitySelectionPolicy SelectionPolicy);

public sealed record RouterCandidate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("score")] double Score);

public sealed record ResolveCapabilityBinding(
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("tried")] IReadOnlyList<RouterCandidate> TriedCandidates);

public sealed record ResolveCapabilityFailure(
    [property: JsonPropertyName("failure_code")] string FailureCode,
    [property: JsonPropertyName("tried")] IReadOnlyList<RouterCandidate> TriedCandidates);

public static class ResolveCapabilityFailureCodes
{
    public const string NoCandidates = "no_candidates";
    public const string AllAddsFailed = "all_adds_failed";
    public const string SelectionPolicyNoMatch = "selection_policy_no_match";
    public const string RouterUnavailable = "router_unavailable";
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj --nologo`
Expected: Build succeeds, 0 errors. (DTOs compile standalone.)

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Skills/Meta/CapabilityResolverTypes.cs
git commit -m "feat(#230): add capability resolver DTOs"
```

---

## Task 2: Router Candidate Parser — Failing Test

**Files:**
- Create: `src/OpenClaw.Tests/Tools/RouterCandidateParserTests.cs`
- Create: `src/OpenClaw.Agent/Tools/RouterCandidateParser.cs` (will only contain a stub returning empty list)

**Interfaces:**
- Consumes: the upstream Router `search_mcp_server` prose envelope (string).
- Produces: `RouterCandidateParser.Parse(string prose)` returning `IReadOnlyList<RouterCandidate>` containing at most 5 entries, each with `name`, `description`, `score = 1.0 / rank`.

- [ ] **Step 1: Write the failing test**

Write to `src/OpenClaw.Tests/Tools/RouterCandidateParserTests.cs`:

```csharp
using System.Linq;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Skills.Meta;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class RouterCandidateParserTests
{
    private const string UpstreamEnvelope = """
        ## 获取weather city的步骤如下：
        ### 1. 当前可用的mcp server列表为：{"weather-mcp":{"name":"weather-mcp","description":"weather forecast"},"candidate-1":{"name":"candidate-1","description":"other"}}
        ### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server
        """;

    [Fact]
    public void Parse_UpstreamEnvelope_ReturnsRankedCandidates()
    {
        var parsed = RouterCandidateParser.Parse(UpstreamEnvelope);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("weather-mcp", parsed[0].Name);
        Assert.Equal("weather forecast", parsed[0].Description);
        Assert.Equal(1.0, parsed[0].Score); // rank 1 → 1/1
        Assert.Equal(0.5, parsed[1].Score); // rank 2 → 1/2
    }

    [Fact]
    public void Parse_CapsAtFiveEntries()
    {
        var six = string.Join(",", Enumerable.Range(0, 6).Select(i => $"\"k{i}\":{{\"name\":\"k{i}\",\"description\":\"d\"}}"));
        var prose = "## 获取t的步骤如下：\n### 1. 当前可用的mcp server列表为：{" + six + "}\n### 2. ...";
        var parsed = RouterCandidateParser.Parse(prose);
        Assert.Equal(5, parsed.Count);
    }

    [Fact]
    public void Parse_MalformedEnvelope_ReturnsEmpty()
    {
        var parsed = RouterCandidateParser.Parse("garbage with no JSON");
        Assert.Empty(parsed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~RouterCandidateParserTests"`
Expected: FAIL — `RouterCandidateParser` does not exist.

- [ ] **Step 3: Implement the parser**

Write to `src/OpenClaw.Agent/Tools/RouterCandidateParser.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Parses the prose envelope emitted by the upstream Nacos MCP Router's
/// <c>search_mcp_server</c> tool. The envelope wraps a JSON object keyed by
/// server name between two Chinese-language markers; this helper extracts the
/// JSON, drops everything else, and returns up to 5 candidates ranked by their
/// position in the dictionary.
/// </summary>
public static class RouterCandidateParser
{
    private const int MaxCandidates = 5;
    private static readonly Regex JsonBlock = new(
        @"### 1\. 当前可用的mcp server列表为：(\{.*?\})\n### 2\.",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<RouterCandidate> Parse(string prose)
    {
        if (string.IsNullOrWhiteSpace(prose)) return Array.Empty<RouterCandidate>();

        var match = JsonBlock.Match(prose);
        if (!match.Success) return Array.Empty<RouterCandidate>();

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<RouterCandidate>();

            var results = new List<RouterCandidate>(MaxCandidates);
            var rank = 1;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (rank > MaxCandidates) break;
                if (property.Value.ValueKind != JsonValueKind.Object) { rank++; continue; }

                string name = property.Name;
                string description = "";
                if (property.Value.TryGetProperty("description", out var descNode)
                    && descNode.ValueKind == JsonValueKind.String)
                {
                    description = descNode.GetString() ?? "";
                }

                results.Add(new RouterCandidate(name, description, 1.0 / rank));
                rank++;
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<RouterCandidate>();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~RouterCandidateParserTests"`
Expected: PASS — 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Tools/RouterCandidateParser.cs src/OpenClaw.Tests/Tools/RouterCandidateParserTests.cs
git commit -m "feat(#230): add RouterCandidateParser for upstream search prose envelope"
```

---

## Task 3: ResolveCapabilityTool — Failing Test (Happy Path)

**Files:**
- Create: `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs`
- Create: `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs` (stub throwing `NotImplementedException` for now)

**Interfaces:**
- `ResolveCapabilityTool` will later consume `McpServerToolRegistry` (DI) and use `GetClientByServerId("nacos-mcp-router")`.
- `ResolveCapabilityTool.Name` must be `"resolve_capability"`.
- `ResolveCapabilityTool.ParameterSchema` must declare three fields: `task_description` (required string), `key_words` (optional string), `selection_policy` (optional enum string `"first"` | `"exact_name"`).
- `ResolveCapabilityTool.ExecuteAsync(argsJson, ct)` returns JSON.

- [ ] **Step 1: Write the failing test**

Write to `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs` (only the happy-path test for now; we add the failure tests in Tasks 5 & 6):

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Skills.Meta;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class ResolveCapabilityToolTests
{
    [Fact]
    public async Task HappyPath_ReturnsBinding_AndCallsSearchThenAddOnce()
    {
        var (tool, registry, state) = await BuildAsync();
        var args = """
            {"task_description":"weather city","key_words":"weather,city","selection_policy":"first"}
            """;
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("weather-mcp", root.GetProperty("server").GetString());
        Assert.Equal("get_weather", root.GetProperty("tool").GetString());
        Assert.Equal(2, root.GetProperty("tried").GetArrayLength());

        // No use_tool call ever happens in the resolver.
        Assert.DoesNotContain(state.Calls, c => c.StartsWith("use:"));
        // search then add for the chosen candidate.
        Assert.Contains("search", state.Calls);
        Assert.Contains("add:weather-mcp", state.Calls);
        registry.Received(1).GetClientByServerId("nacos-mcp-router");
    }

    private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state)> BuildAsync()
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FakeNacosRouterMcpTools>();
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = Substitute.For<McpServerToolRegistry>(
            new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        registry.GetClientByServerId("nacos-mcp-router").Returns(_ => /* server.Urls[0] */ null);
        // … see Step 3 for the full client wiring; this placeholder is updated in Step 3.
        var tool = new ResolveCapabilityTool(registry);
        return (tool, registry, state);
    }
}
```

Note: `BuildAsync` is incomplete above on purpose — it gets fleshed out when the implementation lands in Step 3 of the next task. We keep it here as the failing-test skeleton.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests"`
Expected: FAIL — `ResolveCapabilityTool` does not exist.

- [ ] **Step 3: Stub the tool**

Write to `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs`:

```csharp
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

public sealed class ResolveCapabilityTool : ITool
{
    private readonly McpServerToolRegistry _registry;

    public ResolveCapabilityTool(McpServerToolRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "resolve_capability";

    public string Description =>
        "Resolve an intent into a Router-bound server+tool+schema. Zero LLM round-trips.";

    public string ParameterSchema =>
        "{\"type\":\"object\",\"required\":[\"task_description\"],\"properties\":{" +
        "\"task_description\":{\"type\":\"string\"}," +
        "\"key_words\":{\"type\":\"string\"}," +
        "\"selection_policy\":{\"type\":\"string\",\"enum\":[\"first\",\"exact_name\"]}" +
        "}}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => throw new NotImplementedException("ResolveCapabilityTool not yet implemented — see Task 4.");
}
```

- [ ] **Step 4: Re-run test, expect it still fails on `NotImplementedException`**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests.HappyPath"`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 5: Commit (TDD red)**

```bash
git add src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs
git commit -m "test(#230): red — happy path for resolve_capability"
```

---

## Task 4: ResolveCapabilityTool — Minimal Implementation

**Files:**
- Modify: `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs`
- Modify: `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs` (wire the `BuildAsync` helper to a real Router client)

**Interfaces:**
- `ResolveCapabilityTool.ExecuteAsync` parses `argumentsJson` into a `ResolveCapabilityRequest`, then:
  1. Pulls `McpClient` via `_registry.GetClientByServerId("nacos-mcp-router")`.
  2. If null → returns JSON `{"failure_code":"router_unavailable","tried":[]}`.
  3. Calls `search_mcp_server(task_description, key_words)` via the MCP client.
  4. Parses the response with `RouterCandidateParser.Parse`.
  5. If empty → returns `{"failure_code":"no_candidates","tried":[]}`.
  6. Iterates candidates in order; for `First` policy it tries only the first; for `ExactName` policy it tries until the candidate's name matches `task_description` exactly (case-insensitive) OR exhausts the list.
  7. For each chosen candidate it calls `add_mcp_server(mcp_server_name)`.
  8. If the add response contains `"安装完成"` (upstream success marker) → returns `{"server","tool","schema","tried"}` where `tool` and `schema` are extracted from the prose tool-list JSON.
  9. If all adds fail → returns `{"failure_code":"all_adds_failed","tried":[...]}`.

- [ ] **Step 1: Wire the test's BuildAsync to use a real MCP client**

Replace the placeholder in `ResolveCapabilityToolTests.cs` `BuildAsync`:

```csharp
var client = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(server.Urls.Single() + "/mcp"),
        Name = "nacos-mcp-router",
    }),
    cancellationToken: TestContext.Current.CancellationToken);
registry.GetClientByServerId("nacos-mcp-router").Returns(client);
```

(Add `using ModelContextProtocol.Client;` at the top of the test file.)

- [ ] **Step 2: Implement the tool body**

Replace the `ExecuteAsync` stub in `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs` with:

```csharp
public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
{
    var request = ParseRequest(argumentsJson);
    var client = _registry.GetClientByServerId("nacos-mcp-router");
    if (client is null)
        return JsonFail(ResolveCapabilityFailureCodes.RouterUnavailable, Array.Empty<RouterCandidate>());

    var searchText = await CallToolAsStringAsync(client, "search_mcp_server",
        new Dictionary<string, object?>
        {
            ["task_description"] = request.TaskDescription,
            ["key_words"] = request.KeyWords ?? "",
        }, ct);

    var candidates = RouterCandidateParser.Parse(searchText);
    if (candidates.Count == 0)
        return JsonFail(ResolveCapabilityFailureCodes.NoCandidates, Array.Empty<RouterCandidate>());

    var tried = new List<RouterCandidate>();
    foreach (var candidate in PickCandidates(candidates, request))
    {
        tried.Add(candidate);
        var addText = await CallToolAsStringAsync(client, "add_mcp_server",
            new Dictionary<string, object?> { ["mcp_server_name"] = candidate.Name }, ct);

        if (TryExtractTool(addText, out var toolName, out var schema))
            return JsonBinding(candidate, toolName, schema, tried);
    }

    return JsonFail(ResolveCapabilityFailureCodes.AllAddsFailed, tried);
}

private static IEnumerable<RouterCandidate> PickCandidates(
    IReadOnlyList<RouterCandidate> candidates,
    ResolveCapabilityRequest request)
{
    if (request.SelectionPolicy == ResolveCapabilitySelectionPolicy.ExactName)
    {
        return candidates.Where(c =>
            string.Equals(c.Name, request.TaskDescription, StringComparison.OrdinalIgnoreCase));
    }
    return new[] { candidates[0] };
}

private static bool TryExtractTool(string addResponse, out string toolName, out string schema)
{
    toolName = "";
    schema = "";
    if (string.IsNullOrEmpty(addResponse) || !addResponse.Contains("安装完成"))
        return false;

    var match = System.Text.RegularExpressions.Regex.Match(
        addResponse, @"tool 列表为: (\[.*?\])");
    if (!match.Success) return false;

    try
    {
        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return false;
        toolName = first.GetProperty("name").GetString() ?? "";
        schema = first.TryGetProperty("inputSchema", out var s)
            ? s.GetRawText()
            : "{}";
        return !string.IsNullOrEmpty(toolName);
    }
    catch (JsonException) { return false; }
}
```

Add the helper methods (`ParseRequest`, `CallToolAsStringAsync`, `JsonFail`, `JsonBinding`) at the bottom of the class:

```csharp
private static ResolveCapabilityRequest ParseRequest(string argumentsJson)
{
    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
    var root = doc.RootElement;
    var task = root.GetProperty("task_description").GetString()
        ?? throw new ArgumentException("task_description is required");
    string? keywords = root.TryGetProperty("key_words", out var kw) ? kw.GetString() : null;
    var policy = ResolveCapabilitySelectionPolicy.First;
    if (root.TryGetProperty("selection_policy", out var sp) && sp.ValueKind == JsonValueKind.String)
    {
        policy = sp.GetString() switch
        {
            "exact_name" => ResolveCapabilitySelectionPolicy.ExactName,
            _ => ResolveCapabilitySelectionPolicy.First,
        };
    }
    return new ResolveCapabilityRequest(task, keywords, policy);
}

private static async Task<string> CallToolAsStringAsync(
    McpClient client, string toolName, Dictionary<string, object?> args, CancellationToken ct)
{
    var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
        RequestMethods.ToolsCall,
        new CallToolRequestParams { Name = toolName, Arguments = args!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value) },
        cancellationToken: ct);
    var parts = new List<string>();
    foreach (var c in response.Content ?? []) if (c is TextContentBlock t) parts.Add(t.Text);
    return string.Join("\n", parts);
}

private static string JsonFail(string code, IReadOnlyList<RouterCandidate> tried) =>
    JsonSerializer.Serialize(new ResolveCapabilityFailure(code, tried));

private static string JsonBinding(RouterCandidate chosen, string tool, string schema, IReadOnlyList<RouterCandidate> tried) =>
    JsonSerializer.Serialize(new ResolveCapabilityBinding(chosen.Name, tool, schema, tried));
```

Add `using System.Text.Json;` and `using ModelContextProtocol.Protocol;` and `using ModelContextProtocol.Client;` at the top of the file.

- [ ] **Step 3: Run the happy-path test**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests.HappyPath"`
Expected: PASS.

- [ ] **Step 4: Run the parser tests as a regression sweep**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~RouterCandidateParserTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs
git commit -m "feat(#230): implement resolve_capability tool happy path"
```

---

## Task 5: ResolveCapabilityTool — Empty / All-Failed Failure Tests

**Files:**
- Modify: `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs` (add 2 tests; no production code change)

- [ ] **Step 1: Add the no-candidates test**

Append to `ResolveCapabilityToolTests.cs`:

```csharp
[Fact]
public async Task EmptySearch_ReturnsNoCandidatesFailure()
{
    var (tool, _, state) = await BuildEmptyAsync();
    var args = """{"task_description":"absurdly_unlikely_intent"}""";
    var result = await tool.ExecuteAsync(args, CancellationToken.None);

    using var doc = JsonDocument.Parse(result);
    Assert.Equal("no_candidates", doc.RootElement.GetProperty("failure_code").GetString());
    Assert.Empty(state.Calls.FindAll(c => c.StartsWith("add:")));
}
```

Add `BuildEmptyAsync` next to `BuildAsync`:

```csharp
private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state)> BuildEmptyAsync()
{
    var state = new NacosRouterFixtureState();
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(state);
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<EmptyFakeNacosRouter>();
    await using var server = builder.Build();
    server.MapMcp("/mcp");
    await server.StartAsync(TestContext.Current.CancellationToken);
    var client = await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Urls.Single() + "/mcp"),
            Name = "nacos-mcp-router",
        }),
        cancellationToken: TestContext.Current.CancellationToken);
    var registry = Substitute.For<McpServerToolRegistry>(
        new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
    registry.GetClientByServerId("nacos-mcp-router").Returns(client);
    return (new ResolveCapabilityTool(registry), registry, state);
}

[McpServerToolType]
private sealed class EmptyFakeNacosRouter
{
    [McpServerTool(Name = "search_mcp_server")]
    public string Search(string task_description, string key_words) => "### 1. 当前可用的mcp server列表为：{}\n### 2. ";
}
```

- [ ] **Step 2: Add the all-adds-failed test**

```csharp
[Fact]
public async Task AllAddsFail_ReturnsAllAddsFailed_WithTriedList()
{
    var (tool, _, _) = await BuildAllFailAsync();
    var args = """{"task_description":"weather city","key_words":"weather"}""";
    var result = await tool.ExecuteAsync(args, CancellationToken.None);

    using var doc = JsonDocument.Parse(result);
    Assert.Equal("all_adds_failed", doc.RootElement.GetProperty("failure_code").GetString());
    var tried = doc.RootElement.GetProperty("tried").EnumerateArray().ToList();
    Assert.NotEmpty(tried);
}
```

Add `BuildAllFailAsync`:

```csharp
private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state)> BuildAllFailAsync()
{
    var state = new NacosRouterFixtureState { FailUse = false };
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(state);
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<FakeNacosRouterMcpTools>();
    await using var server = builder.Build();
    server.MapMcp("/mcp");
    await server.StartAsync(TestContext.Current.CancellationToken);
    var client = await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Urls.Single() + "/mcp"),
            Name = "nacos-mcp-router",
        }),
        cancellationToken: TestContext.Current.CancellationToken);
    var registry = Substitute.For<McpServerToolRegistry>(
        new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
    registry.GetClientByServerId("nacos-mcp-router").Returns(client);
    return (new ResolveCapabilityTool(registry), registry, state);
}
```

(Note: the existing `FakeNacosRouterMcpTools` always returns success on `add`. To exercise `all_adds_failed` we add a new fixture.)

Create `src/OpenClaw.Tests/Tools/AllFailFakeNacosRouter.cs`:

```csharp
using ModelContextProtocol.Server;

namespace OpenClaw.Tests.Tools;

[McpServerToolType]
public sealed class AllFailFakeNacosRouter
{
    [McpServerTool(Name = "search_mcp_server")]
    public string Search(string task_description, string key_words) =>
        "## 获取weather city的步骤如下：\n" +
        "### 1. 当前可用的mcp server列表为：{\"weather-mcp\":{\"name\":\"weather-mcp\",\"description\":\"weather\"}}\n" +
        "### 2. 从当前可用的mcp server列表中选择你需要的mcp server调add_mcp_server工具安装mcp server";

    [McpServerTool(Name = "add_mcp_server")]
    public string Add(string mcp_server_name) =>
        "weather-mcp is not found, use search_mcp_server to get mcp servers";
}
```

Update `BuildAllFailAsync` to use `AllFailFakeNacosRouter`:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<AllFailFakeNacosRouter>();
```

- [ ] **Step 3: Run all ResolveCapabilityTool tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests"`
Expected: PASS — 3 tests.

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs src/OpenClaw.Tests/Tools/AllFailFakeNacosRouter.cs
git commit -m "test(#230): failure-path coverage for resolve_capability"
```

---

## Task 6: ResolveCapabilityTool — Zero-LLM-Roundtrip Integration Test

**Files:**
- Modify: `src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs` (add one integration test that drives the AgentRuntime meta-skill path and asserts no IChatClient / ILlmExecutionService calls)

**Interfaces:**
- Mirrors the pattern used by `NacosRouterIntegrationTests.StaticDemo_UsesOnlyThreeRouterTools_AndHandlesProtocolFailures` but with the new `resolve_capability` tool substituting for the direct `bind`/`query` steps.
- Asserts: after invoking a meta-skill that uses `resolve_capability`, `chat.ReceivedCalls()` and `execution.ReceivedCalls()` are empty.

- [ ] **Step 1: Add the integration test**

```csharp
[Fact]
public async Task MetaSkill_ResolveCapabilityOnly_ZeroLlmRoundtrips()
{
    var state = new NacosRouterFixtureState();
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(state);
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<FakeNacosRouterMcpTools>();
    await using var server = builder.Build();
    server.MapMcp("/mcp");
    await server.StartAsync(TestContext.Current.CancellationToken);

    var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
    var reload = await registry.ReloadWorkspaceServersAsync(
        new Dictionary<string, McpServerConfig>
        {
            ["nacos-mcp-router"] = new()
            {
                Enabled = true, Transport = "http",
                Url = server.Urls.Single() + "/mcp",
                ToolNamePrefix = "nacos_mcp_router_",
            },
        }, TestContext.Current.CancellationToken);

    var resolveTool = new ResolveCapabilityTool(registry);
    var tools = reload.AddedTools.Append<ITool>(resolveTool).Append(new EmitTextTool()).ToArray();

    var chat = Substitute.For<IChatClient>();
    var execution = Substitute.For<ILlmExecutionService>();
    var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    using var memory = new FileMemoryStore(root, 4);
    var runtime = new AgentRuntime(chat, tools, memory, new GatewayConfig().Llm, maxHistoryTurns: 5);

    var session = new Session { Id = "resolve-cap-test", SenderId = "test", ChannelId = "test" };
    var skill = new SkillDefinition
    {
        Name = "resolve-cap-fixture",
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
                    ToolArgsJson = """{"text":"{{ outputs.resolve | default('(empty)') }}"}""",
                    DependsOn = new[] { "resolve" },
                },
            },
        },
    };

    var method = typeof(AgentRuntime).GetMethod("ExecuteMetaSkillAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var result = await (Task<string>)method.Invoke(runtime,
        [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;

    Assert.Contains("weather-mcp", result);
    Assert.Empty(chat.ReceivedCalls());
    Assert.Empty(execution.ReceivedCalls());

    if (Directory.Exists(root)) Directory.Delete(root, true);
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests.MetaSkill_ResolveCapabilityOnly_ZeroLlmRoundtrips"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs
git commit -m "test(#230): assert zero-LLM-roundtrip for resolve_capability in meta-skill"
```

---

## Task 7: Wire `ResolveCapabilityTool` into the Gateway

**Files:**
- Modify: `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs:14-26`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs:127-180`

**Interfaces:**
- The tool must appear in `services.GetRequiredService<NativePluginRegistry>().RegisteredToolNames` (acceptance criterion #1 of issue #230).

- [ ] **Step 1: Register the tool in NativePluginRegistry inside `ToolServicesExtensions.AddOpenClawToolServices`**

After line 17 in `ToolServicesExtensions.cs`, add:

```csharp
registry.RegisterExternalTool(
    new ResolveCapabilityTool(sp.GetRequiredService<McpServerToolRegistry>()),
    pluginId: "agent.resolve-capability");
```

(Add `using OpenClaw.Agent.Tools;` at the top. `McpServerToolRegistry` is the DI-registered service type — distinct from the `RuntimeServices.McpRegistry` instance property used in Task 7 Step 2.)

- [ ] **Step 2: Register the tool in `CreateBuiltInTools`**

In `RuntimeInitializationExtensions.RuntimeFactories.cs`, locate the `var tools = new List<ITool> { ... }` block (around line 139). Add a new entry:

```csharp
new ResolveCapabilityTool(services.McpRegistry),
```

(Add `using OpenClaw.Agent.Tools;` at the top.)

Note: the property on `RuntimeServices` is `McpRegistry` (not `McpServerToolRegistry`) — verified at `RuntimeInitializationExtensions.CompositionStages.cs:622`.

- [ ] **Step 3: Build**

Run: `dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj --nologo`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Add a smoke test that the tool appears in the registered names**

Append to `ResolveCapabilityToolTests.cs`:

```csharp
[Fact]
public void ToolName_IsResolveCapability()
{
    Assert.Equal("resolve_capability", new ResolveCapabilityTool(
        Substitute.For<McpServerToolRegistry>()).Name);
}

[Fact]
public void ParameterSchema_DeclaresIntentFields()
{
    var schema = new ResolveCapabilityTool(
        Substitute.For<McpServerToolRegistry>()).ParameterSchema;
    using var doc = JsonDocument.Parse(schema);
    var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
    Assert.Contains("task_description", required);
    Assert.Contains("selection_policy", doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
}
```

- [ ] **Step 5: Run all ResolveCapabilityTool tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~ResolveCapabilityToolTests"`
Expected: PASS — 6 tests.

- [ ] **Step 6: Run the full suite minus the two pre-existing failures**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName!~InstallPreparedDirectoryAsync_NativePluginDoesNotRunNpmLifecycleScripts&FullyQualifiedName!~Keyboard_PaletteAndComposer_PreserveDraftUntilSend"`
Expected: 0 failures, 1 skipped (live Router).

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs src/OpenClaw.Tests/Tools/ResolveCapabilityToolTests.cs
git commit -m "feat(#230): register resolve_capability in gateway composition"
```

---

## Task 8: Documentation Update

**Files:**
- Modify: `docs/nacos-mcp-router.md` (append a "Capability resolver (issue #230)" subsection before "Remaining live acceptance")

- [ ] **Step 1: Add the subsection**

Append after the "Reproducible local checks" section in `docs/nacos-mcp-router.md`:

```markdown
## Capability resolver (issue #230)

The `resolve_capability` native tool turns the model-driven three-step
chain into a deterministic code path. The model only needs to emit an
intent; binding happens in code.

Inputs:

- `task_description` (required) — the same shape the Router `search_mcp_server` accepts.
- `key_words` (optional) — comma-separated string, same wire shape as the Router.
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`).

Output (success):

```json
{
  "server": "weather-mcp",
  "tool": "get_weather",
  "schema": { "type": "object", "properties": { "city": {"type":"string"} }, "required": ["city"] },
  "tried": [
    {"name": "weather-mcp", "description": "...", "score": 1.0},
    {"name": "candidate-1", "description": "...", "score": 0.5}
  ]
}
```

Output (failure — JSON, not exception):

```json
{ "failure_code": "no_candidates", "tried": [] }
{ "failure_code": "all_adds_failed", "tried": [{"name":"...","description":"...","score":0.5}, ...] }
{ "failure_code": "router_unavailable", "tried": [] }
```

Behaviour contract:

1. The tool never invokes `use_tool`; downstream DAG nodes execute the bound tool.
2. The tool never calls any LLM; round-trips are zero (test: `chat.ReceivedCalls()` empty).
3. The tool never throws on Router prose failures; they are normalised to a `failure_code`.
4. `tried` lists the candidates the resolver actually attempted to add, not all returned candidates.
5. `score` is `1.0 / rank` so the field is monotonic in the upstream's deterministic top-N ordering (upstream does not return scores; this avoids fabricating them).
```

- [ ] **Step 2: Build docs site (if applicable)**

Run: `dotnet build docs/` only if the docs site is part of the .NET build graph; otherwise verify the file is referenced by `DocsConsistencyTests`.

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo --filter "FullyQualifiedName~DocsConsistencyTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/nacos-mcp-router.md
git commit -m "docs(#230): document resolve_capability behaviour and contract"
```

---

## Self-Review Checklist (run before declaring done)

- [ ] Every spec acceptance criterion in issue #230 maps to a passing test:
  - AC1 (tool in runtime table, 3-field schema) → Task 7 Step 4
  - AC2 (zero LLM round-trips, harness-asserted) → Task 6
  - AC3 (structured failure, no unhandled exceptions) → Task 5
  - AC4 (search candidates carry only name/description/score) → Task 4 + Task 2 (parser omits everything else)
  - AC5 (`dotnet test` green) → Task 7 Step 6
- [ ] No `TBD` / `TODO` / placeholder strings remain in any production file.
- [ ] Type names are consistent across tasks: `ResolveCapabilityTool`, `ResolveCapabilityRequest`, `RouterCandidate`, `ResolveCapabilityBinding`, `ResolveCapabilityFailure`, `RouterCandidateParser`, `NacosRouterFixtureState`, `FakeNacosRouterMcpTools`, `AllFailFakeNacosRouter`, `EmptyFakeNacosRouter`.
- [ ] No `use_tool` call exists anywhere in `ResolveCapabilityTool` or its helpers.
- [ ] `tried` lists only candidates the resolver actually attempted, not all upstream results.
- [ ] All commits compile and the cumulative test suite minus the two pre-existing failures is green.

## Out-of-Scope Reminders

- Binding cache (#232) — sibling issue; do not add caching here.
- `capabilityRef` schema (#231) — sibling issue; the resolver returns `schema` as raw JSON, not as a typed `capabilityRef`.
- Ranking beyond rank-based score — sibling issue #233 (fallback/failover) covers this.
- Observability (#234) — sibling issue; the resolver does NOT write to `meta-runs` here.
