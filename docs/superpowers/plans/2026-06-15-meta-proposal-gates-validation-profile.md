# Meta Proposal Gates Validation Profile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a structured pre-accept validation profile for meta-run proposals so `accept` and `change --to accept` enforce OpenSquilla-style combined gates while preserving existing lifecycle compatibility.

**Architecture:** Keep the runtime orchestration unchanged and evolve only the proposal-governance surface in CLI. Introduce a dedicated acceptance-quality evaluator with grouped checks (structure, trigger consistency, runtime evidence, safety boundary), emit additive machine-readable gate details in JSON failures, and persist a compact gate snapshot in durable proposal metadata for auditability. Preserve backward compatibility for sparse historical meta-runs by distinguishing malformed evidence from minimal-but-valid legacy evidence.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json source-generated serialization, OpenClaw CLI command tests

---

## Scope Check

This spec touches one subsystem: proposal governance before acceptance (`meta-runs proposals accept` and `meta-runs proposals change --to accept`). It does not require splitting into separate plans.

## Worktree Requirement

Use a dedicated worktree before implementation.

Example:

```bash
git worktree add ../openclaw-meta-gates-validation metaskill
```

Then continue implementation in the new worktree.

## File Structure

- Create: `src/OpenClaw.Cli/MetaRunProposalAcceptanceQualityGate.cs`
  Responsibility: define validation profile model/check IDs and central evaluator that returns structured gate results.
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  Responsibility: replace string-only gate check with structured evaluator usage; write additive JSON gate details on failure; persist gate snapshot metadata on success paths.
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: add source-generated DTOs for gate result serialization (if needed by CLI output/persistence helpers).
- Modify: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
  Responsibility: add red/green tests for grouped gate checks, machine-readable failure payload, and compatibility behavior.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  Responsibility: protect lifecycle/idempotency/conflict contracts from regressions after gate expansion.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  Responsibility: update strict migration conclusion and evidence for validation profile alignment.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  Responsibility: mirror Chinese status, remaining gaps, and test evidence.

---

### Task 1: Add Structured Acceptance Gate Evaluator

**Files:**
- Create: `src/OpenClaw.Cli/MetaRunProposalAcceptanceQualityGate.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`

- [ ] **Step 1: Write failing tests for structured gate details and check-group IDs**

Add these tests in `SkillCommandsMetaGovernanceTests.cs`:

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_LowQuality_EmitsGateProfileDetails()
{
    // Arrange session with failed run but no failure evidence
    // Act: accept with --json
    // Assert: errorCode == proposal_accept_quality_gate_failed
    // Assert: root has gate.profileId == "opensquilla-authoring-v1"
    // Assert: root has gate.failedChecks containing "runtime.failed_evidence_present"
}

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_PausedWithoutCheckpoint_EmitsSafetyBoundaryCheckFailure()
{
    // Arrange paused run without checkpoint
    // Assert failedChecks contains "safety.paused_checkpoint_consistent"
}
```

- [ ] **Step 2: Run the test slice and confirm failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests.RunAsync_MetaRuns_Proposals_Accept_Json_LowQuality_EmitsGateProfileDetails|FullyQualifiedName~SkillCommandsMetaGovernanceTests.RunAsync_MetaRuns_Proposals_Accept_Json_PausedWithoutCheckpoint_EmitsSafetyBoundaryCheckFailure"
```

Expected: FAIL because current gate only returns a plain reason string and does not emit structured gate details.

- [ ] **Step 3: Implement evaluator and grouped checks in new file**

Create `MetaRunProposalAcceptanceQualityGate.cs` with a focused API:

