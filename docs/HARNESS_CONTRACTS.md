# Harness Contracts

A Harness Contract is a structured plan for agent work. It captures the goal, intended actions, resources touched, tools required, approval needs, verification plan, rollback plan, and success criteria before meaningful execution happens.

Harness Contracts are passive in this release. They do not change normal chat behavior, quickstart, provider behavior, existing tool execution, memory behavior, Companion setup, MCP routes, or OpenAI-compatible routes.

## Why They Exist

Harness Contracts make non-trivial agent work more inspectable before execution. They are useful for:

- high-risk tool use
- file writes
- shell execution
- multi-step workflows
- learning proposals
- future Plan-Execute-Verify mode
- industrial and operational workflows
- future evidence bundles, governance ledger entries, shared harness state, and Runtime Pulse workflows

## Contract Shape

Each contract records:

- intent and user request summary
- planned actions
- read and write resource sets
- tool requirements
- risk level and approval requirement
- assumptions and constraints
- verification plan
- rollback plan
- success criteria
- source session, actor, channel, and sender metadata

Example:

```json
{
  "id": "hctr_docs_update",
  "status": "proposed",
  "goal": "Update documentation for a passive harness feature",
  "userRequestSummary": "Document Harness Contracts and link them from the docs index.",
  "sourceSessionId": "session-123",
  "actorId": "operator",
  "riskLevel": "medium",
  "approvalRequired": "none",
  "plannedActions": [
    {
      "id": "docs",
      "title": "Update docs",
      "toolName": "file_write",
      "actionType": "write",
      "requiresApproval": false,
      "writeSet": [
        {
          "kind": "file",
          "path": "docs/HARNESS_CONTRACTS.md",
          "description": "Harness Contract documentation"
        }
      ],
      "expectedOutcome": "Operators can understand the feature boundary."
    }
  ],
  "verificationPlan": [
    {
      "id": "tests",
      "title": "Run tests",
      "kind": "command",
      "command": "dotnet test",
      "expectedSignal": "All relevant tests pass.",
      "required": true
    }
  ],
  "rollbackPlan": [
    {
      "id": "revert",
      "title": "Revert changes",
      "description": "Revert the feature commit if the passive surface causes regressions."
    }
  ],
  "successCriteria": [
    "Contracts can be stored, listed, and inspected.",
    "Normal runtime behavior is unchanged."
  ],
  "tags": ["harness", "docs"]
}
```

## Admin Inspection

Operators can inspect Harness Contracts through:

- `GET /admin/harness/contracts`
- `GET /admin/harness/contracts/{id}`
- `POST /admin/harness/contracts`
- `POST /admin/harness/contracts/{id}/status`

Read endpoints require authenticated admin viewer access. Mutation endpoints require operator-level access and the same CSRF protections used by existing admin mutations.

## Important Nuance

Harness Contracts do not replace tool approvals. They make approval needs and planned work easier to inspect before execution.

Harness Contracts are also distinct from OpenClaw.NET's existing executable contract-governance API. The existing `ContractPolicy` and `/api/contracts` surface validate and attach execution constraints to sessions. Harness Contracts are structured intent records. A future Plan-Execute-Verify workflow may translate a Harness Contract into executable governance constraints, but this release does not do that automatically.

## What This Does Not Do Yet

- It is not enabled for all normal chat by default.
- It is not full Plan-Execute-Verify mode.
- It does not automatically roll back changes.
- It is not a replacement for existing tool approvals.
- It does not weaken existing approval, security, provider, memory, quickstart, MCP, or OpenAI-compatible behavior.
