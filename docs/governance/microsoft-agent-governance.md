# Microsoft Agent Governance Integration

OpenClaw.NET integrates with Microsoft Agent Governance through the generic
`IToolGovernanceService` boundary. The default implementation is an AOT-safe HTTP sidecar adapter,
so the gateway does not need to reference Microsoft preview packages or make governance a required
runtime dependency.

Microsoft public references currently show action interception and the .NET
`GovernanceKernel.EvaluateToolCall` API. The OpenClaw.NET sidecar adapter therefore keeps the
endpoint configurable instead of treating `/api/v1/execute` as a permanent Microsoft contract.

Useful upstream references:

- [microsoft/agent-governance-toolkit](https://github.com/microsoft/agent-governance-toolkit)
- [Agent OS README](https://github.com/microsoft/agent-governance-toolkit/blob/main/packages/agent-os/README.md)
- [Quickstart](https://github.com/microsoft/agent-governance-toolkit/blob/main/QUICKSTART.md)
- [FAQ](https://github.com/microsoft/agent-governance-toolkit/blob/main/FAQ.md)

## Local Configuration

```json
{
  "OpenClaw": {
    "Governance": {
      "Enabled": true,
      "Provider": "http_sidecar",
      "SidecarBaseUrl": "http://127.0.0.1:8088",
      "DecisionEndpoint": "/api/v1/execute",
      "TimeoutMs": 300,
      "AuditResults": true,
      "FailClosed": true,
      "RequireGovernanceForHighRiskTools": true
    }
  }
}
```

Keep governance disabled for normal local development unless you are running a compatible sidecar.
When governance is enabled, startup requires `SidecarBaseUrl` to be an absolute URL.

## Kubernetes Pattern

Run OpenClaw.NET and the governance sidecar in the same Pod and communicate over localhost. Mount
policies as a ConfigMap so the sidecar can enforce policy without an external policy service.

Example manifests live under:

```text
deploy/kubernetes/governance-sidecar/
```

## OWASP Agentic Top 10 Mapping

Governance decisions can help mitigate tool misuse, excessive agency, unsafe code execution,
data exfiltration, and weak auditability. Do not claim full OWASP Agentic Top 10 coverage unless
the policy set, tests, and deployment controls are mapped and verified for the target environment.

Recommended policy coverage for the first deployment:

| Capability | Suggested default |
| --- | --- |
| `process.execute` / `code.execute` | Require approval or deny. |
| `filesystem.write` | Require approval, scoped paths, and audit. |
| `external.http` / `data.export` | Deny for low-trust agents; audit all access. |
| `message.send` / `email.send` | Require approval unless the channel is tightly scoped. |
| Payment and home automation writes | Require approval and fail closed. |

## Future Direct Adapter

A future optional package such as `OpenClaw.Governance.Microsoft` can wrap the Microsoft .NET
`GovernanceKernel` directly. That package should remain optional and must not be referenced by the
default NativeAOT gateway target.
