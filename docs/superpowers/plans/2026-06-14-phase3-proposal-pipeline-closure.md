# Phase 3 Proposal Pipeline Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the Phase 3 product governance gap for meta-run proposal mutations by adding explicit lifecycle policy, mutation permission boundaries, and additive audit fields with full JSON/text contract coverage.

**Architecture:** Keep runtime orchestration unchanged and implement Phase 3 strictly in the proposal mutation surface (`skills meta-runs proposals ...`). Introduce a small policy module in core models for allowed transitions and permission checks, then apply it in CLI mutation commands with unified machine-readable errors. Extend detail/mutation responses additively for audit metadata, and verify with focused plus end-to-end tests.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json source-gen context, OpenClaw CLI command tests

## Execution Status (2026-06-14)

- Completed: Task 1 policy primitives + permission boundary (`OPENCLAW_OPERATOR_ID`) with JSON `permission_denied` contract.
- Completed: Task 2 mutation policy wiring for `accept|dismiss|rollback|change`, now centralized through `MetaRunProposalPolicy` action-aware transition checks.
- Completed: Task 3 additive audit payload (`audit.schemaVersion|actorId|changedAtUtc|transitionAction`) on mutation and proposal detail responses.
- Completed: Task 4 initial E2E acceptance slice (`create --proposal-draft -> dismiss -> rollback -> change -> show`) with contract assertions for lifecycle, provenance history, and audit.
- Verification evidence:
    - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState" -v minimal` -> PASS (1/1)
    - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_" -v minimal` -> PASS (85/85)

---

## File Structure

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  - Apply lifecycle/permission policy checks before durable proposal writes.
  - Emit stable JSON error schema for new policy failures.
  - Populate additive audit fields into mutation responses.
- Create: `src/OpenClaw.Core/Models/MetaRunProposalPolicy.cs`
  - Define Phase 3 policy primitives: transition rules, permission checks, and policy decision objects.
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  - Add additive response fields for proposal mutation/detail audit sections.
  - Register any new DTOs in `CoreJsonContext` source generation attributes.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  - Add red-green tests for transition policy rejection, permission denial, additive audit output, and end-to-end chain.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  - Record Phase 3 implementation evidence and command contracts.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  - Mirror Chinese migration evidence and update completion state.

## Task 1: Add Policy Primitives (Transitions + Permission)

**Files:**
- Create: `src/OpenClaw.Core/Models/MetaRunProposalPolicy.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write the failing tests for policy-backed permission/transition outcomes**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_WithoutOperatorId_ReturnsPermissionDenied()
{
    var previousError = Console.Error;
    var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
    using var error = new StringWriter();

    try
    {
        Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", null);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "accept", "sess-any", "--proposal", "meta-run:run-001:paused", "--json"]);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(error.ToString());
        Assert.Equal("permission_denied", document.RootElement.GetProperty("errorCode").GetString());
    }
    finally
    {
        Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
        Console.SetError(previousError);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_Json_WithoutOperatorId_ReturnsPermissionDenied" -v minimal`
Expected: FAIL because permission gate is not implemented yet.

- [x] **Step 3: Add minimal policy module in core models**

```csharp
namespace OpenClaw.Core.Models;

public static class MetaRunProposalPolicy
{
    public static bool CanMutate(string? operatorId)
        => !string.IsNullOrWhiteSpace(operatorId);

    public static bool IsAllowedTransition(string fromStatus, string toStatus)
        => (fromStatus, toStatus) switch
        {
            (LearningProposalStatus.Pending, LearningProposalStatus.Approved) => true,
            (LearningProposalStatus.Pending, LearningProposalStatus.Rejected) => true,
            (LearningProposalStatus.Approved, LearningProposalStatus.RolledBack) => true,
            (LearningProposalStatus.Rejected, LearningProposalStatus.RolledBack) => true,
            (LearningProposalStatus.RolledBack, LearningProposalStatus.Approved) => true,
            (LearningProposalStatus.RolledBack, LearningProposalStatus.Rejected) => true,
            _ => false
        };
}
```