```csharp
namespace OpenClaw.Cli;

internal static class MetaRunProposalAcceptanceQualityGate
{
    internal const string ProfileId = "opensquilla-authoring-v1";

    internal static MetaRunProposalAcceptanceGateResult Evaluate(
        MetaRunDerivedProposalSummary proposal,
        Session session)
    {
        var checks = new List<MetaRunProposalAcceptanceGateCheckResult>();

        var run = session.MetaRunHistory.FirstOrDefault(r => string.Equals(r.RunId, proposal.RunId, StringComparison.Ordinal));
        checks.Add(Check("structure.run_exists", run is not null));
        checks.Add(Check("structure.run_identity_present", run is not null && !string.IsNullOrWhiteSpace(run.RunId) && !string.IsNullOrWhiteSpace(run.SkillName)));

        if (run is not null && run.StepResults.Count > 0)
        {
            var hasInvalidShape = run.StepResults.Any(step =>
                string.IsNullOrWhiteSpace(step.Id)
                || string.IsNullOrWhiteSpace(step.Kind)
                || string.IsNullOrWhiteSpace(step.Status));
            checks.Add(Check("structure.step_shape_valid", !hasInvalidShape));

            var hasDuplicateIds = run.StepResults
                .Select(step => step.Id)
                .GroupBy(id => id, StringComparer.Ordinal)
                .Any(group => group.Count() > 1);
            checks.Add(Check("structure.step_id_unique", !hasDuplicateIds));
        }

        if (run is not null)
        {
            var triggerStatusValid = string.Equals(run.Status, proposal.Status, StringComparison.OrdinalIgnoreCase);
            checks.Add(Check("trigger.proposal_status_matches_run", triggerStatusValid));

            if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var hasFailureEvidence = !string.IsNullOrWhiteSpace(run.ErrorCode)
                    || !string.IsNullOrWhiteSpace(run.Error)
                    || run.StepResults.Any(step => !string.IsNullOrWhiteSpace(step.FailureCode));
                checks.Add(Check("runtime.failed_evidence_present", hasFailureEvidence));
            }
            else
            {
                checks.Add(Check("runtime.failed_evidence_present", true));
            }

            if (string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase))
            {
                var checkpoint = session.MetaExecutionCheckpoint;
                var consistent = checkpoint is not null
                    && string.Equals(checkpoint.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(checkpoint.PendingStepId);
                checks.Add(Check("safety.paused_checkpoint_consistent", consistent));
            }
            else
            {
                checks.Add(Check("safety.paused_checkpoint_consistent", true));
            }
        }

        return MetaRunProposalAcceptanceGateResult.From(ProfileId, checks);
    }

    private static MetaRunProposalAcceptanceGateCheckResult Check(string id, bool passed)
        => new(id, passed);
}

internal sealed record MetaRunProposalAcceptanceGateCheckResult(string Id, bool Passed);

internal sealed record MetaRunProposalAcceptanceGateResult(
    string ProfileId,
    bool Passed,
    IReadOnlyList<MetaRunProposalAcceptanceGateCheckResult> Checks,
    IReadOnlyList<string> FailedChecks)
{
    internal static MetaRunProposalAcceptanceGateResult From(string profileId, IReadOnlyList<MetaRunProposalAcceptanceGateCheckResult> checks)
    {
        var failed = checks.Where(c => !c.Passed).Select(c => c.Id).ToArray();
        return new(profileId, failed.Length == 0, checks, failed);
    }
}
```

- [ ] **Step 4: Wire `SkillCommands` to use evaluator without changing mutation flow yet**

In `SkillCommands.cs`, replace:

```csharp
if (string.Equals(targetStatus, MetaRunProposalReviewStatuses.Accepted, StringComparison.Ordinal)
    && !PassesProposalAcceptanceQualityGate(proposal, session, out var acceptanceGateReason))
{
    WriteProposalAcceptanceQualityError(asJson, $"skills meta-runs proposals {action}", acceptanceGateReason);
    return 1;
}
```

With:

```csharp
if (string.Equals(targetStatus, MetaRunProposalReviewStatuses.Accepted, StringComparison.Ordinal))
{
    var gate = MetaRunProposalAcceptanceQualityGate.Evaluate(proposal, session);
    if (!gate.Passed)
    {
        WriteProposalAcceptanceQualityError(asJson, $"skills meta-runs proposals {action}", gate);
        return 1;
    }
}
```

Add overload:

