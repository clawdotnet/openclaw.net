# ActionPolicyEngine Risk Gate Hardening Implementation Plan

> **Status:** Completed
> **Implementation date:** 2026-07-28
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) so it implements a deterministic risk-tiered decision path that matches the policy-gated adapter design: low → proceed_execute, medium → require_approval, high/critical → proposal_only, and unknown/invalid inputs fall back safely.

**Implemented behavior:** The engine now checks connector safety first, honors explicit `policyDecision` overrides while preserving valid risk metadata, maps `low/medium/high/critical` to the expected decisions, and downgrades invalid or ambiguous risk input to `proposal_only` with `high` risk.

**Architecture:** Keep the engine as a small, deterministic decision component. Replace the current hard-coded proceed path with a risk classifier that first checks connector safety, then applies explicit policy overrides, then falls back to a configurable risk matrix. The decision result stays compatible with [src/OpenClaw.Agent/Tools/ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs) and existing tests.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json, existing OpenClaw action models

## Global Constraints

- 写路径仅允许业务 API Connector，禁止数据库直写。
- 连接器未知时拒绝执行。
- 判级不确定时按高风险处理。
- 策略引擎不可用时降级 proposal_only。
- 不改变未接入 Action 机制的 MetaSkill 行为。
- Preserve NativeAOT friendliness and avoid reflection-heavy trim-unsafe dependencies in runtime core paths.

---

## Scope Check

This plan covers one subsystem: the policy decision layer. The change remains focused on [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) and its test surface. No additional subsystem split is required.

## File Structure

- Modify: [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs)
  Responsibility: replace the current proceed-by-default logic with risk-based classification, explicit override handling, and protective downgrade.
- Modify: [src/OpenClaw.Tests/ActionPolicyEngineTests.cs](src/OpenClaw.Tests/ActionPolicyEngineTests.cs)
  Responsibility: add red/green tests for low/medium/high/critical decisions, unknown connector denial, and invalid-risk fallback.
- Optional: Modify [src/OpenClaw.Agent/Tools/ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs)
  Responsibility: keep behavior aligned with the new decision semantics; no change should be required unless the tool needs to surface a new failure code.

---

### Task 1: Introduce a risk-tiered classifier in the policy engine

**Files:**
- Modify: [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs)
- Modify: [src/OpenClaw.Tests/ActionPolicyEngineTests.cs](src/OpenClaw.Tests/ActionPolicyEngineTests.cs)

**Interfaces:**
- Consumes: [src/OpenClaw.Core/Models/ActionProposal.cs](src/OpenClaw.Core/Models/ActionProposal.cs) via the existing proposal model
- Produces: `ActionPolicyDecision` with `Decision`, `RiskLevel`, `ReasonCodes`, `RequiredApprovals`, and `Constraints`

- [ ] **Step 1: Write the failing tests for risk-tiered decisions**

Add these tests to [src/OpenClaw.Tests/ActionPolicyEngineTests.cs](src/OpenClaw.Tests/ActionPolicyEngineTests.cs):

```csharp
[Theory]
[InlineData("low", "proceed_execute")]
[InlineData("medium", "require_approval")]
[InlineData("high", "proposal_only")]
[InlineData("critical", "proposal_only")]
public void Evaluate_RiskTier_ReturnsExpectedDecision(string riskLevel, string expectedDecision)
{
    var engine = new ActionPolicyEngine();
    var proposal = BuildProposal("crm", metadata: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["riskLevel"] = riskLevel
    });

    var decision = engine.Evaluate(proposal);

    Assert.Equal(expectedDecision, decision.Decision);
    Assert.Equal(riskLevel, decision.RiskLevel);
}

[Fact]
public void Evaluate_InvalidRiskLevel_FallsBackToProposalOnlyWithHighRisk()
{
    var engine = new ActionPolicyEngine();
    var proposal = BuildProposal("crm", metadata: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["riskLevel"] = "mysterious"
    });

    var decision = engine.Evaluate(proposal);

    Assert.Equal("proposal_only", decision.Decision);
    Assert.Equal("high", decision.RiskLevel);
    Assert.Contains("unknown_risk", decision.ReasonCodes);
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionPolicyEngineTests" -v minimal
```

Expected: FAIL because the engine still returns `proceed_execute` for every known connector and does not understand `riskLevel`.

- [ ] **Step 3: Implement the deterministic classifier in the engine**

Update [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) to add a small classifier and map the decision matrix:

