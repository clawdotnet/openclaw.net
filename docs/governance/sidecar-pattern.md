# Tool Governance Sidecar Pattern

OpenClaw.NET enforces optional tool governance at one central point: `OpenClawToolExecutor`.
Registered, preset-allowed tool calls are evaluated before approval, sentinel substitution,
sandbox routing, or execution. Unknown tools and preset-blocked tools continue to fail locally
without calling the governance sidecar.

## Runtime Flow

```text
client
  -> OpenClaw.NET gateway / agent runtime
  -> OpenClawToolExecutor
  -> IToolGovernanceService
  -> optional HTTP sidecar decision endpoint
  -> existing approval / hooks / sandbox / executor path
  -> optional sidecar result audit endpoint
```

Governance is disabled by default. When enabled, the runtime sends only redacted tool arguments
and tool metadata to the sidecar. The sidecar decision is attached to the stored `ToolInvocation`,
tool audit entries, activity tags, and logs.

## Configuration

```json
{
  "OpenClaw": {
    "Governance": {
      "Enabled": true,
      "Provider": "http_sidecar",
      "SidecarBaseUrl": "http://127.0.0.1:8088",
      "DecisionEndpoint": "/api/v1/execute",
      "ResultEndpoint": "/api/v1/result",
      "TimeoutMs": 300,
      "AuditResults": true,
      "FailClosed": true,
      "FailOpenReadOnlyLowRisk": false,
      "RequireGovernanceForHighRiskTools": true
    }
  }
}
```

`DecisionEndpoint` defaults to `/api/v1/execute`, but this is an adapter setting, not a hard
OpenClaw.NET contract. Change it if the deployed sidecar uses another route or payload shape.

## Decision Behavior

Supported actions:

| Action | Runtime behavior |
| --- | --- |
| `allow` | Continue through the normal OpenClaw.NET tool path. |
| `deny` | Return a blocked tool result; the tool is not executed. |
| `require_approval` | Use the existing OpenClaw.NET approval callback. If no approval-capable surface is attached, execution is denied. |
| `redact` | Record redacted governance metadata. Execution arguments change only if the sidecar returns explicit replacement arguments. |
| `audit_only` | Continue and attach the governance decision to audit/trace data. |

Sidecar failures and timeouts fail closed by default. High-risk, write-capable, data-export,
network-write, shell, process, and code-execution tools fail closed when
`RequireGovernanceForHighRiskTools` is enabled. Read-only low-risk tools can fail open only when
`FailOpenReadOnlyLowRisk` is explicitly enabled.

## Tool Metadata

OpenClaw.NET keeps a central descriptor catalog for built-in and native tool names. Descriptors
include category, capabilities, risk level, approval hints, filesystem/network/code-execution
flags, and data-export flags. This metadata is sent to the sidecar so policies can reason about
capabilities instead of hardcoding every tool name.

Plugin and dynamic tools that are not in the catalog receive a conservative fallback descriptor:
medium risk, not read-only, and capability `plugin.invoke`.

## Audit Fields

Tool invocations and audit entries include:

- `GovernanceAllowed`
- `GovernanceAction`
- `GovernanceReason`
- `GovernancePolicyId`
- `GovernanceRuleId`
- `GovernanceTrustScore`
- `GovernanceEvaluationMs`

OpenClaw.NET does not send raw tool results to the sidecar result endpoint. Result audit payloads
include status, failure code/message, duration, timeout/failure flags, and result byte count.
