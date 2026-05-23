# Governance Ledger

The Governance Ledger records approval and oversight decisions as durable harness state. It helps operators inspect what was approved or rejected, who made the decision, why, what scope applied, what risk level was involved, and which session, Harness Contract, Evidence Bundle, learning proposal, or approval request the decision related to.

Governance Ledger entries are passive in this release. They do not change normal chat behavior, provider behavior, quickstart, tool execution, approval semantics, memory behavior, Companion setup, MCP routes, or OpenAI-compatible routes.

## Relationship to Harness State

Harness Contract = intended work.

Evidence Bundle = what happened, what was checked, what remains uncertain, and why the result should or should not be trusted.

Governance Ledger = human/operator decision history.

The ledger is separate from the existing approval prompt itself. It records the decision after the existing approval flow decides, so approval failures, denials, timeouts, and requester checks keep their current behavior.

It is also separate from reusable approval grants. A grant may be consumed by existing approval-grant logic, and the ledger can record that fact, but the ledger does not create or apply grants.

## Example

```json
{
  "id": "gov_approval_001",
  "decision": "approved",
  "status": "active",
  "source": "tool_approval",
  "actionType": "execute",
  "toolName": "shell",
  "actionSummary": "Operator approved a shell command after reviewing arguments.",
  "argumentSummary": "{\"cmd\":\"dotnet test\"}",
  "redactedArguments": "{\"cmd\":\"dotnet test\"}",
  "riskLevel": "high",
  "scope": "once",
  "scopeKey": "apr_123",
  "sessionId": "sess_123",
  "harnessContractId": "hctr_123",
  "evidenceBundleId": "evb_123",
  "approvalId": "apr_123",
  "channelId": "web",
  "senderId": "operator",
  "decidedBy": "operator",
  "decisionReason": "Tests and rollback plan were reviewed.",
  "policyHint": {
    "suggestedFutureBehavior": "consider_reusable_grant",
    "suggestedScope": "session",
    "confidence": "medium",
    "requiresReview": true,
    "notes": "Informational only; future automation must be explicit."
  },
  "tags": ["approval", "governance"]
}
```

## What This Does Not Do Yet

- It does not auto-approve future actions.
- It does not replace existing tool approvals or requester-match checks.
- It does not weaken safety behavior.
- It does not enable full Plan-Execute-Verify mode.
- It does not automatically create Evidence Bundles for every approval.
- It does not use `policyHint` for enforcement; future policy automation must be explicit and opt-in.
