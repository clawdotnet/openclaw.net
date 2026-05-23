# Shared Harness State

Shared Harness State is structured state for delegated and multi-agent work. It records who is participating, what each participant read or changed, what assumptions they made, what evidence supports their work, and where conflicts appear.

It is passive by default. It does not start a multi-agent scheduler, change normal chat behavior, or block tool execution. Operators and future governed workflows can create and inspect shared state explicitly.

## What It Helps Answer

- Which agents or operators participated?
- What did each participant do?
- What resources did they read or write?
- What assumptions did they rely on?
- What version dependencies did they assume?
- What verification obligations remain?
- Did any workstreams conflict?
- What evidence supports the final state?

## Relationship To Other Harness Primitives

- Harness Contracts describe intended work.
- Evidence Bundles record what happened.
- Governance Ledger records human decisions.
- Shared Harness State coordinates multiple participants over the same active work.
- Plan-Execute-Verify Mode can use shared state for multi-participant runs.
- Fractal Memory stores durable project memory, while Shared Harness State tracks active transactional work.

## Admin API

Read endpoints require operator authentication:

```text
GET /admin/harness/shared-state
GET /admin/harness/shared-state/{id}
GET /admin/sessions/{sessionId}/harness-state
```

Mutation endpoints require operator authentication and CSRF protection for browser sessions:

```text
POST /admin/harness/shared-state
POST /admin/harness/shared-state/{id}/participants
POST /admin/harness/shared-state/{id}/actions
POST /admin/harness/shared-state/{id}/detect-conflicts
```

CLI inspection is gateway-backed:

```bash
openclaw harness state list
openclaw harness state show shs_example
openclaw harness state session session-123
openclaw harness state conflicts shs_example
openclaw harness state list --session session-123 --json
```

## Example JSON

```json
{
  "id": "shs_release_docs",
  "sessionId": "session-release",
  "parentSessionId": "session-manager",
  "harnessContractId": "hctr_release",
  "status": "active",
  "goal": "Prepare release notes with independent review",
  "participants": [
    {
      "id": "manager",
      "role": "manager",
      "sessionId": "session-manager",
      "displayName": "Release manager"
    },
    {
      "id": "docs",
      "role": "docs_writer",
      "sessionId": "session-docs"
    }
  ],
  "actions": [
    {
      "id": "draft-release-notes",
      "participantId": "docs",
      "title": "Draft release notes",
      "status": "active",
      "toolName": "file_write",
      "readSet": [
        { "kind": "file", "path": "CHANGELOG.md", "version": "main@abc123" }
      ],
      "writeSet": [
        { "kind": "file", "path": "docs/RELEASES.md" }
      ],
      "versionDependencies": [
        {
          "id": "changelog-version",
          "resource": { "kind": "file", "path": "CHANGELOG.md" },
          "version": "main@abc123"
        }
      ],
      "verifierObligations": [
        {
          "id": "review-docs",
          "title": "Review release notes",
          "verifier": "reviewer",
          "required": true
        }
      ]
    }
  ],
  "evidenceBundleIds": ["evb_release_docs"],
  "tags": ["release", "docs"]
}
```

## Conflict Detection

The initial detector records conflicts and recommendations. It does not block execution.

It detects:

- write/write conflicts when two actions write the same resource
- read/write conflicts when a version-dependent read overlaps another action's write
- assumption conflicts when the same assumption key has different values
- missing verifier obligations for high-risk write actions

Conflict policy defaults are conservative:

- medium- or lower-risk conflicts use `warn`
- high- or critical-risk conflicts use `escalate`

## What This Does Not Do Yet

- It is not full semantic conflict resolution.
- It is not mandatory multi-agent orchestration.
- It does not automatically merge or repair conflicting work.
- It does not block execution by default.
- It is not a replacement for human review.
- It does not automatically capture every delegated tool call yet; use the API or future PEV/delegation integrations for explicit capture.
