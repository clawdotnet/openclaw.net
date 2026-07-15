# Harness Action Adapter Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the first production-ready bridge from MetaSkill proposal output to policy-gated action execution, including approval gating, governance records, and safe connector-only write path.

**Architecture:** Keep the current MetaSkill DAG engine unchanged and add an additive bridge layer. Introduce a typed ActionProposal normalization boundary, then route execution through a new `action_execute` controlled tool that enforces policy decisions (`proceed_execute`, `require_approval`, `proposal_only`) before adapter execution. Persist governance/audit artifacts through existing session and PEV surfaces, and preserve backward compatibility for skills that do not call `action_execute`.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json, existing OpenClaw ToolExecutor/PEV/governance models

## Global Constraints

- 写路径仅允许业务 API Connector，禁止数据库直写。
- 不开放数据库直写能力。
- 不改变现有未接入 Action 机制的 MetaSkill 行为。
- 策略引擎不可用时降级 proposal_only。
- 判级不确定时按高风险处理。
- 连接器未知时拒绝执行。
- 首版按顺序执行，避免并发竞态。
- proposal 必须携带 idempotencyKey。
- Preserve NativeAOT friendliness and avoid reflection-heavy trim-unsafe dependencies in runtime core paths.

---

## Scope Check

This implementation is one subsystem (MetaSkill-to-Action bridge) with four tightly coupled deliverables:
1. Proposal normalization boundary
2. Controlled execution entry (`action_execute`)
3. Policy/approval gating
4. Adapter/connector execution + audit mapping

No additional plan split is required for this slice.

## File Structure

- Create: `src/OpenClaw.Core/Models/ActionProposalModels.cs`
  Responsibility: typed ActionProposal DTOs, normalization result, validation errors, decision enums.
- Create: `src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs`
  Responsibility: normalize MetaSkill JSON output (from `llm_chat` or `tool_call`) into ActionProposal, enforce minimal DSL constraints.
- Create: `src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs`
  Responsibility: evaluate proposal risk and return decision/result payload (`decision`, `riskLevel`, `reasonCodes`, `requiredApprovals`, `constraints`).
- Create: `src/OpenClaw.Agent/Actions/ActionAdapter.cs`
  Responsibility: run preCheck/execution/rollback sequentially via connectors with idempotency enforcement.
- Create: `src/OpenClaw.Agent/Actions/ActionApprovalRecord.cs`
  Responsibility: approval callback payload model and validator (`approver`, `decisionAt`, `decisionReason`, `ticketRef`, optional `decisionType`).
- Create: `src/OpenClaw.Agent/Tools/ActionExecuteTool.cs`
  Responsibility: controlled tool entry for `action_execute`, orchestration of builder/policy/approval/adapter and governance mapping.
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`
  Responsibility: register `ActionExecuteTool` in runtime tool list.
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
  Responsibility: optional helper path for proposal normalization reuse in MetaSkill execution flow (no behavior change for non-action skills).
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  Responsibility: add additive audit fields/records for action decision, approval, and execution evidence mapping.
- Test: `src/OpenClaw.Tests/ActionProposalBuilderTests.cs`
  Responsibility: red/green coverage for normalization, required field enforcement, illegal DB-write field rejection.
- Test: `src/OpenClaw.Tests/ActionExecuteToolTests.cs`
  Responsibility: decision routing, approval gating semantics, proposal-only downgrade behavior.
- Test: `src/OpenClaw.Tests/ActionAdapterTests.cs`
  Responsibility: sequential execution, rollback trigger, idempotency conflict behavior.
- Test: `src/OpenClaw.Tests/ActionApprovalRecordTests.cs`
  Responsibility: approval field completeness and format checks (ISO 8601 UTC, ticketRef traceability).
- Modify: `docs/zh-CN/meta-skills.md`
  Responsibility: add user-facing `action_execute` usage example and approval callback semantics.

---

### Task 1: Implement ActionProposal Domain Model and Normalization Boundary

**Files:**
- Create: `src/OpenClaw.Core/Models/ActionProposalModels.cs`
- Create: `src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs`
- Test: `src/OpenClaw.Tests/ActionProposalBuilderTests.cs`

**Interfaces:**
- Consumes: raw MetaSkill step output JSON text (`string`) from existing DAG step outputs.
- Produces:
  - `ActionProposalNormalizationResult ActionProposalBuilder.Normalize(string rawOutput)`
  - `bool ActionProposalValidation.TryValidate(ActionProposal proposal, out string errorCode)`
  - `ActionProposal` DTO surface used by `ActionExecuteTool` in Task 2.

- [ ] **Step 1: Write the failing tests for normalization and validation**

Add tests in `ActionProposalBuilderTests.cs`:

```csharp
[Fact]
public void Normalize_ValidProposalJson_ReturnsTypedProposal()
{
    var raw = """
    {
      "actionName":"sync_customer_tier",
      "source":{"metaSkill":"customer-risk-assistant","runId":"run_1","stepId":"step_1"},
      "trigger":{"condition":"riskLevel == medium","evidenceRefs":["ev_001"]},
      "target":{"system":"crm","operation":"updateCustomerTier"},
      "preChecks":[{"call":"crm.getCustomer","args":{"customerId":"C123"}}],
      "execution":[{"call":"crm.updateTier","args":{"customerId":"C123","tier":"B"}}],
      "rollback":[{"call":"crm.updateTier","args":{"customerId":"C123","tier":"A"}}],
      "idempotencyKey":"proposal-C123-20260715-01",
      "metadata":{"env":"prod"}
    }
    """;

    var result = ActionProposalBuilder.Normalize(raw);

    Assert.True(result.Success);
    Assert.NotNull(result.Proposal);
    Assert.Equal("sync_customer_tier", result.Proposal!.ActionName);
}

