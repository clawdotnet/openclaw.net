# Capability Node-Level Degradation and Retry (#233) Implementation Plan

> Historical implementation plan. Retained task snippets and checkboxes are non-normative; the implementation has since been refactored. See [the current capability-resolution contract](../../capability-resolution.md) for supported behavior and remaining live/NativeAOT acceptance.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement node-level degradation and fault tolerance for capability slots: fallback routing on empty search, Top-5 candidate rotation on add failure, and use_tool retry with backoff and circuit-style exhaustion.

**Architecture:** The machinery is mostly in place from #230/#231 plus the generic step retry policy. The one production change is in the shared resolver core: the `first` selection policy currently picks only `candidates[0]`, which makes the existing add-rotation loop dead code for the default policy. Everything else is pinning tests (unit + e2e across both runtimes), a new retry-capable example skill, and docs.

**Tech Stack:** net10.0 / C# 14 / xUnit / NSubstitute / ModelContextProtocol (in-process HTTP MCP fixture, no external Router needed) / warnings-as-errors.

**Spec:** https://github.com/clawdotnet/openclaw.net/issues/233 — the plan argues from this issue; executors read both. Related design: zh 架构文档 `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` §5.3/§6/§7.5.

## Global Constraints

- Work happens on branch `nacos`; never push without explicit user approval.
- Every commit message ends with `Co-Authored-By: Claude Code <noreply@anthropic.com>`.
- No credentials anywhere (tests, docs, comments, issue posts). The local Nacos password and MiniMax keys are secrets and must never appear in any committed artifact.
- TDD RED→GREEN per task: write the failing test first, watch it fail for the expected reason, implement minimal code, watch it pass, commit.
- Build is warnings-as-errors on net10.0. New files must compile cleanly with the usings shown in the code blocks.
- Test baseline: the full suite has two known environmental failures unrelated to this issue (`PluginCommandsTests...NativePluginDoesNotRunNpmLifecycleScripts` — Node 24 npm temp-dir MODULE_NOT_FOUND; `CompanionCanvasUiTests...PreserveDraftUntilSend` — CRLF line endings). Expect exactly these two in the final full run; never "fix" them in this issue.
- Interpretation decisions locked with the issue text ("全部复用既有机制"):
  - AC1 (fallback on empty search) and AC3 (use_tool retry + fallback after exhaustion) are already implemented by existing runtime machinery (#231 fallback→`on_failure` folding, and the per-step `MetaStepRetryPolicy` retry loops in both runtimes). This plan pins them with e2e tests; no production change.
  - AC4 (fallback 与 on_failure 互斥) is already implemented AND already tested (`SkillTests.cs:817` expects `invalid_capability_ref`). Zero work; Task 4 cites it in verification.
  - "熔断" = the retry cap (`Retry.MaxAttempts`) plus fallback routing; no new persistent breaker state.
  - Rotation applies to `first` policy only (exact_name already rotates among name matches). `resolve_capability` itself remains never-cached and never calls `use_tool`.
- Existing e2e theories pin exact `state.Calls` sequences; the rotation change must keep the first-candidate-succeeds path byte-identical.

---

### Task 1: Rotate first-policy candidate adds through the Top-5 list

**Files:**
- Modify: `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs:102-112` (`PickCandidates`)
- Modify: `src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs` (fixture state: `FailAddNames`, `SucceedUseServers`; use-tool server check)
- Test: `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs` (2 new tests)
- Create: `src/OpenClaw.Tests/ResolveCapabilityToolTests.cs` (new file, 1 test)

**Interfaces:**
- Consumes: `ResolveCapabilityTool.ResolveCoreAsync(McpServerToolRegistry, ResolveCapabilityRequest, CancellationToken)`; `NacosRouterFixtureState`/`FakeNacosRouterMcpTools` fixture; `CapabilitySlotExecutor` + `MetaCapabilityRefDefinition` from #231; `CapabilityBindingCache` from #232.
- Produces: `first` policy tries all Top-5 candidates in upstream rank order, first successful `add_mcp_server` wins; failed adds rotate. Fixture gains `HashSet<string> FailAddNames` and `HashSet<string> SucceedUseServers`. Task 2's e2e theories consume both.

- [ ] **Step 1: Extend the fixture state (test infra, no production code)**

In `src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs`, extend `NacosRouterFixtureState`:

```csharp
public sealed class NacosRouterFixtureState
{
    public List<string> Calls { get; } = [];
    public bool FailUse { get; set; }
    public bool PlainTextFailure { get; set; }
    public bool FailAdd { get; set; }
    public bool EmptySearch { get; set; }

    // Issue #233: per-candidate add failures drive the rotation tests.
    public HashSet<string> FailAddNames { get; } = new(StringComparer.Ordinal);

    // Issue #233: when non-empty, use_tool succeeds for these servers too
    // (default remains weather-mcp only, keeping legacy tests pinned).
    public HashSet<string> SucceedUseServers { get; } = new(StringComparer.Ordinal);
}
```

In `FakeNacosRouterMcpTools.Add`, fail when the name is in `FailAddNames`:

```csharp
    [McpServerTool(Name = "add_mcp_server"), Description("Bind a registered server.")]
    public string Add(string mcp_server_name)
    {
        state.Calls.Add("add:" + mcp_server_name);
        if (state.FailAdd || state.FailAddNames.Contains(mcp_server_name))
            return "failed to install mcp server: " + mcp_server_name;
        return "1. " + mcp_server_name + RouterProseContract.AddSuccessMarker + ", " + RouterProseContract.AddToolListMarker
            + "[{\"name\":\"get_weather\",\"description\":\"weather city\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}}]"
            + "\n2." + mcp_server_name + "的工具需要通过nacos-mcp-router的use_tool工具代理使用";
    }
```

In `FakeNacosRouterMcpTools.Use`, accept the configured extra servers:

```csharp
    [McpServerTool(Name = "use_tool"), Description("Proxy an installed tool.")]
    public CallToolResult Use(string mcp_server_name, string mcp_tool_name, string @params)
    {
        state.Calls.Add("use:" + mcp_server_name + ":" + mcp_tool_name);
        var serverOk = mcp_server_name == "weather-mcp" || state.SucceedUseServers.Contains(mcp_server_name);
        var invalid = !serverOk || mcp_tool_name != "get_weather";
        var failed = state.FailUse || invalid;

        string text;
        if (failed)
        {
            text = "failed to use tool: " + mcp_tool_name;
        }
        else
        {
            text = TryDecodeCity(@params, mcp_tool_name, out failed)
                ? "Weather for " + ExtractCity(@params) + ": sunny"
                : "failed to use tool: " + mcp_tool_name;
        }

        return new CallToolResult { IsError = failed && !state.PlainTextFailure, Content = [new TextContentBlock { Text = text }] };
    }
```

- [ ] **Step 2: Write the two failing executor tests**

In `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs`, after `Execute_Dynamic_ExactNamePolicyNoMatch_ReturnsCapabilityResolveFailed` (before `Execute_Dynamic_NoCandidates_ReturnsCapabilityResolveFailed`):

```csharp
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
```

- [ ] **Step 3: Write the failing resolver wire-contract test (new file)**

Create `src/OpenClaw.Tests/ResolveCapabilityToolTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Wire-contract tests for the #230 resolve_capability native tool, pinned
/// against the shared fake Router fixture.
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResolveCapabilityToolTests
{
    private sealed class RouterFixture : IAsyncDisposable
    {
        public required NacosRouterFixtureState State { get; init; }
        public required McpServerToolRegistry Registry { get; init; }
        public required WebApplication Server { get; init; }

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
        return new RouterFixture { State = state, Registry = registry, Server = server };
    }

    [Fact]
    public async Task Resolve_FirstCandidateAddFails_TriedListsAttemptedCandidatesInRankOrder()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAddNames.Add("weather-mcp");

        var tool = new ResolveCapabilityTool(fixture.Registry);
        var json = await tool.ExecuteAsync(
            """{"task_description":"weather city","key_words":"weather,city","selection_policy":"first"}""",
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("server", out _), $"expected a binding, got: {json}");
        Assert.Equal("candidate-1", root.GetProperty("server").GetString());
        Assert.Equal("get_weather", root.GetProperty("tool").GetString());
        var tried = root.GetProperty("tried").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "weather-mcp", "candidate-1" }, tried);
        Assert.Equal(new[] { "search", "add:weather-mcp", "add:candidate-1" }, fixture.State.Calls);
    }
}
```

- [ ] **Step 4: Run the new tests and verify they fail for the expected reason**

Run:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~CapabilitySlotExecutorTests.Execute_Dynamic_FirstCandidateAddFails|FullyQualifiedName~CapabilitySlotExecutorTests.Execute_Dynamic_AllCandidateAddsFail|FullyQualifiedName~ResolveCapabilityToolTests"
```

Expected: 3 FAIL. With the current `PickCandidates` the `first` policy picks only `weather-mcp`, so the first add failure ends resolution immediately: the rotation test fails with `ResultStatus` Failed and `Calls` `[search, add:weather-mcp]`; the all-fail test fails with `Calls` `[search, add:weather-mcp]` instead of six entries; the wire test fails with "expected a binding" because the tool returns `all_adds_failed` JSON.

- [ ] **Step 5: Implement the rotation in the resolver core**

In `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs`, change `PickCandidates`:

```csharp
    private static IEnumerable<RouterCandidate> PickCandidates(
        IReadOnlyList<RouterCandidate> candidates,
        ResolveCapabilityRequest request)
    {
        if (request.SelectionPolicy == ResolveCapabilitySelectionPolicy.ExactName)
        {
            return candidates.Where(c =>
                string.Equals(c.Name, request.TaskDescription, StringComparison.OrdinalIgnoreCase));
        }

        // First policy: try candidates in the upstream's deterministic top-N
        // order; the first successful add wins and failed adds rotate (issue #233).
        return candidates;
    }
```

No other production change: `ResolveCoreAsync` already rotates on add failure (`add.IsError` → `continue`, prose failure → `TryExtractTool` false → `continue`) and stops only on transport death.

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run: the same filter command as Step 4.

Expected: 3 PASS, plus the pre-existing executor/parser/e2e tests still pass:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~CapabilitySlot|FullyQualifiedName~NacosRouterIntegrationTests|FullyQualifiedName~RouterCandidateParser"
```

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs src/OpenClaw.Tests/ResolveCapabilityToolTests.cs
git commit -m "feat(#233): rotate first-policy candidate adds through the Top-5 list"
```

---

### Task 2: Pin e2e fallback routing and candidate rotation in both runtimes

**Files:**
- Modify: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` (2 new theories)

**Interfaces:**
- Consumes: Task 1's rotation change and fixture fields (`FailAddNames`, `SucceedUseServers`); `LoadDynamicDemo()`; `CreateRuntime(maf, tools, memory, skill, gatewayConfig, executor)`.
- Produces: e2e guarantees for AC1 (empty search → fallback, normal completion) and AC2 (binding lands on the second candidate) pinned for both `AgentRuntime` and `MafAgentRuntime`.

- [ ] **Step 1: Write the two theories**

In `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`, after `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure` (before `DynamicSlot_SameSession_ResolvesOnceThenReusesBinding`):

```csharp
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
```

Note: these theories pin existing machinery (fallback folding + OnFailure branch from #231; rotation from Task 1), so no RED phase is expected — the `EmptySearch` theory must already pass on the current tree, and the rotation theory validates Task 1 end-to-end. If either fails, stop and diagnose; do not "fix" by weakening assertions.

- [ ] **Step 2: Run the new theories and verify PASS**

Run:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~NacosRouterIntegrationTests.DynamicSlot_EmptySearch_RoutesToFallback|FullyQualifiedName~NacosRouterIntegrationTests.DynamicSlot_FirstCandidateAddFails_BindsToNextCandidate"
```

Expected: 4 PASS (2 theories × 2 InlineData), including AC5's stepResults assertions (`query.FailureCode == "capability_resolve_failed"`, `fallback.Status == "completed"`).

- [ ] **Step 3: Run the surrounding e2e theories to confirm no regression**

Run:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~NacosRouterIntegrationTests"
```

Expected: all theories PASS (the existing `Calls` pinning must be untouched by rotation — first-success paths stay identical).

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/NacosRouterIntegrationTests.cs
git commit -m "test(#233): pin e2e fallback routing and candidate rotation in both runtimes"
```

---

### Task 3: Pin use_tool retry with backoff and a retry-capable example skill

**Files:**
- Create: `examples/skills/nacos-router-weather-retry/SKILL.md`
- Modify: `src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs` (`UseTimestamps` in fixture state + timestamp in `Use`)
- Modify: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` (ExtraDirs entry, `LoadRetryDemo()`, 1 new theory)

**Interfaces:**
- Consumes: `step.Retry` (`MetaStepRetryPolicy.MaxAttempts`/`BackoffMs`) consumed by `AgentRuntime.ExecuteMetaCapabilityStepWithPolicyAsync` (AgentRuntime.cs:2844) and the Maf twin (MafAgentRuntime.cs:~2629); fixture from Task 1.
- Produces: e2e pin for AC3 (3 attempts, backoff between attempts, fallback after exhaustion, `capability_use_tool_failed` in stepResults) in both runtimes; a user-facing retry example skill.

- [ ] **Step 1: Create the retry example skill**

Create `examples/skills/nacos-router-weather-retry/SKILL.md`:

````markdown
---
name: nacos-router-weather-retry
description: "Opt-in Nacos Router weather PoC: dynamic capability slot with step retry."
kind: meta
final_text_mode: "step:query"
composition:
  steps:
    - id: query
      kind: tool_call
      # Dynamic capability slot (issue #231) with node-level retry (issue #233):
      # use_tool failures retry up to three attempts with 100 ms backoff, then
      # the fallback branch fires. The resolver itself is deterministic and
      # adds no LLM turn.
      capability_ref:
        binding: dynamic
        intent:
          type: cap:WeatherQuery
          task_description: weather city
          keywords: [weather, city]
        selection_policy: first
        fallback: fallback_notice
      retry:
        max_attempts: 3
        backoff_ms: 100
      tool_args:
        city: "{{ input }}"
    - id: fallback_notice
      kind: tool_call
      tool: emit_text
      tool_args:
        text: "no weather capability bound; check Nacos registration and Router logs."
---
Opt-in contract fixture for #233: the capability step retries use_tool failures
three times (100 ms backoff) before the fallback branch fires; no model call
is needed.
````

- [ ] **Step 2: Extend the fixture with use timestamps**

In `src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs`, add to `NacosRouterFixtureState`:

```csharp
    // Issue #233: timestamps of every use_tool call, for backoff assertions.
    public List<DateTimeOffset> UseTimestamps { get; } = [];
```

And at the top of `FakeNacosRouterMcpTools.Use` (before `state.Calls.Add(...)`):

```csharp
        state.UseTimestamps.Add(DateTimeOffset.UtcNow);
```

- [ ] **Step 3: Write the retry theory**

In `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`, first extend `LoadSkill`'s `ExtraDirs` (inside `SkillLoader.LoadAll`):

```csharp
                ExtraDirs = [Path.Join(root.FullName, "examples", "skills", "nacos-router-weather"),
                    Path.Join(root.FullName, "examples", "skills", "nacos-router-weather-explore"),
                    Path.Join(root.FullName, "examples", "skills", "nacos-router-weather-dynamic"),
                    Path.Join(root.FullName, "examples", "skills", "nacos-router-weather-retry")]
```

Add the loader helper next to `LoadDynamicDemo`:

```csharp
    private static SkillDefinition LoadRetryDemo() => LoadSkill("nacos-router-weather-retry");
```

Add the theory after `DynamicSlot_FirstCandidateAddFails_BindsToNextCandidate`:

```csharp
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicSlot_UseToolFailureRetries_ThenFallsBack(bool maf)
    {
        var state = new NacosRouterFixtureState { FailUse = true, PlainTextFailure = true };
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
        var skill = LoadRetryDemo();
        var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var memory = new FileMemoryStore(root, 4);
        var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
        var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
        var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig, new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
        try
        {
            var session = new Session { Id = "nacos-retry-" + (maf ? "maf" : "native"), SenderId = "test", ChannelId = "test" };
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
            // Three attempts (resolve once, then cached-binding retries), then the
            // fallback branch fires.
            Assert.Equal("no weather capability bound; check Nacos registration and Router logs.", result);
            Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, state.Calls);
            var timestamps = state.UseTimestamps;
            Assert.Equal(3, timestamps.Count);
            Assert.True((timestamps[1] - timestamps[0]).TotalMilliseconds >= 100,
                $"expected >= 100 ms backoff between attempts 1 and 2, got {(timestamps[1] - timestamps[0]).TotalMilliseconds:0} ms");
            Assert.True((timestamps[2] - timestamps[1]).TotalMilliseconds >= 100,
                $"expected >= 100 ms backoff between attempts 2 and 3, got {(timestamps[2] - timestamps[1]).TotalMilliseconds:0} ms");
            var run = Assert.Single(session.MetaRunHistory);
            var query = Assert.Single(run.StepResults, step => step.Id == "query");
            Assert.Equal("capability_use_tool_failed", query.FailureCode);
            var fallback = Assert.Single(run.StepResults, step => step.Id == "fallback_notice");
            Assert.Equal("completed", fallback.Status);
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
```

Note: the retry loop lives in the runtime (not the executor), so this pins existing behavior — no RED phase expected. The binding cache (#232) makes attempts 2–3 pure `use_tool` calls; the `Calls` sequence proves that. The 60 ms floor (against a 100 ms backoff) proves the delay without flaking on timer resolution.

- [ ] **Step 4: Run the new theory and verify PASS**

Run:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~NacosRouterIntegrationTests.DynamicSlot_UseToolFailureRetries_ThenFallsBack"
```

Expected: 2 PASS (both runtimes). If the theory fails on `Calls` or timing, stop — a mismatch means the runtime retry loop differs from the plan's model; diagnose rather than adjust assertions silently.

- [ ] **Step 5: Run the full NacosRouter e2e class**

Run:

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~NacosRouterIntegrationTests"
```

Expected: all theories PASS (LiveRouter skipped as designed).

- [ ] **Step 6: Commit**

```bash
git add examples/skills/nacos-router-weather-retry/SKILL.md src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs src/OpenClaw.Tests/NacosRouterIntegrationTests.cs
git commit -m "test(#233): pin use_tool retry and backoff with a retry-capable example skill"
```

---

### Task 4: Document degradation semantics and run the full suite

**Files:**
- Modify: `docs/nacos-mcp-router.md`
- Modify: `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md`

**Interfaces:**
- Consumes: the #233 behavior from Tasks 1–3 (rotation, fallback routing, retry + exhaustion) and the #232 cache semantics already documented.
- Produces: docs that match the implemented behavior, including the AC4 note (mutual exclusion, already validated by `SkillTests.cs:817`).

- [ ] **Step 1: Update the EN contract doc**

In `docs/nacos-mcp-router.md`:

1. Header status block (lines 3–12): after "**capability slots implemented (2026-09-14)** for #231." insert a sibling sentence:

```text
**capability slots implemented (2026-09-14)** for #231; **node-level degradation
and retry implemented (2026-09-14)** for #233.
```

and in the closing line of the header, change "the remaining cache/retry/replay work (#232–#234) builds on it." to "the remaining replay work (#234) builds on it."

2. In "Capability resolver (issue #230)" inputs list, replace the `selection_policy` bullet:

```text
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`); a name with no exact match fails with `failure_code: "selection_policy_no_match"` and an empty `tried`.
```

with:

```text
- `selection_policy` (optional) — `first` (default) or `exact_name` (case-insensitive name match against `task_description`); a name with no exact match fails with `failure_code: "selection_policy_no_match"` and an empty `tried`. Under `first`, candidates are attempted in the upstream's deterministic top-N order and a failed add rotates to the next candidate; the first successful add wins.
```

3. In "Behaviour contract", replace item 4:

```text
4. `tried` lists the candidates the resolver actually attempted to add, not all returned candidates.
```

with:

```text
4. `tried` lists the candidates the resolver actually attempted to add, in rank order, not all returned candidates. Rotation applies to every attempted candidate: a failed add (prose or protocol) moves to the next one, and only a dead transport stops the rotation.
```

4. In "Capability slots (issue #231)", after the "Binding modes" list add a degradation bullet:

```text
- **degradation** — a failed slot routes to the step's `fallback` (folded into `on_failure` at parse time; declaring both `capability_ref.fallback` and a step-level `on_failure` is rejected as `invalid_capability_ref`). `use_tool` failures retry per the step's `retry` policy (`max_attempts` + `backoff_ms`) before the fallback fires, and the failed step records its `failure_code` in the run's step results.
```

5. In "Examples:", extend:

```text
Examples: `examples/skills/nacos-router-weather` (static) and
`examples/skills/nacos-router-weather-dynamic` (dynamic). Both add an
`emit_text` fallback step and stay opt-in, not bundled by default.
```

to:

```text
Examples: `examples/skills/nacos-router-weather` (static),
`examples/skills/nacos-router-weather-dynamic` (dynamic), and
`examples/skills/nacos-router-weather-retry` (dynamic + `retry` policy).
Each adds an `emit_text` fallback step and stays opt-in, not bundled by default.
```

6. In "Remaining live acceptance" item 4, replace "Prose failures are now typed via the #230 `failure_code` envelope; fallback, retries, caching, or replay build on that." with "Prose failures are now typed via the #230 `failure_code` envelope; fallback routing, candidate rotation, retry, and caching build on that (#231/#232/#233). Binding replay remains #234."

7. Final paragraph: replace "The static slot's auto-add cache is runtime-scoped idempotency only — no session-level binding cache, retry policy, or binding replay yet; those remain tracked by #232–#234." with "The static slot's auto-add cache is runtime-scoped idempotency; session-level binding caching (#232) and node-level degradation with retry (#233) are implemented. Binding replay remains tracked by #234."

- [ ] **Step 2: Update the zh architecture doc**

In `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md`:

1. §5.3 dynamic row: append rotation/retry semantics to the cell ending "绑定按会话缓存（intent 哈希键，TTL/reload 失效，#232）":

```text
| `binding: dynamic` | `ms:binding = "dynamic"` | 执行时经 #230 Resolver 核心 `search → add` 解析绑定，再 `use_tool`；零 LLM 往返；绑定按会话缓存（intent 哈希键，TTL/reload 失效，#232）；add 失败按 Top-5 顺序轮替候选，use_tool 失败按 retry 策略重试后走 fallback（#233） |
```

2. §6 note: change "实现对照（2026-09-14，#230/#231/#232 已落地）" to "实现对照（2026-09-14，#230/#231/#232/#233 已落地）" and append before the trailing "。" of that note: "；节点级降级（fallback 路由 / Top-5 候选轮替 / retry 重试熔断）已实现".

3. §7.5 table: add a status column and mark every row implemented:

```text
| 失败点 | 策略 | 状态 |
|---|---|---|
| `search_mcp_server` 返回空 | 路由到 `ms:fallback` 指定节点（如 OpenClaw 原生 web 搜索工具） | #233 已实现 |
| `add_mcp_server` 失败 | 自动尝试 Top-5 中的下一个候选 | #233 已实现 |
| `use_tool` 执行错误 | 节点级重试 + 熔断，错误记入 DAG 执行轨迹 | #233 已实现 |
```

4. §8 note: change "原生 Resolver（#230）与槽位执行（#231，静态 + 动态）已落地，三步链确定性化完成；会话级绑定缓存、Nacos 变更事件订阅仍待实现。" to "原生 Resolver（#230）与槽位执行（#231，静态 + 动态）已落地，三步链确定性化完成；会话级绑定缓存（#232）与节点级降级（#233）已实现；Nacos 变更事件订阅仍待实现。" (fixes a stale #232 status line found during #233).

- [ ] **Step 3: Run the full test suite and compare against the baseline**

Run:

```sh
dotnet test -c Release
```

Expected: all pass except the two known environmental failures from the Global Constraints (npm temp-dir MODULE_NOT_FOUND; CRLF line endings), plus the designed `LiveRouter` skip. Any other failure is a regression from this issue — diagnose and fix before committing.

Also run the validation pin for AC4 (already-implemented, cites the existing test):

```sh
dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~SkillTests.TryParseSkillContent_CapabilityRefValidation_ReturnsExpectedCode"
```

Expected: PASS — the `fallback` + `on_failure` coexistence case already expects `invalid_capability_ref`.

- [ ] **Step 4: Commit**

```bash
git add docs/nacos-mcp-router.md "docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md"
git commit -m "docs(#233): record node-level degradation and retry semantics"
```

- [ ] **Step 5: Report**

Report per-AC mapping with test evidence, the full-suite numbers (including the two known environmental failures stated honestly), and the commit list. Do not push and do not post issue comments without explicit user instruction.

---

## Self-Review

- **Spec coverage:** AC1 → Task 2 (`DynamicSlot_EmptySearch_RoutesToFallback`); AC2 → Task 1 (rotation) + Task 2 (e2e); AC3 → Task 3; AC4 → already implemented + tested (Task 4 Step 3 verification pin); AC5 → asserted in every new e2e theory (stepResults `FailureCode`); AC6 → Task 4 Step 3. Issue constraint "fallback 与 on_failure 互斥" → existing `SkillTests.cs:817`, cited in Task 4. Issue constraint "复用既有机制" → honored (no new retry machinery, no new breaker state; only the dead-code fix in `PickCandidates`).
- **Placeholder scan:** every code step carries full code; no TBD/TODO.
- **Type consistency:** fixture fields `FailAddNames`/`SucceedUseServers`/`UseTimestamps` defined in Task 1/Task 3 and consumed with identical names in the e2e theories; `LoadRetryDemo` defined and used in Task 3; `final_text_mode: "step:query"` and step ids `query`/`fallback_notice` match the existing dynamic demo; fallback output text matches the existing pinned string.