- [x] **Step 4: Run targeted tests to verify compile + baseline behavior**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_Json_WithoutOperatorId_ReturnsPermissionDenied" -v minimal`
Expected: still FAIL (policy exists but CLI path not wired yet).

- [ ] **Step 5: Commit (pending, uncommitted in current workspace)**

```bash
git add src/OpenClaw.Core/Models/MetaRunProposalPolicy.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "test: add phase3 permission gate red test and policy scaffold"
```

### Task 2: Wire Policy Into CLI Mutation Commands

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write failing tests for transition-policy and command-specific error codes**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Change_Json_InvalidTransition_ReturnsPolicyError()
{
    var previousError = Console.Error;
    using var error = new StringWriter();

    try
    {
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "change", "sess-any", "--proposal", "meta-run:run-001:paused", "--to", "dismiss", "--json"]);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(error.ToString());
        Assert.Equal("invalid_lifecycle_transition", document.RootElement.GetProperty("errorCode").GetString());
    }
    finally
    {
        Console.SetError(previousError);
    }
}
```

- [x] **Step 2: Run tests to verify they fail before implementation**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~PermissionDenied|FullyQualifiedName~InvalidTransition_ReturnsPolicyError" -v minimal`
Expected: FAIL with missing permission/transition enforcement.

- [x] **Step 3: Implement minimal CLI wiring in mutation paths**

```csharp
var operatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
if (!MetaRunProposalPolicy.CanMutate(operatorId))
{
    WriteSkillsCommandError(asJson, "skills meta-runs proposals accept", "permission_denied", "Proposal mutation requires OPENCLAW_OPERATOR_ID.");
    return 1;
}

if (!MetaRunProposalPolicy.IsAllowedTransition(existing.Status, targetLifecycleStatus))
{
    WriteSkillsCommandError(asJson, "skills meta-runs proposals change", "invalid_lifecycle_transition", $"Invalid lifecycle transition: {existing.Status} -> {targetLifecycleStatus}.");
    return 1;
}
```

Apply the same policy pattern for:
- `accept`
- `dismiss`
- `rollback`
- `change`

- [x] **Step 4: Run focused mutation regression slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Dismiss_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Rollback_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Change_" -v minimal`
Expected: PASS for existing contracts plus new policy tests.

- [ ] **Step 5: Commit (pending, uncommitted in current workspace)**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: enforce phase3 proposal mutation policy in skills CLI"
```

### Task 3: Add Additive Audit Fields to Mutation and Detail Responses

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write failing JSON contract tests for additive audit fields**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_Json_IncludesAuditSection()
{
    // Arrange session + proposal lifecycle mutations first
    // ...
    var exitCode = await SkillCommands.RunAsync([
        "meta-runs", "proposals", "show", "sess-audit", "--proposal", "meta-run:run-001:paused", "--json"]);

    Assert.Equal(0, exitCode);
    using var document = JsonDocument.Parse(output.ToString());
    var proposal = document.RootElement.GetProperty("proposal");
    Assert.True(proposal.TryGetProperty("audit", out var audit));
    Assert.Equal("v1", audit.GetProperty("schemaVersion").GetString());
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~IncludesAuditSection|FullyQualifiedName~IncludesMutationAudit" -v minimal`
Expected: FAIL because audit fields are not present.

- [x] **Step 3: Implement additive DTO fields and serializer registrations**

```csharp
public sealed class MetaRunProposalAuditDetail
{
    public string SchemaVersion { get; init; } = "v1";
    public string? ActorId { get; init; }
    public DateTimeOffset? ChangedAtUtc { get; init; }
    public string? TransitionAction { get; init; }
}

public sealed class MetaRunDerivedProposalDetail
{
    // existing fields...
    public MetaRunProposalAuditDetail? Audit { get; init; }
}
```

And register in `CoreJsonContext`:

```csharp
[JsonSerializable(typeof(MetaRunProposalAuditDetail))]
```

Populate from CLI mutation metadata:

```csharp
Audit = new MetaRunProposalAuditDetail
{
    ActorId = operatorId,
    ChangedAtUtc = reviewedAtUtc,
    TransitionAction = "change"
}
```

