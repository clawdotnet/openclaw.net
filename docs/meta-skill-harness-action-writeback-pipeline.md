# MetaSkill Temp Graph Inference → Harness Action Business API Writeback: Full Pipeline Guide

- Document date: 2026-07-16
- Scope: OpenClaw.NET
- Language: English

## 1. Overview

This document describes the complete data flow from MetaSkill DAG consuming a temporary graph
(`load_temporary_graph`), through LLM inference producing a structured ActionProposal, to Harness
Action policy-gated execution, finally writing back to a business API via the HTTP Connector.

**Core principles:**

- Separation of inference and execution: MetaSkill is responsible for reasoning and proposal; the
  Harness policy layer is responsible for risk classification and execution decisions
- Write path only via business API Connector; direct database writes are blocked
- Full audit trail: proposal → classification → approval → execution → compensation
- Existing MetaSkills that do not call `action_execute` are unaffected

## 2. Architecture Overview

```
External Slicer (SPARQL CONSTRUCT + JSON-LD Framing)
        │
        ▼
  Temp Graph File (.jsonld / .json / .md)
        │
        ▼
┌─ MetaSkill DAG ─────────────────────────────────────────────────────┐
│                                                                      │
│  Step 1: load_graph           kind: tool_call                       │
│           tool: load_temporary_graph                                 │
│           Reads temp graph → outputs["load_graph"]                   │
│                                                                      │
│  Step 2: reason               kind: llm_chat                        │
│           depends_on: [load_graph]                                   │
│           system_prompt: {{ outputs.load_graph }}                    │
│           LLM infers → ActionProposal JSON                           │
│           output_contract validates → outputs["reason"]              │
│                                                                      │
│  Step 3: execute              kind: tool_call                       │
│           depends_on: [reason]                                       │
│           tool: action_execute                                       │
│           proposal: {{ outputs.reason }}                             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Harness Action Execution Layer ────────────────────────────────────┐
│                                                                      │
│  ActionExecuteTool                                                   │
│    ├─ ActionProposalBuilder.Normalize()  → typed ActionProposal     │
│    ├─ ActionPolicyEngine.Evaluate()      → risk classification      │
│    │    ├─ low       → proceed_execute    → auto-execute             │
│    │    ├─ medium    → require_approval   → execute after approval    │
│    │    ├─ high/critical → proposal_only  → proposal only            │
│    │    └─ unknown   → policy_denied      → reject                   │
│    └─ ActionAdapter.ExecuteAsync()        → preCheck/execution/rollback│
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ HTTP Connector Layer ──────────────────────────────────────────────┐
│                                                                      │
│  HttpActionAdapterConnector                                          │
│    ActionCall { Call: "crm.updateCustomerTier", Args: {...} }        │
│      ↓                                                               │
│    POST https://crm.example.com/api/v1/updateCustomerTier            │
│    Authorization: Bearer <token-from-env>                            │
│      ↓                                                               │
│    2xx → Succeeded │ 4xx/5xx → connector_error │ timeout → unavailable │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Governance Artifacts ──────────────────────────────────────────────┐
│                                                                      │
│  governanceMapping:                                                  │
│    sessionMetaRunRecord → DAG step-level audit                       │
│    harnessContractId    → action intent and constraints               │
│    pevId                → decision and approval state                 │
│    evidenceBundleId     → execution and compensation evidence         │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

## 3. Data Flow in Detail

### 3.1 Temporary Graph Loading

The MetaSkill DAG `tool_call` step invokes the `load_temporary_graph` tool.

**Tool definition:** [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs)

```yaml
# DAG step definition
- id: load_graph
  kind: tool_call
  tool: load_temporary_graph
  with:
    path: "./tmp/quality-slice.jsonld"
    format: "jsonld"
    max_chars: 120000