```csharp
public ActionPolicyDecision Evaluate(ActionProposal proposal)
{
    ArgumentNullException.ThrowIfNull(proposal);

    if (!KnownConnectorSystems.Contains(proposal.Target.System))
    {
        return ActionPolicyDecision.PolicyDenied(
            riskLevel: "high",
            reasonCodes: ["unknown_connector"]);
    }

    if (proposal.Metadata.TryGetValue("policyDecision", out var configuredDecision)
        && TryNormalizeDecision(configuredDecision, out var normalizedDecision))
    {
        return ActionPolicyDecision.ForDecision(normalizedDecision);
    }

    var riskLevel = ClassifyRiskLevel(proposal);
    return riskLevel switch
    {
        "low" => ActionPolicyDecision.ForDecision("proceed_execute"),
        "medium" => ActionPolicyDecision.ForDecision("require_approval"),
        "high" or "critical" => ActionPolicyDecision.ForDecision("proposal_only"),
        _ => ActionPolicyDecision.ForDecision("proposal_only")
    };
}

private static string ClassifyRiskLevel(ActionProposal proposal)
{
    if (proposal.Metadata.TryGetValue("riskLevel", out var riskLevel)
        && TryNormalizeRiskLevel(riskLevel, out var normalizedRiskLevel))
    {
        return normalizedRiskLevel;
    }

    var env = proposal.Metadata.TryGetValue("env", out var environment) ? environment : "dev";
    var isProd = string.Equals(env, "prod", StringComparison.OrdinalIgnoreCase);
    var usesBulkWrite = proposal.Execution.Any(step =>
        step.Call.Contains("update", StringComparison.OrdinalIgnoreCase)
        || step.Call.Contains("delete", StringComparison.OrdinalIgnoreCase)
        || step.Call.Contains("create", StringComparison.OrdinalIgnoreCase));

    return isProd && usesBulkWrite ? "high" : "low";
}

private static bool TryNormalizeRiskLevel(string? riskLevel, out string normalizedRiskLevel)
{
    normalizedRiskLevel = string.Empty;
    if (string.IsNullOrWhiteSpace(riskLevel))
        return false;

    var normalized = riskLevel.Trim().ToLowerInvariant();
    if (normalized is "low" or "medium" or "high" or "critical")
    {
        normalizedRiskLevel = normalized;
        return true;
    }

    return false;
}
```

Also update the decision factory so `require_approval` returns `medium` and `proposal_only` returns `high` when used as fallback or manual override:

```csharp
public static ActionPolicyDecision ForDecision(string decision)
    => decision switch
    {
        "require_approval" => new ActionPolicyDecision
        {
            Decision = "require_approval",
            RiskLevel = "medium",
            ReasonCodes = ["approval_required"],
            RequiredApprovals = ["operator"],
            Constraints = []
        },
        "proposal_only" => new ActionPolicyDecision
        {
            Decision = "proposal_only",
            RiskLevel = "high",
            ReasonCodes = ["proposal_only_mode"],
            RequiredApprovals = [],
            Constraints = ["no_execution"]
        },
        _ => new ActionPolicyDecision
        {
            Decision = "proceed_execute",
            RiskLevel = "low",
            ReasonCodes = ["policy_passed"],
            RequiredApprovals = [],
            Constraints = []
        }
    };
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionPolicyEngineTests" -v minimal
```

Expected: PASS for the new risk-tier tests.

---

### Task 2: Add safety fallback and explicit denial semantics for uncertain policies

**Files:**
- Modify: [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs)
- Modify: [src/OpenClaw.Tests/ActionPolicyEngineTests.cs](src/OpenClaw.Tests/ActionPolicyEngineTests.cs)

**Interfaces:**
- Consumes: the same proposal model and metadata bag
- Produces: policy-denied or proposal-only results for unsafe or ambiguous inputs

- [ ] **Step 1: Write the failing tests for protective downgrade**

Add these tests:

```csharp
[Fact]
public void Evaluate_UnknownConnector_ReturnsPolicyDenied()
{
    var engine = new ActionPolicyEngine();
    var proposal = BuildProposal("unknown_db_system");

    var decision = engine.Evaluate(proposal);

    Assert.Equal("policy_denied", decision.Decision);
    Assert.Equal("high", decision.RiskLevel);
    Assert.Contains("unknown_connector", decision.ReasonCodes);
}

[Fact]
public void Evaluate_MissingRiskMetadata_UsesSafeDefault()
{
    var engine = new ActionPolicyEngine();
    var proposal = BuildProposal("crm", metadata: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["env"] = "dev"
    });

    var decision = engine.Evaluate(proposal);

    Assert.Equal("proceed_execute", decision.Decision);
    Assert.Equal("low", decision.RiskLevel);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionPolicyEngineTests" -v minimal
```

