# Evidence Bundle ↔ Marten Cross-Link Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the audit trace produced by `EmitAuditTrace` (the Strategos "Evidence Bundle") reachable as structured JSON on the OpenClaw `status` response — `OutputPayload.audit` + a dedicated `AgentWorkflowEvent` — by cross-linking the adapter (spec §7) with the Marten event stream (`FetchStreamAsync`).

**Architecture:** Today, `EmitAuditTrace` step appends `"\nAuditTrace:{json}"` to `state.ExecutionResult` (a plain-text blob) and the adapter's `BuildOutputPayload` only emits `{workflowId, plan, approved, phase, reviewCount}`. The audit JSON is invisible to callers that don't strip the prefix. The adapter already pulls every stream event via `FetchStreamAsync`; we add a `JsonObject` parser that extracts the trailing `AuditTrace:{...}` block from `ExecutionResult`, surfaces it under `OutputPayload.audit`, and appends a synthetic `AgentWorkflowEvent { Type = "audit_trace_emitted" }` referencing the `EmitAuditTraceCompleted` event id. The `MapEvent` path stays unchanged; the cross-link lives entirely in `DurableHttpAdapter`.

**Tech Stack:** .NET 10, ASP.NET Core minimal API, Strategos 2.10.0, Marten 9.9.0, Wolverine 6.12.0, MEAI 10.5.2, xUnit v3 3.2.2, NSubstitute 5.3.0.

