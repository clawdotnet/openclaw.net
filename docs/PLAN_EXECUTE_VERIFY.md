# Plan-Execute-Verify Mode

Plan-Execute-Verify Mode is an optional governed execution mode for OpenClaw.NET.

It turns non-trivial agent work into:

```text
intent
-> Harness Contract
-> approval/governance
-> execution
-> Evidence Bundle
-> verification
-> accept/revise/escalate/rollback
```

The mode is designed for bounded, inspectable, and verifiable work. It is disabled by default and does not change normal chat behavior unless explicitly enabled.

## When To Use It

Use Plan-Execute-Verify Mode for work that should leave a governed trail before it is trusted:

- high-risk tools
- file writes
- shell execution
- browser actions
- external API calls
- multi-tool workflows
- learning proposal application
- public or channel-triggered actions
- industrial and operational workflows
- future remote execution backends

## What It Does

When enabled, the runtime wraps configured high-risk tool execution with a PEV run:

1. Classifies the tool/action using existing tool governance descriptors and action metadata.
2. Creates a Harness Contract for configured high-risk or write-capable work.
3. Preserves existing approval behavior and requires approval for configured risk levels.
4. Creates an Evidence Bundle when configured.
5. Records tool outcomes, approval evidence, verification checks, and governance decisions.
6. Verifies the result with initial built-in verifiers.
7. Marks the run and linked contract as verified, failed, rejected, cancelled, or escalated.

The first implementation wraps the central tool execution path. It does not replace the full agent loop.

## Built-In Verification

Initial verifiers include:

- `ToolOutcomeVerifier`: passes when required tool actions complete successfully.
- `ApprovalVerifier`: passes when required approvals were recorded as approved.
- `ContractCompletenessVerifier`: warns when success criteria, verification plan, or rollback plan are missing.
- `SecurityPostureVerifier`: warns on unsafe public-bind approval posture when detectable from config.

Verification failure fails safely. By default, OpenClaw.NET does not automatically roll back work. Failed verification recommends revision or operator escalation unless explicit safe rollback support is added later.

## Configuration

JSON configuration example:

```json
{
  "OpenClaw": {
    "harness": {
      "executionMode": "plan-execute-verify",
      "planExecuteVerify": {
        "enabled": true,
        "contractRequiredFor": [
          "high_risk_tools",
          "write_tools",
          "shell",
          "browser",
          "external_api",
          "multi_tool_workflows"
        ],
        "requireApprovalForRisk": ["high", "critical"],
        "createEvidenceBundles": true,
        "runVerification": true,
        "autoRollbackOnFailedVerification": false,
        "maxPlanActions": 20,
        "maxVerificationSteps": 20
      }
    }
  }
}
```

Defaults are conservative:

- `executionMode` is `normal`.
- `planExecuteVerify.enabled` is `false`.
- evidence bundles are created only when PEV is enabled.
- verification runs only when PEV is enabled and `runVerification` is true.
- automatic rollback is disabled.
- `multi_tool_workflows` applies when one model response asks OpenClaw to run more than one tool call.

## Admin API

Operator-authenticated endpoints:

- `GET /admin/harness/pev/runs`
- `GET /admin/harness/pev/runs/{id}`
- `POST /admin/harness/pev/runs/{id}/verify`
- `POST /admin/harness/pev/runs/{id}/cancel`

PEV runs link to Harness Contracts and Evidence Bundles when those records are available.

## What This Does Not Do

Plan-Execute-Verify Mode is:

- not enabled by default
- not required for normal chat
- not automatic rollback by default
- not a substitute for human review
- not a guarantee that every task is semantically correct
- not a replacement for unit, integration, or harness regression tests

Use it as an execution governance layer for work where intent, approvals, evidence, and verification need to be inspectable.