Expected: the new tests fail until the engine applies a protective fallback and explicit denial path.

- [ ] **Step 3: Implement the fallback logic**

In [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs), keep the early `policy_denied` branch for unknown connectors, then add a fallback branch for ambiguous risk values:

```csharp
if (proposal.Metadata.TryGetValue("policyDecision", out var configuredDecision)
    && TryNormalizeDecision(configuredDecision, out var normalizedDecision))
{
    return ActionPolicyDecision.ForDecision(normalizedDecision);
}

var riskLevel = ClassifyRiskLevel(proposal);
if (riskLevel is null)
{
    return new ActionPolicyDecision
    {
        Decision = "proposal_only",
        RiskLevel = "high",
        ReasonCodes = ["unknown_risk"],
        RequiredApprovals = [],
        Constraints = ["no_execution"]
    };
}
```

The classifier should also mark any unsupported explicit value as `high`/`proposal_only` rather than silently defaulting to `proceed_execute`.

- [ ] **Step 4: Run the tests and verify they pass**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionPolicyEngineTests" -v minimal
```

Expected: PASS.

---

### Task 3: Align the tool-facing contract with the new engine semantics

**Files:**
- Modify: [src/OpenClaw.Agent/Tools/ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs)
- Optional: Modify [src/OpenClaw.Tests/ActionExecuteToolTests.cs](src/OpenClaw.Tests/ActionExecuteToolTests.cs)

**Interfaces:**
- Consumes: `ActionPolicyDecision` from the engine
- Produces: the same tool response shape used by existing integration tests

- [ ] **Step 1: Add or update a regression test for engine-driven routing**

Add one test to [src/OpenClaw.Tests/ActionExecuteToolTests.cs](src/OpenClaw.Tests/ActionExecuteToolTests.cs) that verifies a `require_approval` decision from the engine produces `pending_approval`:

```csharp
[Fact]
public async Task ExecuteAsync_EngineRequireApprovalDecision_ReturnsPendingApproval()
{
    var tool = new ActionExecuteTool(new ActionPolicyEngine(), null);
    var proposal = BuildProposalJson(metadataFragment: "\"riskLevel\":\"medium\"");

    var result = await tool.ExecuteAsync(BuildArguments(proposal), TestContext.Current.CancellationToken);

    Assert.Contains("pending_approval", result, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the test to verify it passes without changing the existing tool contract**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ActionExecuteToolTests" -v minimal
```

Expected: PASS because the tool already reads the engine decision and routes on it.

- [ ] **Step 3: Keep the tool contract unchanged unless a new failure code is needed**

No new response shape is required. The existing tool contract stays compatible as long as the engine returns the same decisions: `proceed_execute`, `require_approval`, `proposal_only`, and `policy_denied`.

---

## Implementation Notes

The key implementation points in [src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) are:

1. Replace the current default `proceed_execute` path with a `ClassifyRiskLevel` helper.
2. Map `low` → `proceed_execute`, `medium` → `require_approval`, `high`/`critical` → `proposal_only`.
3. Preserve the existing `policy_denied` branch for unknown connectors.
4. Treat invalid or missing risk values as high-risk and downgrade to `proposal_only`.
5. Keep decision reasons explicit so the tool and future auditing layers can explain why a proposal was allowed, gated, or denied.

## Acceptance Criteria

- Known low-risk proposals return `proceed_execute`.
- Known medium-risk proposals return `require_approval`.
- High or critical proposals return `proposal_only`.
- Unknown connectors return `policy_denied`.
- Invalid or ambiguous risk metadata falls back to `proposal_only` with `high` risk.
- Existing action tool integration remains backward compatible.

## Verification

Verified with:

```bash
$env:NuGetAudit='false'; dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~ActionPolicyEngineTests|FullyQualifiedName~ActionExecuteToolTests" -v minimal --nologo -p:TreatWarningsAsErrors=false -p:WarningsNotAsErrors=NU1902
```

Result: `EXIT:0`, `27 passed, 0 failed`.
