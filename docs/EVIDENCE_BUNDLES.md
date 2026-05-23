# Evidence Bundles

An Evidence Bundle is a structured record of what happened during an agent run or action. It captures tool results, checks, approvals, runtime events, risks, assumptions, untested areas, and human review.

Evidence Bundles are passive in this release. They are created only when code or an operator explicitly creates them. They do not change normal chat behavior, quickstart, provider behavior, existing tool execution, approval behavior, memory behavior, Companion setup, MCP routes, or OpenAI-compatible routes.

## Why They Exist

Evidence Bundles help operators answer:

- What did the agent do?
- Which tools were called?
- What checks ran?
- What passed or failed?
- What risks remain?
- Who reviewed or approved the action?
- What evidence supports accepting or rejecting the result?

They are intended as a foundation for future Plan-Execute-Verify mode, Harness Contracts, Governance Ledger entries, Harness Evolution Proposals, Runtime Pulse alerts, Industrial Pack workflows, audit export, trajectory export, and operator trust workflows.

## Bundle Shape

Each bundle can record:

- source session, Harness Contract, learning proposal, tool call, and automation run IDs
- actor, channel, and sender metadata
- confidence level
- evidence items
- checks and command results
- risks and mitigations
- assumptions and untested areas
- human reviews
- tags and metadata

Example:

```json
{
  "id": "evb_docs_update",
  "title": "Documentation update evidence",
  "summary": "Focused tests passed and the operator reviewed the generated docs.",
  "sourceSessionId": "session-123",
  "harnessContractId": "hctr_docs_update",
  "confidence": "high",
  "items": [
    {
      "id": "item_tests",
      "kind": "test_result",
      "title": "Focused test run",
      "summary": "Evidence Bundle and admin endpoint tests passed.",
      "status": "passed",
      "outputSummary": "dotnet test completed successfully."
    }
  ],
  "checks": [
    {
      "id": "check_build",
      "name": "Build and tests",
      "kind": "command",
      "required": true,
      "status": "passed",
      "command": "dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj",
      "summary": "Focused tests passed."
    }
  ],
  "risks": [
    {
      "riskLevel": "low",
      "description": "Only passive storage and admin inspection were changed.",
      "mitigation": "No default chat, provider, approval, memory, MCP, or OpenAI route hooks were added."
    }
  ],
  "assumptions": [
    {
      "id": "assumption_manual",
      "text": "Operators create bundles manually in v1.",
      "verified": true
    }
  ],
  "untestedAreas": [
    {
      "id": "untested_pev",
      "description": "Full Plan-Execute-Verify orchestration is not implemented in this release.",
      "riskLevel": "medium"
    }
  ],
  "humanReviews": [
    {
      "reviewer": "operator",
      "decision": "accepted",
      "notes": "The evidence matches the change scope."
    }
  ],
  "tags": ["harness", "evidence"]
}
```

## Admin Inspection

Operators can inspect and manually append Evidence Bundle content through:

- `GET /admin/harness/evidence`
- `GET /admin/harness/evidence/{id}`
- `POST /admin/harness/evidence`
- `POST /admin/harness/evidence/{id}/items`
- `POST /admin/harness/evidence/{id}/checks`
- `POST /admin/harness/evidence/{id}/reviews`

Read endpoints require authenticated admin viewer access. Mutation endpoints require operator-level access and the same CSRF protections used by existing admin mutations.

Trajectory export remains unchanged by default. Operators can opt in to linked evidence records with `includeEvidence=true`, which emits additional JSONL records of type `evidence_bundle` for bundles linked by `SourceSessionId`.

## Relationship To Harness Contracts

Harness Contract = what the agent planned to do.

Evidence Bundle = what happened, what was checked, what remains uncertain, and why the result should or should not be trusted.

An Evidence Bundle may reference a Harness Contract by ID, but it does not require the contract record to exist. That keeps evidence exportable and usable for sessions, proposals, tool calls, automations, or manual operator review.

## What This Does Not Do Yet

- It is not full Plan-Execute-Verify mode.
- It is not automatic verification for every run.
- It is not enabled for all normal chat by default.
- It does not automatically create bundles for every tool call.
- It is not automatic rollback.
- It is not a replacement for tool approvals or human review.
- It does not weaken existing approval, security, provider, memory, quickstart, MCP, or OpenAI-compatible behavior.
