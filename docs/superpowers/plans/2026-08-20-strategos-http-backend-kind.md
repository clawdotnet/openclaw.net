# `Kind: "strategos-http"` Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote `Kind: "strategos-http"` to a first-class workflow backend kind alongside `maf-durable-http`, dispatching it in the gateway's `AgentWorkflowRegistry` to a thin `StrategosHttpWorkflowRunner` that composes the existing `MafDurableHttpWorkflowRunner`.

**Architecture:** `StrategosHttpWorkflowRunner` is composition-only (has-a) — it owns an inner `MafDurableHttpWorkflowRunner` and delegates every `IAgentWorkflowRunner` method to it. The runner exists purely so the gateway reports `Kind = "strategos-http"` to its consumers (e.g. the integration discovery endpoint, the runtime event store's `backendId` metadata), which makes Strategos-backed workflows distinguishable from generic maf-durable-http backends in logs and UI. No host-side change: the P0 sidecar still speaks the maf-durable-http contract on the wire; the gateway tags it as Strategos because that's what the operator configured.

**Tech Stack:** .NET 10, ASP.NET Core, Strategos 2.10.0, Marten 9.9.0, Wolverine 6.12.0, MEAI 10.7.0, xUnit v3 3.2.2, NSubstitute 5.3.0.

**Spec:** `docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md` (P1 follow-up section) + this plan's user message: "事后把 `Kind: 'strategos-http'` 升级为一等后端类型（一行注册表分支 + `StrategosHttpWorkflowRunner` 组合既有 `MafDurableHttpWorkflowRunner`）".

## Global Constraints

- `TreatWarningsAsErrors=true` everywhere — every change must build with 0 warnings.
- No gateway behavior change for existing `Kind = "maf-durable-http"` callers; this is a pure addition.
- Runner and registry stay `internal` (existing convention); tests live in `src/OpenClaw.Tests/` which already has `InternalsVisibleTo("OpenClaw.Tests")`.
- The new runner is composition-only (`has-a`), never inheritance — `MafDurableHttpWorkflowRunner` is `internal sealed` and we don't want to leak its surface.
- `AgentWorkflowBackendKinds.StrategosHttp` lives in `OpenClaw.Core.Models` next to the existing `MafDurableHttp` constant — single source of truth for kind strings.
- Tests must be byte-exact on values: `StrategosHttp == "strategos-http"`.

---

## File Structure

| File | Role | Change |
|---|---|---|
| `src/OpenClaw.Core/Models/WorkflowModels.cs` | `AgentWorkflowBackendKinds` constants | Add `StrategosHttp = "strategos-http"` |
| `src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs` | New `IAgentWorkflowRunner` implementation | Create (composes `MafDurableHttpWorkflowRunner`) |
| `src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs` | Dispatch runner by `Kind` | Modify: switch on kind, throw on unknown |
| `src/OpenClaw.Tests/Workflows/StrategosHttpWorkflowRunnerTests.cs` | Unit tests for the new runner | Create |
| `src/OpenClaw.Tests/Workflows/AgentWorkflowRegistryTests.cs` | Unit tests for kind dispatch + unknown-kind rejection | Create |

---

### Task 1: Add the `StrategosHttp` kind constant

**Files:**
- Modify: `src/OpenClaw.Core/Models/WorkflowModels.cs:15-18`
- Create: `src/OpenClaw.Tests/Workflows/WorkflowBackendKindsTests.cs`

**Interfaces:**
- Consumes: nothing (pure data constant)
- Produces: `OpenClaw.Core.Models.AgentWorkflowBackendKinds.StrategosHttp == "strategos-http"` (consumed by Task 2 + Task 3)

- [ ] **Step 1: Write the failing test**

Create `src/OpenClaw.Tests/Workflows/WorkflowBackendKindsTests.cs`:

```csharp
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class WorkflowBackendKindsTests
{
    [Fact]
    public void StrategosHttp_Is_StrategosHttp_Literal()
        => Assert.Equal("strategos-http", AgentWorkflowBackendKinds.StrategosHttp);
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter WorkflowBackendKindsTests`
Expected: FAIL with `AgentWorkflowBackendKinds.StrategosHttp` not found.

- [ ] **Step 3: Add the constant**

In `src/OpenClaw.Core/Models/WorkflowModels.cs`, after the existing `MafDurableHttp` constant (around line 17), add:

```csharp
public const string StrategosHttp = "strategos-http";
```

So the class reads:

```csharp
public static class AgentWorkflowBackendKinds
{
    public const string MafDurableHttp = "maf-durable-http";
    public const string StrategosHttp = "strategos-http";
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter WorkflowBackendKindsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/WorkflowModels.cs src/OpenClaw.Tests/Workflows/WorkflowBackendKindsTests.cs
git commit -m "feat(gateway): add AgentWorkflowBackendKinds.StrategosHttp constant

Adds the new 'strategos-http' kind alongside 'maf-durable-http' so
AgentWorkflowRegistry and StrategosHttpWorkflowRunner can dispatch
on it without a magic string."
```

---

### Task 2: `StrategosHttpWorkflowRunner` (composes `MafDurableHttpWorkflowRunner`)

**Files:**
- Create: `src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs`
- Create: `src/OpenClaw.Tests/Workflows/StrategosHttpWorkflowRunnerTests.cs`

**Interfaces:**
- Consumes: `IAgentWorkflowRunner` (existing), `MafDurableHttpWorkflowRunner` (existing `internal sealed`), `AgentWorkflowBackendKinds.StrategosHttp` (Task 1).
- Produces: `StrategosHttpWorkflowRunner(string backendId, WorkflowBackendConfig config, RuntimeEventStore events, ILogger<StrategosHttpWorkflowRunner> logger)` constructor; `IAgentWorkflowRunner` surface identical to `MafDurableHttpWorkflowRunner`. `GetSummary().Kind` returns `AgentWorkflowBackendKinds.StrategosHttp`.

- [ ] **Step 1: Write the failing test**

Create `src/OpenClaw.Tests/Workflows/StrategosHttpWorkflowRunnerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class StrategosHttpWorkflowRunnerTests
{
    [Fact]
    public void GetSummary_Reports_StrategosHttp_Kind()
    {
        var runner = new StrategosHttpWorkflowRunner(
            backendId: "strategos",
            config: NewConfig(),
            events: new RuntimeEventStore(),
            logger: NullLogger<StrategosHttpWorkflowRunner>.Instance);

        var summary = runner.GetSummary();

        Assert.Equal(AgentWorkflowBackendKinds.StrategosHttp, summary.Kind);
        Assert.Equal("strategos", summary.Id);
        Assert.Equal("durable-agent-review", summary.WorkflowName);
    }

    [Fact]
    public void BackendId_Exposes_Configured_Id()
    {
        var runner = new StrategosHttpWorkflowRunner(
            backendId: "strategos",
            config: NewConfig(),
            events: new RuntimeEventStore(),
            logger: NullLogger<StrategosHttpWorkflowRunner>.Instance);

        Assert.Equal("strategos", runner.BackendId);
        Assert.Equal("durable-agent-review", runner.WorkflowId);
    }

    private static WorkflowBackendConfig NewConfig() => new()
    {
        Kind = AgentWorkflowBackendKinds.StrategosHttp,
        WorkflowName = "durable-agent-review",
        BaseUrl = "http://localhost:8080/",
        Enabled = true,
    };
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter StrategosHttpWorkflowRunnerTests`
Expected: FAIL with `StrategosHttpWorkflowRunner` type not found.

- [ ] **Step 3: Implement the runner**

Create `src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs`:

```csharp
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway.Workflows;

// Alias runner for Kind="strategos-http". Composes MafDurableHttpWorkflowRunner
// because the on-the-wire contract is identical (maf-durable-http); this type
// exists only to tag the backend in summary, events, and runtime metadata as
// Strategos-backed so observability + UIs can distinguish it from generic
// maf-durable-http backends. Composition (has-a) over inheritance because
// MafDurableHttpWorkflowRunner is internal sealed.
internal sealed class StrategosHttpWorkflowRunner : IAgentWorkflowRunner, IDisposable
{
    private readonly MafDurableHttpWorkflowRunner _inner;

    public StrategosHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        ILogger<StrategosHttpWorkflowRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(config, nameof(config));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        _inner = new MafDurableHttpWorkflowRunner(
            backendId,
            config,
            events,
            loggerFactory: NullLoggerFactory.Instance); // inner logger is suppressed; outer logger already covers this runner

        BackendId = _inner.BackendId;
        WorkflowId = _inner.WorkflowId;
    }

    public string BackendId { get; }

    public string WorkflowId { get; }

    public AgentWorkflowBackendSummary GetSummary()
    {
        // Override Kind: inner returns MafDurableHttp. We re-emit with StrategosHttp
        // so observers see the configured kind, not the implementation kind.
        var innerSummary = _inner.GetSummary();
        return new AgentWorkflowBackendSummary
        {
            Id = innerSummary.Id,
            Kind = AgentWorkflowBackendKinds.StrategosHttp,
            WorkflowName = innerSummary.WorkflowName,
            DisplayName = innerSummary.DisplayName,
            Enabled = innerSummary.Enabled,
        };
    }

    public Task<AgentWorkflowRunResult> RunAsync(AgentWorkflowRequest request, CancellationToken cancellationToken = default)
        => _inner.RunAsync(request, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> GetAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(runId, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> RespondAsync(string runId, AgentWorkflowResponse response, CancellationToken cancellationToken = default)
        => _inner.RespondAsync(runId, response, cancellationToken);

    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.StreamAsync(runId, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
```

Required `using` lines:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter StrategosHttpWorkflowRunnerTests`
Expected: PASS (2 / 2).

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs src/OpenClaw.Tests/Workflows/StrategosHttpWorkflowRunnerTests.cs
git commit -m "feat(gateway): add StrategosHttpWorkflowRunner composing MafDurableHttpWorkflowRunner

Kind='strategos-http' is operationally identical to maf-durable-http on
the wire; the runner exists to tag the backend in GetSummary, runtime
events, and observability metadata as Strategos-backed. Composition
(has-a) over inheritance because MafDurableHttpWorkflowRunner is
internal sealed. GetSummary overrides Kind to 'strategos-http'; every
other IAgentWorkflowRunner method delegates verbatim to the inner
runner. IDisposable.Dispose() forwards to the inner."
```

---

### Task 3: Registry dispatch + unknown-kind regression test

**Files:**
- Modify: `src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs:25-41`
- Create: `src/OpenClaw.Tests/Workflows/AgentWorkflowRegistryTests.cs`

**Interfaces:**
- Consumes: `AgentWorkflowBackendKinds.StrategosHttp` (Task 1), `StrategosHttpWorkflowRunner` (Task 2), existing `MafDurableHttpWorkflowRunner`.
- Produces: `AgentWorkflowRegistry(GatewayConfig, RuntimeEventStore, ILoggerFactory)` constructor — same signature, returns `IAgentWorkflowRunner` for both kinds; throws `InvalidOperationException` on any other kind.

- [ ] **Step 1: Write the failing tests**

Create `src/OpenClaw.Tests/Workflows/AgentWorkflowRegistryTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class AgentWorkflowRegistryTests
{
    [Fact]
    public void Registry_Instantiates_MafDurableHttp_Runner_For_Default_Kind()
    {
        var registry = NewRegistry(("default", new WorkflowBackendConfig
        {
            Kind = AgentWorkflowBackendKinds.MafDurableHttp,
            WorkflowName = "default-wf",
            BaseUrl = "http://localhost:9000/",
        }));

        var summary = Assert.Single(registry.List());
        Assert.Equal(AgentWorkflowBackendKinds.MafDurableHttp, summary.Kind);
    }

    [Fact]
    public void Registry_Instantiates_StrategosHttp_Runner_For_StrategosHttp_Kind()
    {
        var registry = NewRegistry(("strategos", new WorkflowBackendConfig
        {
            Kind = AgentWorkflowBackendKinds.StrategosHttp,
            WorkflowName = "durable-agent-review",
            BaseUrl = "http://localhost:8080/",
        }));

        var summary = Assert.Single(registry.List());
        Assert.Equal(AgentWorkflowBackendKinds.StrategosHttp, summary.Kind);
        Assert.Equal("strategos", summary.Id);
    }

    [Fact]
    public void Registry_Rejects_Unknown_Kind()
    {
        var config = NewConfig(("bogus", new WorkflowBackendConfig
        {
            Kind = "weird-thing",
            WorkflowName = "bogus",
            BaseUrl = "http://localhost:9000/",
        }));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AgentWorkflowRegistry(config, new RuntimeEventStore(), NullLoggerFactory.Instance));
        Assert.Contains("weird-thing", ex.Message);
    }

    private static AgentWorkflowRegistry NewRegistry(params (string id, WorkflowBackendConfig cfg)[] backends)
    {
        var config = NewConfig(backends);
        return new AgentWorkflowRegistry(config, new RuntimeEventStore(), NullLoggerFactory.Instance);
    }

    private static OpenClaw.Gateway.GatewayConfig NewConfig(params (string id, WorkflowBackendConfig cfg)[] backends)
    {
        var wf = new WorkflowsConfig { Enabled = true };
        foreach (var (id, cfg) in backends)
            wf.Backends[id] = cfg;
        var config = new OpenClaw.Gateway.GatewayConfig { Workflows = wf };
        return config;
    }
}
```

- [ ] **Step 2: Run the tests and verify the failing cases**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentWorkflowRegistryTests`
Expected: `Registry_Instantiates_StrategosHttp_Runner_For_StrategosHttp_Kind` FAILs with the registry throwing `"Unsupported workflow backend kind 'strategos-http'…"`.

- [ ] **Step 3: Update the registry**

In `src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs`, replace the existing block at lines 25-41 (the `var kind = …` block through the `_runners[…] = new MafDurableHttpWorkflowRunner(...)` line) with:

```csharp
var kind = string.IsNullOrWhiteSpace(backendConfig.Kind)
    ? AgentWorkflowBackendKinds.MafDurableHttp
    : backendConfig.Kind.Trim();

var normalizedBackendId = backendId.Trim();
if (_runners.ContainsKey(normalizedBackendId))
    throw new InvalidOperationException($"Duplicate workflow backend id '{normalizedBackendId}' after trimming whitespace.");

_runners[normalizedBackendId] = kind switch
{
    AgentWorkflowBackendKinds.MafDurableHttp => new MafDurableHttpWorkflowRunner(
        normalizedBackendId,
        backendConfig,
        events,
        loggerFactory.CreateLogger<MafDurableHttpWorkflowRunner>()),
    AgentWorkflowBackendKinds.StrategosHttp => new StrategosHttpWorkflowRunner(
        normalizedBackendId,
        backendConfig,
        events,
        loggerFactory.CreateLogger<StrategosHttpWorkflowRunner>()),
    _ => throw new InvalidOperationException(
        $"Unsupported workflow backend kind '{kind}' for backend '{normalizedBackendId}'.")
};
```

- [ ] **Step 4: Run the tests and verify they all pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentWorkflowRegistryTests`
Expected: PASS (3 / 3).

- [ ] **Step 5: Run the full test suite to verify no regressions**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --nologo`
Expected: previous passing tests still pass, no new failures.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs src/OpenClaw.Tests/Workflows/AgentWorkflowRegistryTests.cs
git commit -m "feat(gateway): AgentWorkflowRegistry dispatches Kind='strategos-http'

Switches the per-backend runner instantiation from a single-line throw
on unknown kind to a switch over AgentWorkflowBackendKinds:
- MafDurableHttp -> existing MafDurableHttpWorkflowRunner
- StrategosHttp  -> new StrategosHttpWorkflowRunner (composition)
- anything else  -> InvalidOperationException (preserved regression)

Registry surface (List / RunAsync / GetAsync / RespondAsync) is
unchanged; only the dispatcher branch grows."
```

---

## Self-Review

**1. Spec coverage** — user message says: (a) upgrade `Kind: "strategos-http"` to first-class, (b) one-line registry branch, (c) `StrategosHttpWorkflowRunner` composes existing `MafDurableHttpWorkflowRunner`. ✅ Task 1 adds the kind, Task 2 builds the runner as composition (has-a), Task 3 wires the dispatch in the registry.

**2. Placeholder scan** — no `TBD` / `TODO` / "implement later". Every code step has a full file. The `RuntimeEventStore` and `GatewayConfig` references come from existing types (Task 3 test verifies the symbols exist at build).

**4. Type consistency** — `StrategosHttpWorkflowRunner` constructor signature matches `MafDurableHttpWorkflowRunner`'s public shape (backendId / config / events / logger); `GetSummary` overrides `Kind` only; `BackendId` / `WorkflowId` exposed identically. Registry dispatch uses the `switch` expression with the same constructor arg ordering for both branches.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-20-strategos-http-backend-kind.md`. Three tasks, each independently testable, ~3 commits total.

Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?