```csharp
private static void WriteProposalAcceptanceQualityError(
    bool asJson,
    string command,
    MetaRunProposalAcceptanceGateResult gate)
{
    if (asJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "error");
            writer.WriteString("command", command);
            writer.WriteString("errorCode", "proposal_accept_quality_gate_failed");
            writer.WriteString("message", "Proposal acceptance quality gate failed.");
            writer.WriteStartObject("gate");
            writer.WriteString("profileId", gate.ProfileId);
            writer.WriteBoolean("passed", gate.Passed);
            writer.WritePropertyName("failedChecks");
            writer.WriteStartArray();
            foreach (var failed in gate.FailedChecks)
                writer.WriteStringValue(failed);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Console.Error.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        return;
    }

    var reason = gate.FailedChecks.Count == 0 ? string.Empty : string.Join(",", gate.FailedChecks);
    var message = string.IsNullOrWhiteSpace(reason)
        ? "Proposal acceptance quality gate failed."
        : $"Proposal acceptance quality gate failed. Reason: {reason}.";
    WriteSkillsCommandError(false, command, "proposal_accept_quality_gate_failed", message);
}
```

- [ ] **Step 5: Run tests to verify pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests.RunAsync_MetaRuns_Proposals_Accept_Json_LowQuality_EmitsGateProfileDetails|FullyQualifiedName~SkillCommandsMetaGovernanceTests.RunAsync_MetaRuns_Proposals_Accept_Json_PausedWithoutCheckpoint_EmitsSafetyBoundaryCheckFailure"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Cli/MetaRunProposalAcceptanceQualityGate.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs
git commit -m "feat(governance): add structured proposal acceptance validation profile"
```

---

### Task 2: Persist Gate Snapshot For Durable Audit

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Write failing tests for durable metadata snapshot**

Add test assertions in proposal lifecycle test cases:

```csharp
Assert.Equal("opensquilla-authoring-v1", durable.Metadata["meta_run_proposal_accept_gate_profile"]);
Assert.Equal("true", durable.Metadata["meta_run_proposal_accept_gate_passed"]);
Assert.Equal("", durable.Metadata["meta_run_proposal_accept_gate_failed_checks"]);
Assert.True(durable.Metadata.ContainsKey("meta_run_proposal_accept_gate_checked_at_utc"));
```

Use existing passing path test as anchor: `RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle`.

- [ ] **Step 2: Run test to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle"
```

Expected: FAIL because the new metadata keys do not exist yet.

- [ ] **Step 3: Add metadata constants and persistence logic**

In `SkillCommands.cs`, extend metadata constants:

```csharp
private static class MetaRunProposalMetadata
{
    public const string AcceptGateProfile = "meta_run_proposal_accept_gate_profile";
    public const string AcceptGatePassed = "meta_run_proposal_accept_gate_passed";
    public const string AcceptGateFailedChecks = "meta_run_proposal_accept_gate_failed_checks";
    public const string AcceptGateCheckedAtUtc = "meta_run_proposal_accept_gate_checked_at_utc";
}
```

Before durable save in accept/change-to-accept flows:

```csharp
var gate = MetaRunProposalAcceptanceQualityGate.Evaluate(proposal, session);
if (!gate.Passed)
{
    WriteProposalAcceptanceQualityError(asJson, commandName, gate);
    return 1;
}

metadata[MetaRunProposalMetadata.AcceptGateProfile] = gate.ProfileId;
metadata[MetaRunProposalMetadata.AcceptGatePassed] = gate.Passed ? "true" : "false";
metadata[MetaRunProposalMetadata.AcceptGateFailedChecks] = string.Join(",", gate.FailedChecks);
metadata[MetaRunProposalMetadata.AcceptGateCheckedAtUtc] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
```

- [ ] **Step 4: Update source-gen attributes if new DTO serialization is introduced**

If any new DTOs are added to model layer, update `CoreJsonContext` declarations in `Session.cs` accordingly:

```csharp
[JsonSerializable(typeof(MetaRunProposalAcceptanceGateResultDto))]
[JsonSerializable(typeof(MetaRunProposalAcceptanceGateCheckResultDto[]))]
```

If you keep gate DTOs internal to CLI writer only, skip this step intentionally and keep model layer unchanged.

- [ ] **Step 5: Run focused tests and verify pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_Json_PrintsAppliedReview"
```

Expected: PASS and metadata snapshot assertions succeed.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(governance): persist acceptance gate profile snapshot in proposal metadata"
```

---

### Task 3: Expand Regression Matrix And Guard Backward Compatibility