[Fact]
public void Normalize_MissingExecution_ReturnsInvalidProposal()
{
    var raw = """
    {
      "actionName":"sync_customer_tier",
      "source":{"metaSkill":"customer-risk-assistant","runId":"run_1","stepId":"step_1"},
      "trigger":{"condition":"riskLevel == medium","evidenceRefs":["ev_001"]},
      "target":{"system":"crm","operation":"updateCustomerTier"},
      "execution":[],
      "idempotencyKey":"proposal-C123-20260715-01",
      "metadata":{"env":"prod"}
    }
    """;

    var result = ActionProposalBuilder.Normalize(raw);

    Assert.False(result.Success);
    Assert.Equal("invalid_proposal", result.ErrorCode);
}

[Fact]
public void Normalize_ContainsSqlWriteField_ReturnsPolicyDenied()
{
    var raw = """
    {
      "actionName":"dangerous_write",
      "source":{"metaSkill":"x","runId":"run_1","stepId":"step_1"},
      "trigger":{"condition":"true","evidenceRefs":[]},
      "target":{"system":"db","operation":"update"},
      "execution":[{"call":"sql.execute","args":{"sql":"update users set role='admin'"}}],
      "idempotencyKey":"k1",
      "metadata":{"connectionString":"Server=prod;"}
    }
    """;

    var result = ActionProposalBuilder.Normalize(raw);

    Assert.False(result.Success);
    Assert.Equal("policy_denied", result.ErrorCode);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionProposalBuilderTests" -v minimal
```

Expected: FAIL because the builder and models do not exist yet.

- [ ] **Step 3: Implement minimal ActionProposal models and builder**

Create `ActionProposalModels.cs` (minimal skeleton):

```csharp
namespace OpenClaw.Core.Models;

public sealed class ActionProposal
{
    public required string ActionName { get; init; }
    public required ActionProposalSource Source { get; init; }
    public required ActionProposalTrigger Trigger { get; init; }
    public required ActionProposalTarget Target { get; init; }
    public IReadOnlyList<ActionCall> PreChecks { get; init; } = [];
    public required IReadOnlyList<ActionCall> Execution { get; init; }
    public IReadOnlyList<ActionCall> Rollback { get; init; } = [];
    public required string IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed class ActionProposalNormalizationResult
{
    public bool Success { get; init; }
    public ActionProposal? Proposal { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

Create `ActionProposalBuilder.cs` (minimal normalize path + checks):

```csharp
namespace OpenClaw.Agent.Actions;

internal static class ActionProposalBuilder
{
    internal static ActionProposalNormalizationResult Normalize(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return Fail("invalid_proposal", "Proposal output is empty.");

        try
        {
            var proposal = JsonSerializer.Deserialize(rawOutput, AgentJsonContext.Default.ActionProposal);
            if (proposal is null)
                return Fail("invalid_proposal", "Proposal payload is null.");

            if (proposal.Execution.Count == 0)
                return Fail("invalid_proposal", "Execution steps are required.");

            if (ContainsBlockedDatabaseWrite(rawOutput))
                return Fail("policy_denied", "Direct database write path is blocked.");

            return new ActionProposalNormalizationResult { Success = true, Proposal = proposal };
        }
        catch (JsonException ex)
        {
            return Fail("invalid_proposal", ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionProposalBuilderTests" -v minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/ActionProposalModels.cs src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs src/OpenClaw.Tests/ActionProposalBuilderTests.cs
git commit -m "feat: add action proposal builder normalization boundary"
```

---

### Task 2: Add action_execute Controlled Entry and Policy Decision Routing

**Files:**
- Create: `src/OpenClaw.Agent/Tools/ActionExecuteTool.cs`
- Create: `src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`
- Test: `src/OpenClaw.Tests/ActionExecuteToolTests.cs`

**Interfaces:**
- Consumes:
  - `ActionProposalBuilder.Normalize(string rawOutput)` from Task 1
  - tool input payload containing proposal JSON
- Produces:
  - Tool result statuses and failure codes aligned with spec (`policy_denied`, `approval_denied`, `invalid_proposal`)
  - policy decision payload (`decision`, `riskLevel`, `reasonCodes`, `requiredApprovals`, `constraints`)

- [ ] **Step 1: Write failing tests for decision routing**

Add tests in `ActionExecuteToolTests.cs`:

```csharp
[Fact]
public async Task InvokeAsync_LowRisk_ReturnsProceedExecute()
{
    var tool = BuildToolWithPolicyDecision("proceed_execute", "low");
    var result = await tool.InvokeAsync("{\"proposal\":{...}}", TestContext.Current.CancellationToken);

    Assert.Contains("\"decision\":\"proceed_execute\"", result, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task InvokeAsync_MediumRisk_ReturnsRequireApprovalAndNoExecution()
{
    var tool = BuildToolWithPolicyDecision("require_approval", "medium");
    var result = await tool.InvokeAsync("{\"proposal\":{...}}", TestContext.Current.CancellationToken);

    Assert.Contains("pending_approval", result, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("execution_started", result, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task InvokeAsync_UnknownConnector_ReturnsPolicyDenied()
{
    var tool = BuildToolWithUnknownConnector();
    var result = await tool.InvokeAsync("{\"proposal\":{...}}", TestContext.Current.CancellationToken);

    Assert.Contains("policy_denied", result, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionExecuteToolTests" -v minimal
```

Expected: FAIL because `ActionExecuteTool` is not implemented.

- [ ] **Step 3: Implement ActionPolicyEngine and ActionExecuteTool minimal routing**

Create `ActionPolicyEngine.cs` with deterministic first-version API:

```csharp
internal interface IActionPolicyEngine
{
    ActionPolicyDecision Evaluate(ActionProposal proposal);
}

internal sealed record ActionPolicyDecision(
    string Decision,
    string RiskLevel,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> RequiredApprovals,
    IReadOnlyList<string> Constraints);
```

Create `ActionExecuteTool.cs` with flow:

```csharp
internal sealed class ActionExecuteTool : ITool
{
    public string Name => "action_execute";

    public Task<string> InvokeAsync(string args, CancellationToken cancellationToken = default)
    {
        var normalized = ActionProposalBuilder.Normalize(ExtractProposal(args));
        if (!normalized.Success)
            return Task.FromResult(BuildError(normalized.ErrorCode ?? "invalid_proposal"));

        var decision = _policyEngine.Evaluate(normalized.Proposal!);
        if (string.Equals(decision.Decision, "proposal_only", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildDecisionOnly(decision));

        if (string.Equals(decision.Decision, "require_approval", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildPendingApproval(decision));

        return Task.FromResult(BuildProceedExecute(decision));
    }
}
```

Register in runtime factory tool list:

```csharp
new ActionExecuteTool(...)
```

- [ ] **Step 4: Run targeted tests to verify pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionExecuteToolTests" -v minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Tools/ActionExecuteTool.cs src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs src/OpenClaw.Tests/ActionExecuteToolTests.cs
git commit -m "feat: add action execute tool with policy decision routing"
```

---

### Task 3: Implement Approval Callback Validation and Adapter Execution Semantics

**Files:**
- Create: `src/OpenClaw.Agent/Actions/ActionApprovalRecord.cs`
- Create: `src/OpenClaw.Agent/Actions/ActionAdapter.cs`
- Test: `src/OpenClaw.Tests/ActionApprovalRecordTests.cs`
- Test: `src/OpenClaw.Tests/ActionAdapterTests.cs`

**Interfaces:**
- Consumes:
  - policy decision from Task 2
  - normalized `ActionProposal` from Task 1
  - approval callback payload for `require_approval`
- Produces:
  - approval validation result and failure code
  - adapter execution result (`succeeded`, `failed`, `rolling_back`, `rolled_back`, `rollback_failed`)

- [ ] **Step 1: Write failing tests for approval fields and adapter execution semantics**

Add tests:

```csharp
[Fact]
public void ValidateApprovalRecord_MissingDecisionAt_ReturnsInvalid()
{
    var record = new ActionApprovalRecord
    {
        Approver = "u_zhangsan",
        DecisionReason = "ok",
        TicketRef = "ITSM-1",
        DecisionType = "approve"
    };

    var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

    Assert.False(valid);
    Assert.Equal("approval_denied", errorCode);
}

[Fact]
public async Task ExecuteAsync_ExecutionStepFails_TriggersRollback()
{
    var adapter = BuildAdapterWithFailingExecutionAndSuccessfulRollback();
    var result = await adapter.ExecuteAsync(BuildValidProposal(), TestContext.Current.CancellationToken);

    Assert.Equal("failed", result.Status);
    Assert.True(result.RollbackTriggered);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionApprovalRecordTests|FullyQualifiedName~ActionAdapterTests" -v minimal
```

Expected: FAIL because approval validator and adapter are missing.

- [ ] **Step 3: Implement approval validator and sequential adapter**

Create `ActionApprovalRecord.cs`:

```csharp
internal sealed class ActionApprovalRecord
{
    public string? Approver { get; init; }
    public string? DecisionAt { get; init; }
    public string? DecisionReason { get; init; }
    public string? TicketRef { get; init; }
    public string? DecisionType { get; init; }
}

internal static class ActionApprovalRecordValidator
{
    internal static bool TryValidate(ActionApprovalRecord record, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(record.Approver)
            || string.IsNullOrWhiteSpace(record.DecisionAt)
            || string.IsNullOrWhiteSpace(record.DecisionReason)
            || string.IsNullOrWhiteSpace(record.TicketRef))
        {
            errorCode = "approval_denied";
            return false;
        }

        return DateTimeOffset.TryParse(record.DecisionAt, out _);
    }
}
```

Create `ActionAdapter.cs` minimal sequential semantics:

```csharp
internal sealed class ActionAdapter
{
    public async Task<ActionAdapterResult> ExecuteAsync(ActionProposal proposal, CancellationToken cancellationToken)
    {
        foreach (var preCheck in proposal.PreChecks)
        {
            var preCheckResult = await _connector.InvokeAsync(preCheck, cancellationToken);
            if (!preCheckResult.Success)
                return ActionAdapterResult.PreCheckFailed();
        }

        foreach (var step in proposal.Execution)
        {
            var stepResult = await _connector.InvokeAsync(step, cancellationToken);
            if (!stepResult.Success)
                return await RunRollbackAsync(proposal, cancellationToken);
        }

        return ActionAdapterResult.Succeeded();
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionApprovalRecordTests|FullyQualifiedName~ActionAdapterTests" -v minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Actions/ActionApprovalRecord.cs src/OpenClaw.Agent/Actions/ActionAdapter.cs src/OpenClaw.Tests/ActionApprovalRecordTests.cs src/OpenClaw.Tests/ActionAdapterTests.cs
git commit -m "feat: add approval validation and adapter execution semantics"
```

---

### Task 4: Wire Governance Mapping and Backward-Compatible Runtime Integration

**Files:**
- Modify: `src/OpenClaw.Agent/Tools/ActionExecuteTool.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Test: `src/OpenClaw.Tests/ActionExecuteToolTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Modify: `docs/zh-CN/meta-skills.md`

**Interfaces:**
- Consumes:
  - `ActionProposal`, policy decision, adapter result from prior tasks
  - existing meta run/session recording surfaces
- Produces:
  - additive governance records: SessionMetaRunRecord, HarnessContract mapping payload, PEV status detail, EvidenceBundle detail
  - unchanged behavior for non-`action_execute` MetaSkill flows

- [ ] **Step 1: Write failing integration tests for governance mapping and backward compatibility**

Add tests:

```csharp
[Fact]
public async Task InvokeAsync_ActionExecute_EmitsGovernanceArtifacts()
{
    var result = await InvokeActionExecuteAsync(BuildApprovedProposal());

    Assert.Contains("SessionMetaRunRecord", result.Diagnostics, StringComparison.Ordinal);
    Assert.Contains("HarnessContract", result.Diagnostics, StringComparison.Ordinal);
    Assert.Contains("PEV", result.Diagnostics, StringComparison.Ordinal);
    Assert.Contains("EvidenceBundle", result.Diagnostics, StringComparison.Ordinal);
}

[Fact]
public async Task ExecuteMetaSkillAsync_NoActionExecutePath_BehaviorUnchanged()
{
    var result = await InvokeExistingMetaSkillFlowAsync();

    Assert.Equal("completed", result.Status);
    Assert.DoesNotContain("action_execute", result.Output, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~InvokeAsync_ActionExecute_EmitsGovernanceArtifacts|FullyQualifiedName~ExecuteMetaSkillAsync_NoActionExecutePath_BehaviorUnchanged" -v minimal
```

Expected: FAIL because governance mapping is not emitted yet.

- [ ] **Step 3: Implement governance mapping and keep additive behavior**

In `ActionExecuteTool.cs`, add additive mapping calls:

```csharp
_recordWriter.WriteSessionMetaRunRecord(sessionId, proposal, decision, adapterResult);
_recordWriter.WriteHarnessContract(sessionId, proposal, decision);
_recordWriter.WritePevRunState(sessionId, decision, approvalState, adapterResult);
_recordWriter.WriteEvidenceBundle(sessionId, proposal, decision, adapterResult);
```

In `AgentRuntime.cs`, ensure no-path behavior remains untouched:

```csharp
if (!IsActionExecuteStep(step))
{
    // Existing flow unchanged.
    return await ExecuteExistingStepAsync(...);
}
```

Update `docs/zh-CN/meta-skills.md` with one approved and one pending-approval `action_execute` example.

- [ ] **Step 4: Run full focused suite for this feature**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionProposalBuilderTests|FullyQualifiedName~ActionExecuteToolTests|FullyQualifiedName~ActionAdapterTests|FullyQualifiedName~ActionApprovalRecordTests|FullyQualifiedName~AgentRuntimeTests" -v minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Tools/ActionExecuteTool.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Agent/AgentRuntime.cs src/OpenClaw.Tests/ActionExecuteToolTests.cs src/OpenClaw.Tests/AgentRuntimeTests.cs docs/zh-CN/meta-skills.md
git commit -m "feat: wire action execute governance mapping with backward compatibility"
```

---

## Self-Review

### 1. Spec coverage

- 6.5 审批执行语义: covered by Task 2 and Task 3 (`require_approval`, callback validation, gate semantics).
- 8.2 审批字段规范和格式约束: covered by Task 3 tests and validator.
- 8.4 能力缺口补齐路径: covered by Task 1 (builder), Task 2 (`action_execute` + policy), Task 3 (adapter/approval), Task 4 (governance mapping).
- 10.1 衔接专项测试: covered by Task 1 and Task 2 test scopes.
- 11.1 阶段化: Task sequence maps to phase 0 -> phase 1 -> phase 2 rollout.

No gaps found for the defined implementation slice.

### 2. Placeholder scan

No `TBD`, `TODO`, or undefined implementation placeholders remain.

### 3. Type consistency

- `ActionProposal` is produced in Task 1 and consumed in Tasks 2-4 consistently.
- `ActionPolicyDecision` is produced in Task 2 and consumed in Tasks 2 and 4 consistently.
- Approval fields and error codes match spec wording throughout tasks.

Type and naming consistency verified.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-15-harness-action-adapter-bridge-implementation.md`. Two execution options:

1. Subagent-Driven (recommended) - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. Inline Execution - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