```

**Supported input formats:**

| Format | Description |
|--------|-------------|
| `json` / `jsonld` | Direct read of JSON/JSON-LD files |
| `markdown` | Extract fenced code blocks from Markdown, matched by `code_block_language` |
| `auto` | Inferred from file extension |

**Return value structure:**

```json
{
  "status": "ok",
  "source": "./tmp/quality-slice.jsonld",
  "input_format": "jsonld",
  "payload_format": "jsonld",
  "payload_length": 2847,
  "truncated": false,
  "payload_text": "{\"@context\":...}",
  "payload_json": "{\"@context\":...}"
}
```

After execution, the result is stored in `outputs["load_graph"]` for downstream steps to
reference via Jinja2 templates.

### 3.2 LLM Inference

The second step `llm_chat` consumes the graph data from the previous step, performs inference,
and produces an ActionProposal.

**Template interpolation:** [MetaTemplateRenderer.cs](src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs)

```yaml
# DAG step definition
- id: reason
  kind: llm_chat
  depends_on: [load_graph]
  with:
    system_prompt: |
      You are an industrial quality analysis assistant. The input is a temporary graph in JSON-LD.
      Analyze the data and produce root cause candidates and next-step actions.
      Output MUST be a valid ActionProposal JSON object.
    input: |
      Temporary graph payload:
      {{ outputs.load_graph }}
    output_contract:
      format: json
      required_properties: [actionName, target, execution, idempotencyKey]
```

`{{ outputs.load_graph }}` is rendered by `MetaTemplateRenderer` using Jinja2.NET. The render
context includes:

- `input` — user's original input text
- `outputs` — dictionary of completed step outputs (keyed by stepId)
- `inputs` — externally passed input parameters
- `steps` — step configurations

The LLM's JSON output is validated against `output_contract` (at minimum containing `actionName`,
`target`, `execution`, `idempotencyKey`), then stored in `outputs["reason"]`.

### 3.3 Harness Action Execution

The third step `tool_call` invokes the `action_execute` tool, feeding the LLM's proposal into
the Harness execution pipeline.

```yaml
- id: execute
  kind: tool_call
  depends_on: [reason]
  tool: action_execute
  with:
    proposal: "{{ outputs.reason }}"
    decision: proceed
```

**Internal flow of `action_execute`:** [ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs)

#### 3.3.1 Proposal Normalization

```
ActionProposalBuilder.Normalize(rawLLMOutput) → ActionProposalNormalizationResult
  ├─ JSON deserialization into ActionProposal
  ├─ Database write interception (target.system = db/database/sql → policy_denied)
  ├─ At least one execution step required
  └─ Returns a strongly-typed ActionProposal
```

#### 3.3.2 Policy Evaluation

```
ActionPolicyEngine.Evaluate(proposal) → ActionPolicyDecision
  ├─ Known system whitelist: crm, salesforce, hubspot, zendesk, stripe, slack, notion
  ├─ Unknown system → policy_denied (high risk)
  ├─ metadata.policyDecision overrides → require_approval | proposal_only
  └─ Default → proceed_execute (low risk)
```

#### 3.3.3 Decision Matrix

| Risk Level | Policy Decision | Tool Status | Adapter Invoked | Notes |
|------------|----------------|-------------|-----------------|-------|
| low | `proceed_execute` | `execution_completed` | ✅ Yes | Auto-executes preCheck→execution |
| medium | `require_approval` | `pending_approval` (1st call) / `execution_completed` (after approval) | ✅ After approval | 1st call returns pending; 2nd call with approval payload executes |
| high | `proposal_only` | `proposal_only` | ❌ No | Proposal only, no execution |
| critical | `proposal_only` | `proposal_only` | ❌ No | Strictly proposal only |
| unknown | `policy_denied` | `failed` | ❌ No | Unknown connector rejected |

#### 3.3.4 Adapter Execution Semantics

When the decision is `proceed_execute` and an `ActionAdapter` is injected:

```
ActionAdapter.ExecuteAsync(proposal)
  ├─ 1. Idempotency check: TryRegister(idempotencyKey)
  │      Already registered → idempotency_conflict
  ├─ 2. preCheck: iterate proposal.PreChecks
  │      IActionAdapterConnector.InvokeAsync(preCheck)
  │      Any failure → abort (no rollback triggered)
  ├─ 3. execution: iterate proposal.Execution
  │      IActionAdapterConnector.InvokeAsync(step)
  │      Any failure → triggers rollback chain
  └─ 4. rollback: iterate proposal.Rollback (only on execution failure)
         IActionAdapterConnector.InvokeAsync(rollbackStep)
         All succeed → rolled_back
         Any fails → rollback_failed (escalate to human)