- [x] **Step 4: Run proposal show/list mutation contract tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Show_Json_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Change_Json_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Rollback_Json_" -v minimal`
Expected: PASS with additive fields while legacy fields stay intact.

- [ ] **Step 5: Commit (pending, uncommitted in current workspace)**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add additive audit fields for phase3 proposal lifecycle outputs"
```

### Task 4: Add Product-Level E2E Acceptance Slice

**Files:**
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [x] **Step 1: Write an end-to-end failing test for create->proposal->lifecycle->audit chain**

```csharp
[Fact]
public async Task RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState()
{
    // 1) skills create --kind meta --proposal-draft --json
    // 2) meta-runs proposals accept/dismiss/rollback/change with operator id
    // 3) proposals show --json verifies lifecycle + provenanceHistory + audit
    // Assert stable additive contract and no partial JSON on failures.
}
```

- [x] **Step 2: Run test to verify it fails first**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState" -v minimal`
Expected: FAIL until all chain assertions are satisfied.

- [x] **Step 3: Implement minimal test fixtures and helper setup in test file**

```csharp
var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3");
try
{
    // execute command chain and assert JSON contracts
}
finally
{
    Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
}
```

- [x] **Step 4: Run full SkillCommands regression slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_" -v minimal`
Expected: PASS (existing 83+ tests plus new E2E test).

- [ ] **Step 5: Commit (pending, uncommitted in current workspace)**

```bash
git add src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "test: add phase3 e2e acceptance chain for proposal pipeline"
```

### Task 5: Update Migration Docs and Evidence

**Files:**
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [x] **Step 1: Write docs assertions as failing checklist items (manual red step)**

```markdown
- [ ] Phase 3 policy enforcement shipped with permission boundary.
- [ ] Additive audit fields documented with JSON examples.
- [ ] E2E acceptance command slice recorded with passing output summary.
```

- [x] **Step 2: Run grep check to confirm old “Phase 3 pending” statements still exist before update**

Run: `rg "Phase 3|proposal pipeline" docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md`
Expected: shows pending language to be replaced.

- [x] **Step 3: Update both docs with completion evidence and command outputs**

```markdown
- Phase 3 policy closure delivered: transition governance + mutation permission boundary.
- Additive audit section added to proposal detail and mutation response outputs.
- Acceptance slice: `dotnet test ... --filter "FullyQualifiedName~RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState"` passed.
```

- [x] **Step 4: Run docs sanity grep after edit**

Run: `rg "pending|TODO|TBD" docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md`
Expected: no stale placeholders for completed Phase 3 items.

- [ ] **Step 5: Commit (pending, uncommitted in current workspace)**

```bash
git add docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs: record phase3 proposal pipeline closure evidence"
```

## Final Verification Gate

- [x] Run complete targeted suite:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_" -v minimal
```

Expected: PASS with all SkillCommands tests green.

- [x] Optional broader confidence slice:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_|FullyQualifiedName~RunAsync_Inspect_|FullyQualifiedName~RunAsync_Install_" -v minimal
```

Expected: PASS.

## Self-Review

### 1) Spec coverage check

- Phase 3 proposal pipeline closure: covered by Task 1 + Task 2.
- Permission boundary: covered by Task 1 + Task 2 tests and CLI wiring.
- Additive audit fields: covered by Task 3.
- Product-level E2E acceptance: covered by Task 4.
- Documentation/evidence update: covered by Task 5.

No uncovered requirement found for the requested Phase 3 scope.

### 2) Placeholder scan

- No `TBD`, `TODO`, `implement later`, or “similar to Task N” placeholders retained.
- Each code-change step contains concrete snippet and concrete command.

### 3) Type/signature consistency

- Policy names use a single surface: `MetaRunProposalPolicy.CanMutate`, `MetaRunProposalPolicy.IsAllowedTransition`, and `MetaRunProposalPolicy.IsAllowedActionTransition`.
- Error schema remains stable with `status/command/errorCode/message`.
- Lifecycle constants reuse existing `LearningProposalStatus` values.

No naming drift detected across tasks.