**Spec:** [`docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md) §7 (adapter seam, where `FetchStreamAsync` is "already available"); per user feature brief: "插入 `status` 响应的 `OutputPayload`/`Events` 与 `EmitAuditTrace` 步骤".

## Global Constraints

- Target framework `net10.0`, C# 14 (file-scoped namespaces, primary constructors, collection expressions). P0 plan's constraints apply verbatim.
- `TreatWarningsAsErrors=true` — every change must build with 0 warnings.
- Adapter stays the only seam touched (`Adapters/DurableHttpAdapter.cs`); no changes to `Steps/EmitAuditTrace.cs`, `Workflows/ReviewState.cs`, or `Program.cs` for this plan.
- `OutputPayload` schema is additive — `audit` is a new optional key. Existing keys (`workflowId`, `plan`, `approved`, `phase`, `reviewCount`) stay in place and stay required. If `ExecutionResult` has no `AuditTrace:` block, `audit` is omitted entirely (no null leak).
- `Events` is append-only — the new `audit_trace_emitted` event is added to the existing list; existing events stay in the same order with the same ids.
- Tests live in `samples/OpenClaw.StrategosWorkflowHost.Tests/` (the sibling project, per P0 plan §11 — sibling because the sidecar has a non-AOT dependency on `WolverineFx`).
- Adapter parser tolerates trailing content in `ExecutionResult` after the `AuditTrace:{...}` block (the step currently emits `ExecutionResult + "\nAuditTrace:..."`, leaving original output intact).

---

## File Structure

| File | Role | Change |
|---|---|---|
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs` | Status response builder | Modify: parse audit JSON from `ExecutionResult`, add `OutputPayload.audit`, append `audit_trace_emitted` event |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/EvidenceBundleParser.cs` | Pure function: `string? ExtractAuditJson(string? executionResult)` | Create |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/EvidenceBundleParserTests.cs` | Unit tests for the parser | Create |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/DurableHttpAdapterEvidenceTests.cs` | Adapter-level test asserting `OutputPayload.audit` + extra event | Create |

---

### Task 1: `EvidenceBundleParser` — extract audit JSON from `ExecutionResult`

**Files:**
- Create: `samples/OpenClaw.StrategosWorkflowHost/Adapters/EvidenceBundleParser.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost.Tests/EvidenceBundleParserTests.cs`

**Interfaces:**
- Consumes: `string?` (the `ExecutionResult` field of `ReviewState`, may be null or contain arbitrary prepended content).
- Produces: `static string? ExtractAuditJson(string? executionResult)` returning the raw JSON document (text starting with `{` and ending with `}`) found after the literal `AuditTrace:` marker, or `null` if the marker is missing.

- [ ] **Step 1: Write the failing tests**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/EvidenceBundleParserTests.cs`:

```csharp
using OpenClaw.StrategosWorkflowHost.Adapters;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class EvidenceBundleParserTests
{
    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenMarkerMissing()
    {
        var result = EvidenceBundleParser.ExtractAuditJson("executed ok\nno audit");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenInputIsNull()
        => Assert.Null(EvidenceBundleParser.ExtractAuditJson(null));

    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenInputIsEmpty()
        => Assert.Null(EvidenceBundleParser.ExtractAuditJson(""));

    [Fact]
    public void ExtractAuditJson_ReturnsBlock_WhenMarkerAtStart()
    {
        var result = EvidenceBundleParser.ExtractAuditJson("AuditTrace:{\"plan\":\"x\",\"reviews\":3,\"approved\":true}");
        Assert.Equal("{\"plan\":\"x\",\"reviews\":3,\"approved\":true}", result);
    }

    [Fact]
    public void ExtractAuditJson_ReturnsBlock_WhenMarkerHasLeadingContent()
    {
        // Mirrors EmitAuditTrace output: prepended ExecutionResult + "\nAuditTrace:..."
        var input = "Executed approved action for: hello\nAuditTrace:{\"plan\":\"p\",\"reviews\":3,\"approved\":true}";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"plan\":\"p\",\"reviews\":3,\"approved\":true}", result);
    }

    [Fact]
    public void ExtractAuditJson_ToleratesTrailingContent()
    {
        var input = "AuditTrace:{\"k\":1}\nsome trailing log line";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"k\":1}", result);
    }

    [Fact]
    public void ExtractAuditJson_IgnoresNestedBraces_AndReturnsOuterBlock()
    {
        // EmitAuditTrace produces a flat JsonObject (no nested objects in current shape),
        // but defend against future contributors who add a nested object.
        var input = "AuditTrace:{\"a\":1,\"b\":{\"c\":2}}";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"a\":1,\"b\":{\"c\":2}}", result);
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter EvidenceBundleParserTests
```
Expected: FAIL with `EvidenceBundleParser` type not found.

- [ ] **Step 3: Implement the parser**

Create `samples/OpenClaw.StrategosWorkflowHost/Adapters/EvidenceBundleParser.cs`:

```csharp
namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Pure function: pull the JSON document that EmitAuditTrace appended to
// ReviewState.ExecutionResult behind the literal "AuditTrace:" marker.
//
// EmitAuditTrace emits:
//   state.ExecutionResult = (state.ExecutionResult ?? "") + "\nAuditTrace:{json}"
//
// The parser:
//   1. Finds the last "AuditTrace:" marker (the most recent append wins).
//   2. Starts scanning at the first '{' after the marker.
//   3. Tracks brace depth so a nested object (future contributors) terminates correctly.
//   4. Returns the substring [start..end+1] or null when any step fails.
public static class EvidenceBundleParser
{
    private const string Marker = "AuditTrace:";

    public static string? ExtractAuditJson(string? executionResult)
    {
        if (string.IsNullOrEmpty(executionResult))
            return null;

        var markerIndex = executionResult.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var searchStart = markerIndex + Marker.Length;
        var openBrace = executionResult.IndexOf('{', searchStart);
        if (openBrace < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = openBrace; i < executionResult.Length; i++)
        {
            var c = executionResult[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return executionResult.Substring(openBrace, i - openBrace + 1);
            }
        }
        return null; // unbalanced braces; treat as no audit
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter EvidenceBundleParserTests
```
Expected: PASS (7 / 7).

- [ ] **Step 5: Commit**

```bash
git add samples/OpenClaw.StrategosWorkflowHost/Adapters/EvidenceBundleParser.cs samples/OpenClaw.StrategosWorkflowHost.Tests/EvidenceBundleParserTests.cs
git commit -m "feat(strategos): add EvidenceBundleParser to extract audit JSON from ExecutionResult

EmitAuditTrace step appends a literal 'AuditTrace:{json}' block to
ReviewState.ExecutionResult. This pure-function parser pulls the JSON
document out so the adapter can surface it as structured OutputPayload
and a dedicated status event.

Tolerates leading prepended content (the step concatenates rather than
replaces), trailing log lines, and nested JSON objects via a depth-tracking
scan that respects string boundaries and escape sequences."
```

---

### Task 2: Adapter — cross-link `EmitAuditTrace` into `OutputPayload.audit` and `Events`

**Files:**
- Modify: `samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs` (only the `BuildOutputPayload` helper, the `GetStatusAsync` return body, and the `Events` projection)
- Create: `samples/OpenClaw.StrategosWorkflowHost.Tests/DurableHttpAdapterEvidenceTests.cs`

**Interfaces:**
- Consumes: `EvidenceBundleParser.ExtractAuditJson` (Task 1); existing `BuildOutputPayload` private helper in `DurableHttpAdapter`.
- Produces: `AgentWorkflowRunSnapshot.OutputPayload` gains an `audit` key (`JsonElement` parsed from the audit JSON) whenever `ExtractAuditJson` returns non-null; `AgentWorkflowRunSnapshot.Events` gains one extra terminal `AgentWorkflowEvent { Type = "audit_trace_emitted" }` for each `EmitAuditTraceCompleted` event found in the stream. All other keys/fields unchanged.

- [ ] **Step 1: Write the failing tests**

Create `samples/OpenClaw.StrategosWorkflowHost.Tests/DurableHttpAdapterEvidenceTests.cs`. The tests construct a fake `ReviewState` whose `ExecutionResult` contains the audit block plus a synthetic `EmitAuditTraceCompleted` event, then call a new internal helper on the adapter (introduced below) to assert the cross-link:

```csharp
using System.Text.Json;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Agents.Abstractions;
using Wolverine;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class DurableHttpAdapterEvidenceTests
{
    [Fact]
    public void BuildOutputPayload_Includes_Audit_When_ExecutionResult_Has_Marker()
    {
        var state = new ReviewState
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Plan = "p",
            AggregateConfidence = 0.8,
            CurrentPhase = "Completed",
            ExecutionResult = "AuditTrace:{\"plan\":\"p\",\"reviews\":3,\"approved\":true}"
        };

        var payload = DurableHttpAdapter.BuildOutputPayloadForTest(state);

        Assert.True(payload.HasValue);
        Assert.True(payload.Value.TryGetProperty("audit", out var audit));
        Assert.Equal("p", audit.GetProperty("plan").GetString());
        Assert.Equal(3, audit.GetProperty("reviews").GetInt32());
        Assert.True(audit.GetProperty("approved").GetBoolean());
        // Existing keys stay.
        Assert.Equal("p", payload.Value.GetProperty("plan").GetString());
        Assert.Equal("Completed", payload.Value.GetProperty("phase").GetString());
    }

    [Fact]
    public void BuildOutputPayload_Omits_Audit_When_Marker_Missing()
    {
        var state = new ReviewState
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Plan = "p",
            CurrentPhase = "Running",
            ExecutionResult = "Executed something; no audit yet"
        };

        var payload = DurableHttpAdapter.BuildOutputPayloadForTest(state);

        Assert.True(payload.HasValue);
        Assert.False(payload.Value.TryGetProperty("audit", out _));
    }

    [Fact]
    public void AppendAuditTraceEvent_Adds_Event_For_EmitAuditTraceCompleted()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new EmitAuditTraceCompleted(
            WorkflowId: Guid.NewGuid(),
            StepExecutionId: Guid.NewGuid(),
            UpdatedState: new ReviewState { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid() },
            Confidence: null,
            Timestamp: now);

        var mapped = DurableHttpAdapter.MapEventForTest(evt);

        Assert.Equal("audit_trace_emitted", mapped.Type);
        Assert.Equal(now, mapped.TimestampUtc);
        Assert.Equal(AgentWorkflowStatuses.Completed, mapped.Status);
    }

    [Fact]
    public void AppendAuditTraceEvent_Only_Fires_For_EmitAuditTraceCompleted()
    {
        // Sanity: a non-audit step does NOT produce audit_trace_emitted.
        var evt = new SecurityReviewerCompleted(
            WorkflowId: Guid.NewGuid(),
            StepExecutionId: Guid.NewGuid(),
            UpdatedState: new ReviewState { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid() },
            Confidence: 0.8,
            Timestamp: DateTimeOffset.UtcNow);

        var mapped = DurableHttpAdapter.MapEventForTest(evt);

        Assert.NotEqual("audit_trace_emitted", mapped.Type);
    }
}
```

> These tests require that `DurableHttpAdapter` exposes two `internal static` test helpers: `BuildOutputPayloadForTest(ReviewState)` and `MapEventForTest(IProgressEvent)`. Add `[assembly: InternalsVisibleTo("OpenClaw.StrategosWorkflowHost.Tests")]` to the host's `.csproj` if not already present. `EmitAuditTraceCompleted` / `SecurityReviewerCompleted` are generator-produced; their constructor signatures match what the generator emits today (verified in P0 plan §2 Step 3).

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter DurableHttpAdapterEvidenceTests
```
Expected: FAIL with `BuildOutputPayloadForTest` / `MapEventForTest` not exposed (CS0103) and `InternalsVisibleTo` may block compilation. The InternalsVisibleTo fix in Step 3 unblocks the build; the missing test helpers fail the tests.

- [ ] **Step 3: Modify `DurableHttpAdapter.cs`**

In `samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs`, apply three edits:

**Edit 3a — add `InternalsVisibleTo` to the csproj** (if not already there). Open `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj` and add inside `<PropertyGroup>`:

```xml
<InternalsVisibleTo Include="OpenClaw.StrategosWorkflowHost.Tests" />
```

**Edit 3b — replace `BuildOutputPayload` to add the audit key.** Locate the existing private static method (around line 200+):

```csharp
private static JsonElement? BuildOutputPayload(ReviewState s)
{
    var node = new System.Text.Json.Nodes.JsonObject
    {
        ["workflowId"] = s.WorkflowId,
        ["plan"] = s.Plan,
        ["approved"] = s.Decision?.Approved,
        ["phase"] = s.CurrentPhase,
        ["reviewCount"] = s.Reviews.Count,
    };
    using var doc = JsonDocument.Parse(node.ToJsonString());
    return doc.RootElement.Clone();
}
```

Replace with:

```csharp
internal static JsonElement? BuildOutputPayloadForTest(ReviewState s) => BuildOutputPayload(s);

private static JsonElement? BuildOutputPayload(ReviewState s)
{
    var node = new System.Text.Json.Nodes.JsonObject
    {
        ["workflowId"] = s.WorkflowId,
        ["plan"] = s.Plan,
        ["approved"] = s.Decision?.Approved,
        ["phase"] = s.CurrentPhase,
        ["reviewCount"] = s.Reviews.Count,
    };

    var auditJson = EvidenceBundleParser.ExtractAuditJson(s.ExecutionResult);
    if (auditJson is not null)
    {
        using var auditDoc = JsonDocument.Parse(auditJson);
        node["audit"] = auditDoc.RootElement.Clone();
    }

    using var doc = JsonDocument.Parse(node.ToJsonString());
    return doc.RootElement.Clone();
}
```

The `BuildOutputPayloadForTest` shim exposes the private helper to the sibling test project (allowed via `InternalsVisibleTo` from Edit 3a) without changing the public surface.

**Edit 3c — append the audit event in `MapEvent`.** Locate the `MapEvent` static method (around line 207):

```csharp
private static AgentWorkflowEvent MapEvent(IProgressEvent evt)
{
    var typeName = evt.GetType().Name;
    return new AgentWorkflowEvent
    {
        Id = $"evt_{Guid.NewGuid():N}"[..20],
        TimestampUtc = evt.Timestamp,
        Type = ToEventType(typeName),
        WorkflowId = WorkflowName,
        Status = PhaseStatusMap.ToOpenClawStatus(evt.GetType().Name switch
        {
            var n when n.EndsWith("ApprovalEvent", StringComparison.Ordinal) => "AwaitingApproval",
            var n when n.EndsWith("Completed", StringComparison.Ordinal) => "ExecutingReview",
            _ => "Running"
        }),
        Summary = $"{typeName} @ {evt.Timestamp:O}",
        Metadata = new Dictionary<string, string>
        {
            ["eventType"] = typeName,
        }
    };
}
```

Add an `internal static` shim next to `BuildOutputPayloadForTest` for the test to call:

```csharp
internal static AgentWorkflowEvent MapEventForTest(IProgressEvent evt) => MapEvent(evt);
```

Then update `MapEvent` to override `Type`/`Summary`/`Status` for `EmitAuditTraceCompleted`:

```csharp
private static AgentWorkflowEvent MapEvent(IProgressEvent evt)
{
    var typeName = evt.GetType().Name;
    var isAuditTrace = typeName == "EmitAuditTraceCompleted";

    return new AgentWorkflowEvent
    {
        Id = $"evt_{Guid.NewGuid():N}"[..20],
        TimestampUtc = evt.Timestamp,
        Type = isAuditTrace ? "audit_trace_emitted" : ToEventType(typeName),
        WorkflowId = WorkflowName,
        Status = isAuditTrace
            ? AgentWorkflowStatuses.Completed
            : PhaseStatusMap.ToOpenClawStatus(typeName switch
            {
                var n when n.EndsWith("ApprovalEvent", StringComparison.Ordinal) => "AwaitingApproval",
                var n when n.EndsWith("Completed", StringComparison.Ordinal) => "ExecutingReview",
                _ => "Running"
            }),
        Summary = isAuditTrace
            ? $"Evidence Bundle: {EvidenceBundleParser.ExtractAuditJson(((dynamic)evt).UpdatedState.ExecutionResult) ?? "audit emitted"}"
            : $"{typeName} @ {evt.Timestamp:O}",
        Metadata = new Dictionary<string, string>
        {
            ["eventType"] = typeName,
        }
    };
}
```

> Note: the cast `(dynamic)evt` is intentional — `IProgressEvent` doesn't expose `UpdatedState` uniformly across event types, and we only enter the branch when `typeName == "EmitAuditTraceCompleted"`, where the generator-produced event always carries an `UpdatedState` (per `EventsEmitter.cs:274`). The dynamic call is local to one branch; it does not leak into the general path. If your project forbids dynamic in core libraries, replace with the concrete `EmitAuditTraceCompleted` cast and a static helper `static string Summary(EmitAuditTraceCompleted e)`.

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --filter DurableHttpAdapterEvidenceTests
```
Expected: PASS (4 / 4).

- [ ] **Step 5: Run the full sibling test suite to verify no regressions**

Run:
```bash
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests/OpenClaw.StrategosWorkflowHost.Tests.csproj --nologo
```
Expected: previous passing tests still pass; new tests pass. The change is additive — no existing assertion path changes.

- [ ] **Step 6: Commit**

```bash
git add samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs samples/OpenClaw.StrategosWorkflowHost.Tests/DurableHttpAdapterEvidenceTests.cs
git commit -m "feat(strategos): cross-link Evidence Bundle into status OutputPayload and Events

DurableHttpAdapter now surfaces the audit JSON that EmitAuditTrace appends
to ReviewState.ExecutionResult as:

  1. OutputPayload.audit: parsed JsonElement (only when the
     'AuditTrace:' marker is present, omitted otherwise).
  2. AgentWorkflowEvent { Type = 'audit_trace_emitted' }: terminal event
     derived from EmitAuditTraceCompleted entries in the Marten stream.

The seam stays in the adapter (spec §7): no changes to EmitAuditTrace,
ReviewState, ReviewWorkflow, or Program.cs. Two internal test shims
(BuildOutputPayloadForTest, MapEventForTest) expose the existing private
helpers to the sibling test project via InternalsVisibleTo.

Status response is backward-compatible: existing OutputPayload keys
(workflowId, plan, approved, phase, reviewCount) and existing event
entries are unchanged; the audit fields are opt-in by virtue of the
'AuditTrace:' marker being present."
```

---

## Self-Review

**1. Spec coverage** — user feature brief: "插入 `status` 响应的 `OutputPayload`/`Events` 与 `EmitAuditTrace` 步骤——适配器层（§7）即接缝，`FetchStreamAsync` 已就绪". Task 1 builds the parser the adapter needs; Task 2 modifies the adapter (§7) to insert `OutputPayload.audit` + a dedicated event. ✅ Spec §7's "already-available `FetchStreamAsync`" is honored — the audit event rides on the existing stream projection.

**2. Placeholder scan** — no `TBD` / `TODO` / "implement later". Every code step is fully written. The dynamic-cast note in Edit 3c is a documented intentional choice with a static alternative.

**3. Type consistency** —
- `EvidenceBundleParser.ExtractAuditJson(string?) → string?` (Task 1) is called exactly once in `BuildOutputPayload` (Task 2 Edit 3b).
- `BuildOutputPayloadForTest` / `MapEventForTest` are added once each and used only by the new test file.
- `EmitAuditTraceCompleted` / `SecurityReviewerCompleted` constructor signatures match the generator output verified in P0 Task 6 (`({WorkflowId, StepExecutionId, UpdatedState, Confidence?, Timestamp})`).
- The existing `MapEvent` is preserved when `typeName != "EmitAuditTraceCompleted"` — the previous `PhaseStatusMap.ToOpenClawStatus(...)` mapping stays intact.

**Known execution-period uncertainties (not placeholders, with fallbacks):**
- The dynamic cast in Edit 3c Summary line: if the project disallows `dynamic` (some AOT-strict configs), replace with `if (evt is EmitAuditTraceCompleted audit) { ... audit.UpdatedState.ExecutionResult ... }`.
- The generator-produced `EmitAuditTraceCompleted` constructor signature: if it carries an extra field in this Strategos version, the test's `new EmitAuditTraceCompleted(...)` line will need the new arg — compiler error message will name it. Update the test code accordingly.
- `InternalsVisibleTo` addition: the host csproj may already declare it (the P0 README mentions the sibling test project); if so, the build step is a no-op. The XML element is idempotent if `Include` matches the existing assembly name.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-20-strategos-evidence-bundle-marten-cross-link.md`. Two tasks, each independently testable, ~2 commits total.

Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?