```

**Implementation:** [ActionAdapter.cs](src/OpenClaw.Agent/Actions/ActionAdapter.cs)

### 3.4 HTTP Connector Writeback

`HttpActionAdapterConnector` maps an `ActionCall` to an HTTP POST request.

**Implementation:** [HttpActionAdapterConnector.cs](src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs)

#### Mapping Rules

```
ActionCall.Call  →  {system}.{operation}  e.g. "crm.updateCustomerTier"
ActionCall.Args  →  JSON Request Body     e.g. {"customerId": "C123", "tier": "B"}
ConnectorConfig  →  BaseUrl + Auth + Timeout
```

**Resolution flow:**

```
1. Extract system (before '.') and operation (after '.') from Call
2. Look up system in ActionAdapterConfig.Connectors → ConnectorDefinition
3. Validate operation against AllowedCalls whitelist
4. Construct URL: {BaseUrl}/{operation}
5. Apply authentication: Bearer Token / ApiKey Header / None
6. Send POST request, Body = JSON serialization of Args
7. Response mapping:
   2xx → ActionAdapterStepResult.Succeeded()
   4xx/5xx → ActionAdapterStepResult.Failure("connector_error")
   timeout → ActionAdapterStepResult.Failure("connector_unavailable")
```

#### Defense in Depth

| Layer | Location | Mechanism |
|-------|----------|-----------|
| Layer 1 | `ActionProposalBuilder` | Blocks `target.system` = db/database/sql; intercepts SQL write keywords in execution calls |
| Layer 2 | `ActionPolicyEngine` | Unknown connector systems → `policy_denied` |
| Layer 3 | `HttpActionAdapterConnector` | `AllowedCalls` whitelist validation; blocks calls containing sql/db/database keywords |
| Layer 4 | `ConnectorActionContractValidator` | Approval field completeness when `require_approval`; UTC ISO-8601 format validation |

### 3.5 Governance Artifacts

Each `action_execute` invocation produces a governance mapping linking four audit entities:

**Implementation:** [ActionExecuteTool.cs:160-170](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs#L160-L170)

```json
{
  "governanceMapping": {
    "sessionMetaRunRecord": "session_meta_run_record_pending",
    "harnessContractId": "hctr_<idempotencyKey>",
    "pevId": "pev_<idempotencyKey>",
    "evidenceBundleId": "evb_<idempotencyKey>"
  }
}
```

When the adapter executes, additional fields are emitted:

```json
{
  "status": "execution_completed",
  "rollbackTriggered": false,
  "statusHistory": ["succeeded"],
  "failureCode": null
}
```

## 4. Configuration

### 4.1 Enabling ActionAdapter

Configuration path: `Harness:ActionAdapter`

```json
{
  "Harness": {
    "ActionAdapter": {
      "Enabled": true,
      "DefaultDecisionMode": "risk-tiered",
      "IdempotencyWindowMinutes": 60,
      "MaxExecutionSteps": 10,
      "MaxRollbackSteps": 5,
      "Connectors": {
        "crm": {
          "BaseUrl": "https://crm.example.com/api/v1",
          "Auth": {
            "Type": "Bearer",
            "TokenEnv": "CRM_API_TOKEN"
          },
          "TimeoutSeconds": 30,
          "AllowedCalls": ["updateCustomerTier", "createCase", "addNote"],
          "RetryCount": 0
        },
        "stripe": {
          "BaseUrl": "https://api.stripe.com/v1",
          "Auth": {
            "Type": "Bearer",
            "TokenEnv": "STRIPE_API_KEY"
          },
          "AllowedCalls": ["refundCharge", "updateSubscription"]
        }
      }
    }
  }
}
```

### 4.2 Safety Degradation

| Scenario | Behavior |
|----------|----------|
| `Enabled: false` | Adapter not instantiated; all actions return `execution_started` / `pending_approval` (proposal only) |
| `DefaultDecisionMode: "proposal-only"` | All risk tiers return `proposal_only` |
| Env var Token not set | `InvokeAsync` returns `connector_unavailable`; rollback triggered |
| Policy engine unavailable | Degrades to `proposal_only` |

### 4.3 Zero-Config Default Behavior

When `Harness:ActionAdapter` is not configured or `Enabled: false`, `action_execute` is fully
backward-compatible — policy evaluation and governance mapping work as normal, but no business API
calls are made. All existing MetaSkill behavior is unaffected.

## 5. Complete DAG Example

```yaml
kind: meta
name: quality-root-cause-assistant
description: |
  Consumes a temporary JSON-LD graph, infers root causes and next actions,
  auto-executes low-risk actions.