**Files:**
- Modify: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`

- [ ] **Step 1: Write failing compatibility test for sparse paused run acceptance**

Add a test proving legacy sparse run shape still passes if checkpoint is valid:

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_SparsePausedRunWithCheckpoint_AllowsAcceptance()
{
    // Arrange paused run with zero StepResults and valid checkpoint
    // Act accept --json
    // Assert exitCode == 0, reviewStatus == accepted
}
```

- [ ] **Step 2: Run the compatibility test to verify baseline behavior**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_Json_SparsePausedRunWithCheckpoint_AllowsAcceptance"
```

Expected: PASS if compatibility is preserved; FAIL if new checks accidentally over-constrain legacy data.

- [ ] **Step 3: Add failing tests for each new grouped check path**

Add focused negative tests:

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_StatusMismatch_FailsTriggerCheck() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_DuplicateStepIds_FailsStructureCheck() { }

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Change_ToAccept_FailedWithoutEvidence_FailsRuntimeCheck() { }
```

Assertions should verify `gate.failedChecks` contains exact IDs.

- [ ] **Step 4: Implement minimal fixes for any failing paths**

If tests fail after evaluator wiring, patch evaluator logic in `MetaRunProposalAcceptanceQualityGate.cs` only; avoid widening lifecycle command logic.

Example minimal patch pattern:

```csharp
var triggerStatusValid = string.Equals(run.Status, proposal.Status, StringComparison.OrdinalIgnoreCase)
    || (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
        && string.Equals(proposal.Status, "completed", StringComparison.OrdinalIgnoreCase));
checks.Add(Check("trigger.proposal_status_matches_run", triggerStatusValid));
```

- [ ] **Step 5: Run governance and lifecycle regression suites**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"
```

Expected: PASS for all governance checks and legacy proposal lifecycle tests.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "test(governance): expand validation profile gate matrix and compatibility coverage"
```

---

### Task 4: Update Migration Documentation And Evidence

**Files:**
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Write failing doc-check test in practice (manual verification list)**

Create a short checklist in your working notes and mark all as unchecked initially:

```text
- Validation profile has grouped checks documented
- JSON failure contract includes gate.profileId and failedChecks
- Compatibility caveat for sparse legacy runs documented
- New test evidence commands and pass counts documented
```

- [ ] **Step 2: Update English migration doc with validation profile status**

Update the row for proposal acceptance gate to mention:
- profile ID `opensquilla-authoring-v1`
- grouped checks: structure/trigger/runtime/safety
- additive JSON gate details
- persistent gate snapshot metadata
- remaining gaps (if any) explicitly called out

- [ ] **Step 3: Update Chinese migration doc with identical semantics**

In `docs/zh-CN/opensquilla-meta-skill-migration.md`, revise:
- 当前结论 section line that currently says continue alignment
- 验收表 row for proposal acceptance gate
- 剩余迁移缺口 wording so it reflects what remains after this change

- [ ] **Step 4: Run full targeted verification commands and capture results in docs**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_"
dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal
```

Expected: PASS for filtered tests and successful build.

- [ ] **Step 5: Commit**

```bash
git add docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs(meta-governance): document validation profile gate parity and evidence"
```

---

## Self-Review

### 1) Spec Coverage

- Requirement: continue alignment to full OpenSquilla gates/validation profile.
  - Covered by Task 1 grouped evaluator and machine-readable profile details.
- Requirement: keep accept and change-to-accept both gated.
  - Covered by Task 1 and Task 2 wiring in both mutation paths.
- Requirement: preserve lifecycle behavior and avoid regressions.
  - Covered by Task 3 compatibility and full proposal lifecycle regression slice.
- Requirement: update migration docs.
  - Covered by Task 4 in both English and Chinese docs.

No uncovered requirement found.

### 2) Placeholder Scan

- No TODO/TBD placeholders left.
- All code-touching steps contain concrete snippets.
- All test steps include executable commands and expected outcomes.

### 3) Type/Name Consistency

- Evaluator names used consistently:
  - `MetaRunProposalAcceptanceQualityGate`
  - `MetaRunProposalAcceptanceGateResult`
  - `MetaRunProposalAcceptanceGateCheckResult`
- Metadata keys use one naming family:
  - `meta_run_proposal_accept_gate_*`
- Check IDs use one naming family:
  - `structure.*`, `trigger.*`, `runtime.*`, `safety.*`

No naming mismatch found.
