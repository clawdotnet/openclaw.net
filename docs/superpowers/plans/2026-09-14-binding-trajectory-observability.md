# Binding Trajectory Observability (#234) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record the complete binding path of every capability-slot execution (intent → candidates → selected server/tool → cache hit → elapsed) into the persisted meta-run step evidence, so `openclaw skills meta-runs --json` exports it and the `OpenClaw.Testing` harness replays it offline with same-input → same-binding assertions.

**Architecture:** A new Core model `CapabilityBindingTrajectory` (+ `CapabilityBindingCandidate`) hangs off `SessionMetaStepExecutionEvidence.CapabilityBinding` and is registered in `CoreJsonContext` (AOT source-gen). `CapabilitySlotExecutor` builds the trajectory on every outcome (static/dynamic, success/failure, cache hit/miss) and returns it on `ToolExecutionResult.BindingTrajectory`; `ResolveCoreAsync` additionally returns the full candidate list (internal signature change only — the `resolve_capability` tool's JSON output contract is unchanged). Both runtimes (`AgentRuntime` and `MafAgentRuntime` have separate copies of the step-result mapping) attach it to `MetaStepExecutionResult.ExecutionEvidence`, which already flows into `SessionMetaRunRecord.StepResults` and therefore into the existing `meta-runs --json` serialization — **no CLI change is needed**. `OpenClaw.Testing` gains `CapabilityBindingReplayFixture.FromMetaRun` + `CapabilityBindingReplay` (with constructor validation and cache-seeding for hit fixtures, mirroring the existing `TrajectoryReplay` pattern) to replay an exported run against a real executor and assert the binding is reproduced.

**Tech Stack:** net10.0 / C# 14 / warnings-as-errors, System.Text.Json source-gen contexts (`CoreJsonContext`, `ScenarioJsonContext`), xUnit + NSubstitute, ModelContextProtocol .NET (in-process HTTP fake router `FakeNacosRouterMcpTools`), superpowers TDD (RED-GREEN, per-task commits).

**Spec:** https://github.com/clawdotnet/openclaw.net/issues/234 (binding 轨迹可观测性闭环：meta-runs 审计 + harness fixture 回流). Design context: `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` §7.6.

## Global Constraints

- Every commit message ends with `Co-Authored-By: Claude Code <noreply@anthropic.com>`.
- Never push the `nacos` branch without the user's explicit approval.
- No credentials in commits, issue comments, docs, or tests; the MiniMax API key and the local Nacos password must never appear anywhere.
- Upstream search returns **no score** (pinned #230 contract): the trajectory records the upstream positional `rank`, never a fabricated score. This deviates from the issue text "Top-5 候选（name+score）" — the deviation and its reason are recorded in the plan, the report, and the issue comment.
- AOT-compatible serialization only: any new serializable type MUST be registered in a `JsonSerializable` source-gen context (`CoreJsonContext` for Core models).
- The `resolve_capability` tool's public JSON output contract must not change (docs pin it). `ResolveCoreAsync` is `internal` — its signature change is contained.
- Runtime parity: `AgentRuntime` and `MafAgentRuntime` each contain their own copy of the meta step-result mapping code; both mapping sites must be edited.
- Compile-level RED is acceptable (established precedent in #233); pinning tests with no RED phase are labeled as such.
- Full-suite baseline (known environmental failures, not caused by this issue): 2 failures (`PluginCommandsTests...NativePluginDoesNotRunNpmLifecycleScripts` npm MODULE_NOT_FOUND, `CompanionCanvasUiTests...PreserveDraftUntilSend` CRLF) + 1 skip (`LiveRouter_*`, requires `OPENCLAW_NACOS_LIVE=1`). Treat 2744 pass / 2 fail / 1 skip as green.
- Work in the main checkout on branch `nacos` (no worktree per user's established workflow).

## File Structure

- Modify: `src/OpenClaw.Core/Models/Session.cs` — `CapabilityBindingTrajectory`, `CapabilityBindingCandidate`, evidence property, `CoreJsonContext` registrations.
- Modify: `src/OpenClaw.Testing/HarnessRegressionScenarios.cs` — `CapabilityBindingTrajectorySerializationScenario` round-trip pin.
- Create: `src/OpenClaw.Testing/CapabilityBindingReplay.cs` — replay fixture + replay runner (harness 回流).
- Modify: `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs` — `ResolveCoreAsync` returns the full candidate list.
- Modify: `src/OpenClaw.Agent/OpenClawToolExecutor.cs` — `ToolExecutionResult.BindingTrajectory`.
- Modify: `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs` — build + attach trajectories.
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs:1553-1559` and `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs:1331-1337` — evidence mapping.
- Test: `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs`, `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`, `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs`.
- Docs: `docs/nacos-mcp-router.md`, `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md`.

---

### Task 1: CapabilityBindingTrajectory model + serialization round-trip

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs:305-322` (evidence + new types) and `:810-814` (context registrations)
- Modify: `src/OpenClaw.Testing/HarnessRegressionScenarios.cs:17-35` (scenario list) and append the new scenario class

**Interfaces:**
- Produces: `CapabilityBindingTrajectory` (sealed class, get/set props, see code below), `CapabilityBindingCandidate { string Name; int Rank }`, `SessionMetaStepExecutionEvidence.CapabilityBinding` property. Tasks 2-5 consume these.

- [ ] **Step 1: Write the failing test (compile-level RED — types do not exist yet)**

Append to `src/OpenClaw.Testing/HarnessRegressionScenarios.cs` and add it to `CreateDefault()` (after `GovernanceLedgerSerializationScenario`):

```csharp
internal sealed class CapabilityBindingTrajectorySerializationScenario()
    : HarnessRegressionScenarioBase(
        "harness.capability_binding_trajectory_serialization",
        "Capability binding trajectory serialization",
        HarnessRegressionCategory.Harness)
{
    protected override ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var run = new SessionMetaRunRecord
        {
            RunId = "meta_binding_regression",
            SkillName = "meta-capability",
            Status = "completed",
            StepResults =
            {
                new SessionMetaStepResult
                {
                    Id = "query",
                    Kind = "tool_call",
                    Status = "completed",
                    ExecutionEvidence = new SessionMetaStepExecutionEvidence
                    {
                        CapabilityBinding = new CapabilityBindingTrajectory
                        {
                            Binding = "dynamic",
                            IntentKey = "a1b2c3",
                            TaskDescription = "weather city",
                            KeyWords = "weather,city",
                            SelectionPolicy = "first",
                            CacheHit = false,
                            Server = "weather-mcp",
                            Tool = "get_weather",
                            ElapsedMs = 12.5,
                            Candidates =
                            {
                                new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 },
                                new CapabilityBindingCandidate { Name = "amap-mcp-server", Rank = 2 }
                            },
                            Attempted =
                            {
                                new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 }
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(run, CoreJsonContext.Default.SessionMetaRunRecord);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionMetaRunRecord);
        var binding = restored?.StepResults[0].ExecutionEvidence?.CapabilityBinding;

        return binding is not null &&
               binding.Binding == "dynamic" &&
               binding.IntentKey == "a1b2c3" &&
               binding.CacheHit == false &&
               binding.Server == "weather-mcp" &&
               binding.Tool == "get_weather" &&
               binding.Candidates.Count == 2 &&
               binding.Candidates[0] is { Name: "weather-mcp", Rank: 1 } &&
               binding.Candidates[1] is { Name: "amap-mcp-server", Rank: 2 } &&
               binding.Attempted.Count == 1
            ? ValueTask.FromResult(Passed("Capability binding trajectory round-tripped through source-generated JSON."))
            : ValueTask.FromResult(Failed("Capability binding trajectory did not round-trip correctly."));
    }
}
```

And in `CreateDefault()` add `new CapabilityBindingTrajectorySerializationScenario(),` after the `GovernanceLedgerSerializationScenario` entry.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~HarnessRegression`
Expected: FAIL — build error CS0246: `CapabilityBindingTrajectory` / `CapabilityBindingCandidate` do not exist. This is a compile-level RED; proceed.

- [ ] **Step 3: Implement the model**

In `src/OpenClaw.Core/Models/Session.cs`, replace the `SessionMetaStepExecutionEvidence` class (lines 316-322) with:

```csharp
public sealed class SessionMetaStepExecutionEvidence
{
    public string CommandPreview { get; init; } = string.Empty;
    public string InputMode { get; init; } = "none";
    public int StdinBytes { get; init; }
    public string ParseMode { get; init; } = "text";
    public CapabilityBindingTrajectory? CapabilityBinding { get; init; }
}

/// <summary>
/// The recorded binding path of one capability slot execution (issue #234).
/// Persisted with the run's step results so `meta-runs --json` and the
/// OpenClaw.Testing harness can audit and replay bindings offline.
/// Upstream search returns no scores, so candidates carry the positional
/// rank only — none are fabricated (issue #230 contract).
/// </summary>
public sealed class CapabilityBindingTrajectory
{
    /// <summary>Binding mode: <c>static</c> or <c>dynamic</c>.</summary>
    public string Binding { get; set; } = "";

    /// <summary>SHA-256 hex of the normalised intent (dynamic only).</summary>
    public string? IntentKey { get; set; }

    /// <summary>Intent task description fed to the resolver (dynamic only).</summary>
    public string? TaskDescription { get; set; }

    /// <summary>Comma-separated intent keywords (dynamic only).</summary>
    public string? KeyWords { get; set; }

    /// <summary>Resolver selection policy as the wire value: <c>first</c> or <c>exact_name</c>.</summary>
    public string SelectionPolicy { get; set; } = "first";

    /// <summary>True when the session binding cache supplied the binding (dynamic only).</summary>
    public bool CacheHit { get; set; }

    /// <summary>The bound server; null when binding failed.</summary>
    public string? Server { get; set; }

    /// <summary>The bound tool; null when binding failed.</summary>
    public string? Tool { get; set; }

    /// <summary>Elapsed milliseconds of the binding phase only (excludes use_tool).</summary>
    public double ElapsedMs { get; set; }

    /// <summary>Search Top-N candidates in upstream rank order (dynamic only).</summary>
    public List<CapabilityBindingCandidate> Candidates { get; set; } = [];

    /// <summary>Candidates the resolver actually attempted to add, in rank order.</summary>
    public List<CapabilityBindingCandidate> Attempted { get; set; } = [];
}

public sealed class CapabilityBindingCandidate
{
    public string Name { get; set; } = "";
    public int Rank { get; set; }
}
```

In the same file, add these registrations to `CoreJsonContext` (after line 814):

```csharp
[JsonSerializable(typeof(CapabilityBindingTrajectory))]
[JsonSerializable(typeof(List<CapabilityBindingTrajectory>))]
[JsonSerializable(typeof(CapabilityBindingCandidate))]
[JsonSerializable(typeof(List<CapabilityBindingCandidate>))]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~HarnessRegression`
Expected: PASS — all HarnessRegression tests pass, including the default-suite run that executes `CreateDefault()` scenarios.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Testing/HarnessRegressionScenarios.cs
git commit -m "feat(#234): add capability binding trajectory model to session run evidence

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: CapabilitySlotExecutor records the binding trajectory

**Files:**
- Modify: `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs:50-100` (ResolveCoreAsync signature + return sites)
- Modify: `src/OpenClaw.Agent/OpenClawToolExecutor.cs:19-30` (ToolExecutionResult)
- Modify: `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs` (trajectory construction)
- Test: `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs`

**Interfaces:**
- Consumes: `CapabilityBindingTrajectory` / `CapabilityBindingCandidate` (Task 1).
- Produces: `ToolExecutionResult.BindingTrajectory` (property); `ResolveCapabilityTool.ResolveCoreAsync` returns `(ResolveCapabilityBinding? Binding, ResolveCapabilityFailure? Failure, IReadOnlyList<RouterCandidate> Candidates)`. Task 3 consumes `BindingTrajectory`; Task 5 consumes both.

- [ ] **Step 1: Write the failing tests (compile-level RED — `BindingTrajectory` does not exist)**

Append to `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs` (uses the existing `CreateFixtureAsync`, `StaticRef`, `DynamicRef` helpers):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilitySlotExecutorTests`
Expected: FAIL — build error CS1061: `ToolExecutionResult` has no `BindingTrajectory`. Compile-level RED; proceed.

- [ ] **Step 3: Extend ToolExecutionResult**

In `src/OpenClaw.Agent/OpenClawToolExecutor.cs`, add to `ToolExecutionResult` (after `NextStep`, line 26):

```csharp
    public CapabilityBindingTrajectory? BindingTrajectory { get; init; }
```

- [ ] **Step 4: Return the full candidate list from ResolveCoreAsync**

In `src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs`, change the signature (line 50-51) and every return site:

```csharp
    internal static async Task<(ResolveCapabilityBinding? Binding, ResolveCapabilityFailure? Failure, IReadOnlyList<RouterCandidate> Candidates)> ResolveCoreAsync(
        McpServerToolRegistry registry, ResolveCapabilityRequest request, CancellationToken ct)
```

Return sites (exact replacements):

```csharp
        if (client is null)
            return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.RouterUnavailable, []), []);
        ...
        if (!search.Reached || search.IsError)
            return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.RouterUnavailable, []), []);

        var candidates = RouterCandidateParser.Parse(search.Text);
        if (candidates.Count == 0)
            return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.NoCandidates, []), []);

        var picked = PickCandidates(candidates, request).ToList();
        if (picked.Count == 0)
            return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.SelectionPolicyNoMatch, []), candidates);
        ...
            if (!add.Reached)
                return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.RouterUnavailable, tried), candidates);
            ...
            if (TryExtractTool(add.Text, out var toolName, out var schema))
                return (new ResolveCapabilityBinding(candidate.Name, toolName, schema, tried), null, candidates);
        ...

        return (null, new ResolveCapabilityFailure(ResolveCapabilityFailureCodes.AllAddsFailed, tried), candidates);
```

Update the tool's `ExecuteAsync` caller (line 38) to discard the third element:

```csharp
        var (binding, failure, _) = await ResolveCoreAsync(_registry, request, ct);
```

- [ ] **Step 5: Record the trajectory in CapabilitySlotExecutor**

In `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs`:

1. Add `using System.Diagnostics;` to the usings.
2. Replace `ExecuteAsync` (lines 51-112) with:

```csharp
    public async Task<ToolExecutionResult> ExecuteAsync(
        MetaCapabilityRefDefinition capabilityRef, string toolArgsJson, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);

        var bindingSw = Stopwatch.StartNew();
        var trajectory = BuildTrajectory(capabilityRef);

        var client = _registry.GetClientByServerId(RouterServerId);
        if (client is null)
        {
            trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
            return Fail(CapabilitySlotFailureCodes.RouterUnavailable,
                $"Nacos MCP Router '{RouterServerId}' is not registered; configure the Router server before executing capability slots.",
                toolArgsJson, trajectory);
        }

        string server;
        string tool;
        if (capabilityRef.Binding == "static")
        {
            var pinned = capabilityRef.Static!;
            server = pinned.McpServerName;
            tool = pinned.ToolName;
            var addFailure = await EnsureAddedAsync(client, server, trajectory, ct);
            if (addFailure is not null)
            {
                trajectory.Server = server;
                trajectory.Tool = tool;
                trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
                return addFailure;
            }
        }
        else
        {
            var intent = capabilityRef.Intent!;
            var request = new ResolveCapabilityRequest(
                intent.TaskDescription,
                intent.Keywords.Count > 0 ? string.Join(",", intent.Keywords) : null,
                capabilityRef.SelectionPolicy == "exact_name"
                    ? ResolveCapabilitySelectionPolicy.ExactName
                    : ResolveCapabilitySelectionPolicy.First);

            var intentKey = CapabilityBindingCache.ComputeIntentKey(
                request.TaskDescription, request.KeyWords, request.SelectionPolicy.ToString());
            if (!string.IsNullOrEmpty(sessionId) &&
                _bindingCache.TryGet(sessionId, intentKey, out var cachedServer, out var cachedTool))
            {
                server = cachedServer;
                tool = cachedTool;
                trajectory.CacheHit = true;
            }
            else
            {
                var (binding, failure, candidates) = await ResolveCapabilityTool.ResolveCoreAsync(_registry, request, ct);
                if (binding is null)
                {
                    var code = failure!.FailureCode == ResolveCapabilityFailureCodes.RouterUnavailable
                        ? CapabilitySlotFailureCodes.RouterUnavailable
                        : CapabilitySlotFailureCodes.ResolveFailed;
                    trajectory.Candidates = ToTrajectoryCandidates(candidates);
                    trajectory.Attempted = ToTrajectoryCandidates(failure.TriedCandidates);
                    trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
                    return Fail(code, $"capability resolve failed: {failure.FailureCode}", toolArgsJson, trajectory);
                }

                server = binding.Server;
                tool = binding.Tool;
                trajectory.Candidates = ToTrajectoryCandidates(candidates);
                trajectory.Attempted = ToTrajectoryCandidates(binding.TriedCandidates);
                if (!string.IsNullOrEmpty(sessionId))
                    _bindingCache.Set(sessionId, intentKey, server, tool);
            }
        }

        trajectory.Server = server;
        trajectory.Tool = tool;
        trajectory.ElapsedMs = bindingSw.Elapsed.TotalMilliseconds;
        return await UseToolAsync(client, server, tool, toolArgsJson, trajectory, ct);
    }
```

3. Add the two helpers (below `ExecuteAsync`):

```csharp
    private static CapabilityBindingTrajectory BuildTrajectory(MetaCapabilityRefDefinition capabilityRef)
    {
        var trajectory = new CapabilityBindingTrajectory { Binding = capabilityRef.Binding };
        if (capabilityRef.Binding == "dynamic" && capabilityRef.Intent is { } intent)
        {
            trajectory.TaskDescription = intent.TaskDescription;
            trajectory.KeyWords = intent.Keywords.Count > 0 ? string.Join(",", intent.Keywords) : null;
            trajectory.SelectionPolicy = capabilityRef.SelectionPolicy == "exact_name" ? "exact_name" : "first";
            trajectory.IntentKey = CapabilityBindingCache.ComputeIntentKey(
                intent.TaskDescription, trajectory.KeyWords, capabilityRef.SelectionPolicy == "exact_name" ? "ExactName" : "First");
        }
        return trajectory;
    }

    private static List<CapabilityBindingCandidate> ToTrajectoryCandidates(IReadOnlyList<RouterCandidate> candidates)
        => candidates.Select(c => new CapabilityBindingCandidate { Name = c.Name, Rank = c.Rank }).ToList();
```

4. Thread the trajectory through the helpers:
   - `EnsureAddedAsync(McpClient client, string server, CapabilityBindingTrajectory trajectory, CancellationToken ct)` — pass `trajectory` as the last argument of each of its three `Fail(...)` calls.
   - `UseToolAsync(McpClient client, string server, string tool, string toolArgsJson, CapabilityBindingTrajectory trajectory, CancellationToken ct)` — pass `trajectory` to its `Fail(...)` calls and to `Completed(...)`.
   - `Fail` and `Completed` gain a trailing parameter `CapabilityBindingTrajectory? trajectory = null` and set `BindingTrajectory = trajectory` in the object initializer.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilitySlotExecutorTests`
Expected: PASS — all CapabilitySlotExecutorTests (existing + 5 new) pass.

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Agent/Tools/ResolveCapabilityTool.cs src/OpenClaw.Agent/OpenClawToolExecutor.cs src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs
git commit -m "feat(#234): record binding trajectory on capability slot execution

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: Both runtimes persist the trajectory into run step evidence

**Files:**
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs:1553-1559`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs:1331-1337`
- Test: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`

**Interfaces:**
- Consumes: `ToolExecutionResult.BindingTrajectory` (Task 2), `SessionMetaStepExecutionEvidence.CapabilityBinding` (Task 1).
- Produces: persisted trajectory on `SessionMetaRunRecord.StepResults[i].ExecutionEvidence` — consumed by Tasks 4-5.

- [ ] **Step 1: Write the failing tests (runtime RED — evidence is currently null)**

Append to `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`. Copy the fixture/setup block verbatim from `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure` (state, WebApplication, registry, `ServerConfig`, `LoadDynamicDemo`/`LoadStaticDemo`, memory store, `CreateRuntime`, and the `finally` cleanup) and change only the test body as shown:

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task DynamicSlot_RecordsBindingTrajectoryInMetaRunHistory(bool maf)
{
    var state = new NacosRouterFixtureState();
    // ... identical setup to DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure ...
    try
    {
        var session = new Session { Id = "nacos-traj", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        Assert.Equal("Weather for Oslo: sunny", result);

        var run = Assert.Single(session.MetaRunHistory);
        var query = Assert.Single(run.StepResults, step => step.Id == "query");
        var binding = query.ExecutionEvidence?.CapabilityBinding;
        Assert.NotNull(binding);
        Assert.Equal("dynamic", binding.Binding);
        Assert.Equal("weather city", binding.TaskDescription);
        Assert.Equal("weather", binding.KeyWords);
        Assert.Equal("first", binding.SelectionPolicy);
        Assert.False(binding.CacheHit);
        Assert.Equal("weather-mcp", binding.Server);
        Assert.Equal("get_weather", binding.Tool);
        Assert.True(binding.ElapsedMs >= 0);
        Assert.Equal(5, binding.Candidates.Count);
        Assert.Equal(1, binding.Candidates[0].Rank);
        var attempted = Assert.Single(binding.Attempted);
        Assert.Equal("weather-mcp", attempted.Name);
        Assert.Equal(1, attempted.Rank);
    }
    finally { /* identical cleanup */ }
}

[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task StaticSlot_RecordsStaticBindingTrajectoryInMetaRunHistory(bool maf)
{
    var state = new NacosRouterFixtureState();
    // ... identical setup to StaticSlot_UsesOnlyThreeRouterTools_AndHandlesProtocolFailures, skill = LoadStaticDemo() ...
    try
    {
        var session = new Session { Id = "nacos-traj-static", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        Assert.Equal("Weather for Oslo: sunny", result);

        var run = Assert.Single(session.MetaRunHistory);
        var query = Assert.Single(run.StepResults, step => step.Id == "query");
        var binding = query.ExecutionEvidence?.CapabilityBinding;
        Assert.NotNull(binding);
        Assert.Equal("static", binding.Binding);
        Assert.Equal("weather-mcp", binding.Server);
        Assert.Equal("get_weather", binding.Tool);
        Assert.Null(binding.TaskDescription);
        Assert.Null(binding.IntentKey);
        Assert.False(binding.CacheHit);
        Assert.Empty(binding.Candidates);
        Assert.Empty(binding.Attempted);
    }
    finally { /* identical cleanup */ }
}

[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task DynamicSlot_EmptySearch_RecordsFailureTrajectory(bool maf)
{
    var state = new NacosRouterFixtureState { EmptySearch = true };
    // ... identical setup to DynamicSlot_EmptySearch_RoutesToFallback ...
    try
    {
        var session = new Session { Id = "nacos-traj-empty", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        Assert.Equal("no weather capability bound; check Nacos registration and Router logs.", result);

        var run = Assert.Single(session.MetaRunHistory);
        var query = Assert.Single(run.StepResults, step => step.Id == "query");
        Assert.Equal("capability_resolve_failed", query.FailureCode);
        var binding = query.ExecutionEvidence?.CapabilityBinding;
        Assert.NotNull(binding);
        Assert.Equal("dynamic", binding.Binding);
        Assert.False(binding.CacheHit);
        Assert.Null(binding.Server);
        Assert.Null(binding.Tool);
        Assert.Empty(binding.Candidates);
        Assert.Empty(binding.Attempted);
    }
    finally { /* identical cleanup */ }
}
```

The setup blocks referenced by the comments are long; copy them verbatim from the named existing tests rather than abbreviating — every line the existing test runs before `try` must appear.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests`
Expected: FAIL — the 3 new theories fail on `Assert.NotNull(binding)` ("Assert.NotNull() Failure" with actual null). The executor sets `BindingTrajectory` (Task 2) but the runtime drops it. This is a runtime RED.

- [ ] **Step 3: Map the trajectory into evidence in AgentRuntime**

In `src/OpenClaw.Agent/AgentRuntime.cs` lines 1553-1559, change the `stepResults.Add` to:

```csharp
                        stepResults.Add(new MetaStepExecutionResult(
                            step.Id,
                            step.Kind,
                            resultStatus,
                            failureCode,
                            stepSw.Elapsed.TotalMilliseconds,
                            Continued: !completed && continueOnError,
                            ExecutionEvidence: toolResult.BindingTrajectory is null
                                ? null
                                : new SessionMetaStepExecutionEvidence { CapabilityBinding = toolResult.BindingTrajectory }));
```

- [ ] **Step 4: Map the trajectory into evidence in MafAgentRuntime**

Apply the identical change to `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs` lines 1331-1337 (same `stepResults.Add(new MetaStepExecutionResult(...))` shape inside the capability branch).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests`
Expected: PASS — all NacosRouterIntegrationTests pass (existing + 3 new).

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Agent/AgentRuntime.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs src/OpenClaw.Tests/NacosRouterIntegrationTests.cs
git commit -m "feat(#234): persist binding trajectory into meta run step evidence in both runtimes

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: Cache-hit distinction end-to-end + meta-runs --json export pin

Pinning task — the producer (Task 2) and mapping (Task 3) already handle cache hits and the CLI serializes nested evidence without changes. If any of these tests FAIL instead of passing on first run, stop and diagnose rather than weakening assertions.

**Files:**
- Test: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` (cache-hit e2e)
- Test: `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs` (--json export shape)

- [ ] **Step 1: Write the cache-hit e2e test**

Append to `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` (setup copied verbatim from `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure`):

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task DynamicSlot_SameSessionSecondExecution_RecordsCacheHitTrajectory(bool maf)
{
    var state = new NacosRouterFixtureState();
    // ... identical setup to DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure ...
    try
    {
        var session = new Session { Id = "nacos-cache", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        var callsAfterFirst = state.Calls.Count;

        var second = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        Assert.Equal("Weather for Oslo: sunny", first);
        Assert.Equal("Weather for Oslo: sunny", second);

        // The second execution binds from the session cache: only use_tool fires.
        Assert.Equal(new[] { "use:weather-mcp:get_weather" }, state.Calls.Skip(callsAfterFirst).ToArray());
        Assert.Equal(2, session.MetaRunHistory.Count);
        var firstBinding = session.MetaRunHistory[0].StepResults.Single(s => s.Id == "query").ExecutionEvidence!.CapabilityBinding!;
        var secondBinding = session.MetaRunHistory[1].StepResults.Single(s => s.Id == "query").ExecutionEvidence!.CapabilityBinding!;
        Assert.False(firstBinding.CacheHit);
        Assert.True(secondBinding.CacheHit);
        Assert.Empty(secondBinding.Candidates);
        Assert.Empty(secondBinding.Attempted);
        Assert.Equal(firstBinding.Server, secondBinding.Server);
        Assert.Equal(firstBinding.Tool, secondBinding.Tool);
    }
    finally { /* identical cleanup */ }
}
```

- [ ] **Step 2: Run the e2e test**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~DynamicSlot_SameSessionSecondExecution_RecordsCacheHitTrajectory`
Expected: PASS immediately (pinning).

- [ ] **Step 3: Write the CLI export test**

Append to `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs` (same Console redirection pattern as its neighbors):

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Json_IncludesCapabilityBindingTrajectory()
{
    var root = CreateTempRoot();
    var previousOut = Console.Out;
    var previousError = Console.Error;

    try
    {
        var memoryPath = Path.Combine(root, "memory");
        await using (var store = new FileMemoryStore(memoryPath))
        {
            await store.SaveSessionAsync(new Session
            {
                Id = "sess-capability",
                ChannelId = "cli",
                SenderId = "tester-cap",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-cap-001",
                        SkillName = "meta-capability",
                        Status = "completed",
                        StepResults =
                        {
                            new SessionMetaStepResult
                            {
                                Id = "query",
                                Kind = "tool_call",
                                Status = "completed",
                                ExecutionEvidence = new SessionMetaStepExecutionEvidence
                                {
                                    CapabilityBinding = new CapabilityBindingTrajectory
                                    {
                                        Binding = "dynamic",
                                        IntentKey = "a1b2c3",
                                        TaskDescription = "weather city",
                                        KeyWords = "weather,city",
                                        SelectionPolicy = "first",
                                        CacheHit = false,
                                        Server = "weather-mcp",
                                        Tool = "get_weather",
                                        ElapsedMs = 12.5,
                                        Candidates = { new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 } },
                                        Attempted = { new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 } }
                                    }
                                }
                            }
                        }
                    }
                }
            }, TestContext.Current.CancellationToken);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-capability", "--storage", memoryPath, "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        using var document = JsonDocument.Parse(output.ToString());
        var run = document.RootElement.GetProperty("runs")[0];
        // The envelope fields (sessionId/totalCount/shownCount/runs) are written
        // explicitly in camelCase; run/step/evidence objects serialize verbatim
        // (CoreJsonContext has no naming policy), hence PascalCase names below.
        var binding = run.GetProperty("StepResults")[0].GetProperty("ExecutionEvidence").GetProperty("CapabilityBinding");
        Assert.Equal("dynamic", binding.GetProperty("Binding").GetString());
        Assert.Equal("a1b2c3", binding.GetProperty("IntentKey").GetString());
        Assert.Equal("weather city", binding.GetProperty("TaskDescription").GetString());
        Assert.Equal("weather,city", binding.GetProperty("KeyWords").GetString());
        Assert.Equal("first", binding.GetProperty("SelectionPolicy").GetString());
        Assert.False(binding.GetProperty("CacheHit").GetBoolean());
        Assert.Equal("weather-mcp", binding.GetProperty("Server").GetString());
        Assert.Equal("get_weather", binding.GetProperty("Tool").GetString());
        Assert.Equal(12.5, binding.GetProperty("ElapsedMs").GetDouble());
        Assert.Equal(1, binding.GetProperty("Candidates").GetArrayLength());
        Assert.Equal("weather-mcp", binding.GetProperty("Candidates")[0].GetProperty("Name").GetString());
        Assert.Equal(1, binding.GetProperty("Candidates")[0].GetProperty("Rank").GetInt32());
        Assert.Equal(1, binding.GetProperty("Attempted").GetArrayLength());
    }
    finally
    {
        Console.SetOut(previousOut);
        Console.SetError(previousError);
        Directory.Delete(root, recursive: true);
    }
}
```

If the emitted casing differs from the assertions, correct the assertions to the actual emitted casing and note the correction in the commit message — the pin's purpose is the exported shape, not the casing style.

- [ ] **Step 4: Run the CLI test**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~RunAsync_MetaRuns_Json_IncludesCapabilityBindingTrajectory`
Expected: PASS (pinning — the CLI already serializes nested evidence).

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Tests/NacosRouterIntegrationTests.cs src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs
git commit -m "test(#234): pin cache-hit trajectories and meta-runs --json export shape

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: Harness replay of exported binding trajectories (AC3)

**Files:**
- Create: `src/OpenClaw.Testing/CapabilityBindingReplay.cs`
- Test: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`

**Interfaces:**
- Consumes: `CapabilityBindingTrajectory` (Task 1), `CapabilitySlotExecutor` + `CapabilityBindingCache` + `McpServerToolRegistry` (existing public types), `MetaCapabilityRefDefinition`/`MetaCapabilityStaticBinding`/`MetaCapabilityIntent` (existing Core skill models).
- Produces: `CapabilityBindingReplayFixture` (`SchemaVersion`, `SessionId`, `Expected`; `static CapabilityBindingReplayFixture FromMetaRun(SessionMetaRunRecord run, string sessionId)`), `CapabilityBindingReplay(fixture)` with `ValueTask<CapabilityBindingReplayResult> RunAsync(McpServerToolRegistry registry, CapabilityBindingCache cache, CancellationToken ct)`, `CapabilityBindingReplayResult { bool Passed; string Message; CapabilityBindingTrajectory? Reproduced }`.

- [ ] **Step 1: Write the failing tests (compile-level RED — harness types do not exist)**

Append to `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` (setup copied from `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure`; the replay itself uses the executor directly, so `CreateRuntime` is only needed to produce the recorded run):

```csharp
[Fact]
public async Task RecordedTrajectory_ReplaysThroughTestingHarness_WithSameBinding()
{
    var state = new NacosRouterFixtureState();
    // ... identical setup to DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure ...
    try
    {
        var session = new Session { Id = "nacos-replay", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        var recorded = Assert.Single(session.MetaRunHistory);

        // Export through the same source-gen context the CLI's meta-runs --json uses.
        var json = JsonSerializer.Serialize(recorded, CoreJsonContext.Default.SessionMetaRunRecord);
        var exported = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionMetaRunRecord)!;

        var fixture = CapabilityBindingReplayFixture.FromMetaRun(exported, session.Id);
        var replay = new CapabilityBindingReplay(fixture);
        var result = await replay.RunAsync(registry, new CapabilityBindingCache(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Message);
        Assert.Equal("weather-mcp", result.Reproduced!.Server);
        Assert.Equal("get_weather", result.Reproduced!.Tool);
        Assert.False(result.Reproduced!.CacheHit);
        Assert.Equal(
            exported.StepResults.Single(s => s.Id == "query").ExecutionEvidence!.CapabilityBinding!.Server,
            result.Reproduced.Server);
    }
    finally { /* identical cleanup */ }
}

[Fact]
public async Task RecordedCacheHitTrajectory_ReplaysThroughTestingHarness_WithCacheHit()
{
    var state = new NacosRouterFixtureState();
    // ... identical setup to DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure ...
    try
    {
        var session = new Session { Id = "nacos-replay-hit", SenderId = "test", ChannelId = "test" };
        var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
        var recordedHit = session.MetaRunHistory[1];
        Assert.True(recordedHit.StepResults.Single(s => s.Id == "query").ExecutionEvidence!.CapabilityBinding!.CacheHit);

        var json = JsonSerializer.Serialize(recordedHit, CoreJsonContext.Default.SessionMetaRunRecord);
        var exported = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionMetaRunRecord)!;

        var result = await new CapabilityBindingReplay(CapabilityBindingReplayFixture.FromMetaRun(exported, session.Id))
            .RunAsync(registry, new CapabilityBindingCache(), TestContext.Current.CancellationToken);

        Assert.True(result.Passed, result.Message);
        Assert.True(result.Reproduced!.CacheHit);
        Assert.Equal("weather-mcp", result.Reproduced.Server);
    }
    finally { /* identical cleanup */ }
}
```

Add `using OpenClaw.Testing;` and `using System.Text.Json;` to the test file's usings if absent.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~RecordedTrajectory_ReplaysThroughTestingHarness|FullyQualifiedName~RecordedCacheHitTrajectory_ReplaysThroughTestingHarness`
Expected: FAIL — build error CS0246: `CapabilityBindingReplayFixture` does not exist. Compile-level RED; proceed.

- [ ] **Step 3: Implement the replay fixture and runner**

Create `src/OpenClaw.Testing/CapabilityBindingReplay.cs`:

```csharp
using ModelContextProtocol.Client;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Testing;

/// <summary>
/// Recorded binding trajectory for one exported capability slot execution
/// (issue #234). Built from a persisted meta-run record — the same shape
/// `openclaw skills meta-runs --json` emits — and replayed against a real
/// executor with a deterministic router harness.
/// </summary>
public sealed class CapabilityBindingReplayFixture
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public CapabilityBindingTrajectory Expected { get; init; } = new();

    /// <summary>
    /// Extracts the replay fixture from an exported meta-run record. Throws
    /// <see cref="InvalidDataException"/> when the run carries no binding trajectory.
    /// </summary>
    public static CapabilityBindingReplayFixture FromMetaRun(SessionMetaRunRecord run, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(run);
        var step = run.StepResults.FirstOrDefault(r => r.ExecutionEvidence?.CapabilityBinding is not null)
            ?? throw new InvalidDataException("The exported meta-run carries no capability binding trajectory.");
        return new CapabilityBindingReplayFixture
        {
            SessionId = sessionId,
            Expected = step.ExecutionEvidence!.CapabilityBinding!
        };
    }
}

public sealed class CapabilityBindingReplayResult
{
    public bool Passed { get; init; }
    public string Message { get; init; } = "";
    public CapabilityBindingTrajectory? Reproduced { get; init; }
}

/// <summary>
/// Replays one recorded capability binding against a real
/// <see cref="CapabilitySlotExecutor"/> and asserts the binding is reproduced.
/// The router behind the supplied registry must be deterministic (the same
/// candidate set the recording observed); a cache-hit fixture is reproduced
/// by seeding the supplied cache with the recorded binding, mirroring how a
/// first execution would have populated it. No LLM is involved.
/// </summary>
public sealed class CapabilityBindingReplay
{
    private readonly CapabilityBindingReplayFixture _fixture;

    public CapabilityBindingReplay(CapabilityBindingReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported replay fixture schema version: {fixture.SchemaVersion}.");
        if (fixture.Expected.Binding is not ("static" or "dynamic"))
            throw new InvalidDataException("Replay fixture binding must be static or dynamic.");
        if (fixture.Expected.Binding == "static" && (fixture.Expected.Server is null || fixture.Expected.Tool is null))
            throw new InvalidDataException("A static replay fixture must carry the recorded server and tool.");
        if (fixture.Expected.Binding == "dynamic" && string.IsNullOrWhiteSpace(fixture.Expected.TaskDescription))
            throw new InvalidDataException("A dynamic replay fixture must carry the recorded task description.");
        if (fixture.Expected.CacheHit && fixture.Expected.Binding == "static")
            throw new InvalidDataException("Static bindings have no session binding cache; a cache-hit fixture must be dynamic.");
        _fixture = fixture;
    }

    public async ValueTask<CapabilityBindingReplayResult> RunAsync(
        McpServerToolRegistry registry, CapabilityBindingCache cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();

        if (_fixture.Expected.CacheHit)
        {
            // Reproduce the recorded hit: the recording bound in an earlier
            // execution of the same session; seeding reproduces that state.
            cache.Set(_fixture.SessionId,
                CapabilityBindingCache.ComputeIntentKey(
                    _fixture.Expected.TaskDescription ?? "",
                    _fixture.Expected.KeyWords,
                    _fixture.Expected.SelectionPolicy == "exact_name" ? "ExactName" : "First"),
                _fixture.Expected.Server ?? "",
                _fixture.Expected.Tool ?? "");
        }

        var executor = new CapabilitySlotExecutor(registry, cache);
        var result = await executor.ExecuteAsync(BuildCapabilityRef(), "{}", _fixture.SessionId, cancellationToken);
        var reproduced = result.BindingTrajectory;
        if (reproduced is null)
            return new CapabilityBindingReplayResult
            {
                Passed = false,
                Message = "Replayed slot produced no binding trajectory."
            };

        var mismatches = new List<string>();
        if (!string.Equals(reproduced.Binding, _fixture.Expected.Binding, StringComparison.Ordinal)) mismatches.Add("binding");
        if (!string.Equals(reproduced.Server, _fixture.Expected.Server, StringComparison.Ordinal)) mismatches.Add("server");
        if (!string.Equals(reproduced.Tool, _fixture.Expected.Tool, StringComparison.Ordinal)) mismatches.Add("tool");
        if (reproduced.CacheHit != _fixture.Expected.CacheHit) mismatches.Add("cacheHit");
        if (!CandidatesEqual(reproduced.Candidates, _fixture.Expected.Candidates)) mismatches.Add("candidates");
        if (!CandidatesEqual(reproduced.Attempted, _fixture.Expected.Attempted)) mismatches.Add("attempted");

        return mismatches.Count == 0
            ? new CapabilityBindingReplayResult { Passed = true, Message = "Recorded binding reproduced.", Reproduced = reproduced }
            : new CapabilityBindingReplayResult
            {
                Passed = false,
                Reproduced = reproduced,
                Message = $"Binding diverged from the recorded trajectory: {string.Join(", ", mismatches)}."
            };
    }

    private MetaCapabilityRefDefinition BuildCapabilityRef()
    {
        var expected = _fixture.Expected;
        if (expected.Binding == "static")
        {
            return new MetaCapabilityRefDefinition
            {
                Binding = "static",
                Static = new MetaCapabilityStaticBinding
                {
                    McpServerName = expected.Server!,
                    ToolName = expected.Tool!
                }
            };
        }

        return new MetaCapabilityRefDefinition
        {
            Binding = "dynamic",
            Intent = new MetaCapabilityIntent
            {
                TaskDescription = expected.TaskDescription!,
                Keywords = (expected.KeyWords ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            },
            SelectionPolicy = expected.SelectionPolicy == "exact_name" ? "exact_name" : "first"
        };
    }

    private static bool CandidatesEqual(
        IReadOnlyList<CapabilityBindingCandidate> actual, IReadOnlyList<CapabilityBindingCandidate> expected)
        => actual.Count == expected.Count &&
           actual.Zip(expected).All(pair =>
               string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
               pair.First.Rank == pair.Second.Rank);
}
```

Note the `CapabilityBindingCache.Set` call can write `(sessionId, intentKey)` entries that `ExecuteAsync` then reads via `TryGet` — the seeding is idempotent and harmless for miss fixtures (it only runs when `Expected.CacheHit`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~RecordedTrajectory_ReplaysThroughTestingHarness|FullyQualifiedName~RecordedCacheHitTrajectory_ReplaysThroughTestingHarness`
Expected: PASS — both replay tests pass.

- [ ] **Step 5: Run the full NacosRouter + executor + harness suites**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests|FullyQualifiedName~CapabilitySlotExecutorTests|FullyQualifiedName~HarnessRegression`
Expected: PASS — no regressions in the touched suites.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Testing/CapabilityBindingReplay.cs src/OpenClaw.Tests/NacosRouterIntegrationTests.cs
git commit -m "feat(#234): replay exported binding trajectories through the Testing harness

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: Documentation (EN + zh)

**Files:**
- Modify: `docs/nacos-mcp-router.md` (header status, new section, stale #234 pointers)
- Modify: `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` (§7.6 + §8)

- [ ] **Step 1: Update the EN guide**

1. In the status header (lines 3-13), extend the status chain: after "**node-level degradation and retry implemented (2026-09-14)** for #233" add "**binding trajectory observability implemented (2026-09-14)** for #234", and replace the final sentence "the remaining replay work (#234) builds on it" with "the binding-trajectory replay work (#234) builds on it".
2. Add a new section after "## Capability slots (issue #231)" and before "## Remaining live acceptance and downstream decisions":

```markdown
## Binding trajectory observability (issue #234)

Every capability slot execution records a binding trajectory on the run's
step evidence (`executionEvidence.capabilityBinding` in `meta-runs --json`):

- `binding` — `static` or `dynamic`;
- intent fields (dynamic): `taskDescription`, `keyWords`, `selectionPolicy`
  (wire values `first` / `exact_name`) and `intentKey` (SHA-256 of the
  normalised intent, the session cache key);
- `cacheHit` — whether the session binding cache supplied the binding;
- `server` / `tool` — the bound pair (null when binding failed);
- `elapsedMs` — the binding phase only (excludes `use_tool`);
- `candidates` — the search Top-N in upstream rank order, and `attempted` —
  the candidates the resolver actually tried to add, as `name` + `rank`.

Upstream search returns no scores (#230), so the trajectory records the
positional `rank` — none are fabricated. Failure trajectories are recorded
too (empty candidates, null server/tool, the step's `failure_code`).

The exported run JSON deserialises through `CoreJsonContext` and replays
offline through `OpenClaw.Testing`: `CapabilityBindingReplayFixture.FromMetaRun`
extracts the recorded trajectory, and `CapabilityBindingReplay` re-executes
the slot against a deterministic router harness and asserts the same binding
(same-input → same-binding; cache-hit fixtures are reproduced by seeding the
cache with the recorded binding). See
`src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` for the loop.
```

3. In "Remaining live acceptance and downstream decisions" item 4 (the paragraph ending "Binding replay remains #234."), replace the ending with: "~~Binding replay remains #234.~~ **Done 2026-09-14**: binding trajectories persist in the run step evidence, `meta-runs --json` exports them, and the OpenClaw.Testing harness replays them with same-binding assertions."
4. In the closing paragraph (after the measurement table), replace "Binding replay remains tracked by #234." with "Binding trajectory observability and offline replay are implemented (#234)."

- [ ] **Step 2: Update the zh design doc**

1. §7.6 可观测性闭环: after the two-item list, add an implementation note:

```markdown
> 实现状态（#234 已实现，2026-09-14）：每次槽位执行的 binding 轨迹随 step executionEvidence 持久化并可由 `openclaw skills meta-runs --json` 导出——含 binding 模式、intent（task_description/key_words/selection_policy 与 intent 键）、缓存命中、选定 server/tool、绑定耗时、Top-N 候选与已尝试候选（name + rank）。上游不返回 score，轨迹只记 rank 不伪造 score（#230 契约）。导出 JSON 经 OpenClaw.Testing 的 `CapabilityBindingReplayFixture.FromMetaRun` / `CapabilityBindingReplay` 离线重放，断言同输入→同绑定。
```

2. §8 实现状态 note: change "Nacos 变更事件订阅仍待实现。" to "绑定轨迹可观测性与离线重放（#234）已实现；Nacos 变更事件订阅仍待实现。"

- [ ] **Step 3: Commit**

```bash
git add docs/nacos-mcp-router.md docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md
git commit -m "docs(#234): document binding trajectory observability and replay

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage (issue #234):**
- ① 扩展 `SessionMetaRunRecord`/`MetaStepExecutionResult` 记录绑定路径 — Task 1 (model + evidence property), Task 2 (producer), Task 3 (mapping into `MetaStepExecutionResult`). ✓
- ② `meta-runs --json` 输出 binding 轨迹 — Task 4 Step 3-4 (export shape pin; no CLI change needed because nested evidence already serializes). ✓
- ③ 轨迹 JSON 可作 OpenClaw.Testing harness fixture 重放且绑定结果一致 — Task 5 (`CapabilityBindingReplayFixture`/`CapabilityBindingReplay` + e2e replay tests). ✓
- ④ 全量测试通过 — finishing-a-development-branch runs the full suite after all tasks; baseline environmental failures documented in Global Constraints. ✓
- Issue text says "Top-5 候选（name+score）" — upstream has no score; plan records `name` + `rank` and documents the deviation (Task 1 doc comment, Task 6 docs, report). ✓
- Cache-hit vs miss distinguishable (AC2) — Task 2 (executor), Task 4 (e2e + export). ✓

**Placeholder scan:** No TBD/TODO. Setup blocks in Tasks 3-5 say "copy verbatim from the named existing test" — that is an explicit, unambiguous instruction referencing existing code, not a placeholder; the plan would otherwise be thousands of duplicated lines. Commit messages, commands, and code are concrete.

**Type consistency:** `CapabilityBindingTrajectory` is get/set (built progressively by the executor — matches `Session.LastUpdatedAtUtc` precedent); `SessionMetaStepExecutionEvidence.CapabilityBinding` is init (constructed once in the runtime mapping). `ToolExecutionResult.BindingTrajectory` name is used identically in Tasks 2, 3, 5. `ResolveCoreAsync`'s new tuple order `(Binding, Failure, Candidates)` is used consistently in Task 2 Step 4 (tool caller discards via `_`) and Task 2 Step 5 (executor destructures). `FromMetaRun(SessionMetaRunRecord run, string sessionId)` signature matches all three call sites in Task 5. `MetaCapabilityRefDefinition`/`MetaCapabilityStaticBinding`/`MetaCapabilityIntent` member names verified against `src/OpenClaw.Core/Skills/SkillModels.cs:305-352`.