composition:
  steps:
    - id: load_graph
      kind: tool_call
      tool: load_temporary_graph
      with:
        path: "./tmp/quality-slice.jsonld"
        format: "jsonld"
        max_chars: 120000

    - id: reason
      kind: llm_chat
      depends_on: [load_graph]
      with:
        system_prompt: |
          You are an industrial quality analysis assistant.
          1. Analyze the product batch defect rate and contributing factors in the graph.
          2. Propose root cause candidates.
          3. Output next-step actions as a valid ActionProposal JSON.
        input: |
          Temporary graph payload:
          {{ outputs.load_graph }}
        output_contract:
          format: json
          required_properties: [actionName, target, execution, idempotencyKey]

    - id: execute
      kind: tool_call
      depends_on: [reason]
      tool: action_execute
      tool_allowlist: [action_execute]
      with:
        proposal: "{{ outputs.reason }}"
```

**Request-Response Walkthrough:**

```bash
# ActionProposal output by the reason step's LLM:
{
  "actionName": "update_customer_risk_tier",
  "source": {
    "metaSkill": "quality-root-cause-assistant",
    "runId": "run_20260716_001",
    "stepId": "reason"
  },
  "trigger": {
    "condition": "defectRate > 0.02",
    "evidenceRefs": ["ev_batch_001_defect"]
  },
  "target": {
    "system": "crm",
    "operation": "updateCustomerTier"
  },
  "preChecks": [
    {"call": "crm.getCustomer", "args": {"customerId": "C123"}}
  ],
  "execution": [
    {"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "B", "reason": "quality_risk"}}
  ],
  "rollback": [
    {"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "A"}}
  ],
  "idempotencyKey": "quality-C123-20260716-001",
  "metadata": {
    "env": "prod",
    "policyDecision": "proceed_execute"
  }
}

# action_execute response (adapter injected):
{
  "status": "execution_completed",
  "decision": "proceed_execute",
  "riskLevel": "low",
  "reasonCodes": ["policy_passed"],
  "requiredApprovals": [],
  "constraints": [],
  "governanceMapping": {
    "sessionMetaRunRecord": "session_meta_run_record_pending",
    "harnessContractId": "hctr_quality-C123-20260716-001",
    "pevId": "pev_quality-C123-20260716-001",
    "evidenceBundleId": "evb_quality-C123-20260716-001"
  },
  "rollbackTriggered": false,
  "statusHistory": ["succeeded"]
}
```

At this point the CRM system has received:

```
POST https://crm.example.com/api/v1/updateCustomerTier
Authorization: Bearer <CRM_API_TOKEN>
Content-Type: application/json

{"customerId": "C123", "tier": "B", "reason": "quality_risk"}
```

## 6. Key Code Index

| Component | File | Responsibility |
|-----------|------|----------------|
| Temp graph loader | [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) | Reads JSON-LD/JSON/Markdown temp graph files |
| DAG step executor | [AgentRuntime.cs:2545](src/OpenClaw.Agent/AgentRuntime.cs#L2545) | `tool_call` step dispatch and execution |
| Template renderer | [MetaTemplateRenderer.cs](src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs) | Jinja2 `{{ outputs.X }}` interpolation |
| Action execute tool | [ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs) | Proposal normalization + policy evaluation + adapter execution |
| Proposal builder | [ActionProposalBuilder.cs](src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs) | LLM output → strongly-typed ActionProposal |
| Policy engine | [ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) | Connector whitelist + risk classification |
| Execution adapter | [ActionAdapter.cs](src/OpenClaw.Agent/Actions/ActionAdapter.cs) | preCheck/execution/rollback/idempotency |
| HTTP connector | [HttpActionAdapterConnector.cs](src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs) | ActionCall → HTTP POST mapping |
| Contract validator | [ConnectorActionContractModels.cs](src/OpenClaw.Core/Models/ConnectorActionContractModels.cs) | Approval field validation, decision semantics |
| Integration API facade | [IntegrationApiFacade.cs](src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs) | Unified CLI/MCP/HTTP entry point |
| Config model | [GatewayConfig.cs](src/OpenClaw.Core/Models/GatewayConfig.cs) | Connector config, auth, whitelists |
| DI registration | [CoreServicesExtensions.cs](src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs) | Conditional adapter registration chain |

## 7. Test Coverage

| Test File | Count | Coverage |
|-----------|-------|----------|
| `TemporaryGraphToolTests.cs` | 2 | Temp graph loading (JSON + Markdown) |
| `ActionProposalBuilderTests.cs` | 3 | Normalization + DB write interception |
| `ActionPolicyEngineTests.cs` | 5 | Full decision matrix |
| `ActionExecuteToolTests.cs` | 15 | Decision routing + adapter injection + backward compatibility |
| `ActionAdapterTests.cs` | 5 | preCheck/execution/rollback/idempotency |
| `HttpActionAdapterConnectorTests.cs` | 6 | HTTP mapping + whitelist + auth + timeout |
| `ConnectorActionContractTests.cs` | 2 | Contract validation + schema export |
| `ConnectorCommandsTests.cs` | multiple | CLI `connector execute` |
| `GatewayAdminEndpointTests.cs` | multiple | MCP tool + integration endpoint |
| `FullPipelineE2ETests.cs` | 3 | Full pipeline: load_graph → execute → mock HTTP |
| Full regression | 2460 | Zero failures, zero regressions |

## 8. Related Documents

- [MetaSkill Feature Overview](zh-CN/meta-skills.md)
- [MetaSkill User Guide](zh-CN/meta-skill-user-guide.md)
- [MetaSkill Orchestration Architecture](zh-CN/meta-skill-orchestration.md)
- [Harness Action Policy-Gated Adapter Design](superpowers/specs/2026-07-15-harness-action-policy-gated-adapter-design.md)
- [ActionAdapter HTTP Connector Bridge Design](superpowers/specs/2026-07-15-action-adapter-http-connector-bridge-design.md)
- [Harness Action Adapter Bridge Implementation Plan](superpowers/plans/2026-07-15-harness-action-adapter-bridge-implementation.md)
- [Connector CLI MCP Contract Implementation Plan](superpowers/plans/2026-07-15-connector-cli-mcp-contract-implementation.md)
- [ActionAdapter HTTP Connector Bridge Implementation Plan](superpowers/plans/2026-07-15-action-adapter-http-connector-bridge-implementation.md)
- [Chinese Version](zh-CN/meta-skill-harness-action-writeback-pipeline.md)

---

[Site Map](SITE_MAP.md)