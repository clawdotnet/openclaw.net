# Microsoft Agent Framework

OpenClaw.NET includes a supported Microsoft Agent Framework adapter in normal gateway builds. It is first class, but it is not the default runtime path.

Use the default native runtime for fast local agent turns:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Orchestrator": "native"
    }
  }
}
```

Use MAF when you want OpenClaw.NET to run turns through the Microsoft Agent Framework adapter:

```json
{
  "OpenClaw": {
    "Runtime": {
      "Orchestrator": "maf"
    }
  }
}
```

No build property, conditional symbol, or `OpenClawEnableMafExperiment` flag is required.

## Configuration

The supported MAF configuration section is:

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "AgentName": "OpenClaw.NET",
      "AgentDescription": "OpenClaw.NET gateway agent",
      "EnableA2A": false,
      "A2APathPrefix": "/a2a",
      "A2AVersion": "1.0.0",
      "A2APublicBaseUrl": null
    }
  }
}
```

`OpenClaw:Experimental:MicrosoftAgentFramework` is still read for one release cycle for migration. When the legacy section is used, startup emits a warning and records a `configuration/deprecated_maf_section` runtime event.

## A2A

A2A endpoints are opt-in even when the MAF adapter is present:

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true
    }
  }
}
```

Default endpoints:

| Surface | Path |
| --- | --- |
| Agent Card | `/.well-known/agent-card.json` |
| Legacy Agent Card | `/a2a/.well-known/agent-card.json` |
| HTTP+JSON | `/a2a` |
| JSON-RPC | `/a2a/rpc` |

See [../a2a.md](../a2a.md) for endpoint behavior, auth, and current streaming notes.

## Limitations

- `native` remains the default orchestrator in every artifact.
- MAF is included in normal gateway builds, so the gateway dependency graph includes MAF and A2A packages even when `native` is selected.
- A2A execution endpoints are disabled unless `OpenClaw:MicrosoftAgentFramework:EnableA2A=true`.
- Durable workflow orchestration is exposed through workflow backends, not by making every agent turn durable.

## Migration

Replace old experimental config paths:

| Old | New |
| --- | --- |
| `OpenClaw:Experimental:MicrosoftAgentFramework` | `OpenClaw:MicrosoftAgentFramework` |
| `OpenClawEnableMafExperiment=true` | No longer needed |
| `OPENCLAW_ENABLE_MAF_EXPERIMENT` | No longer used |
| `gateway-maf-enabled-*` artifacts | normal gateway artifacts |

To run the MAF adapter:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- \
  --OpenClaw:Runtime:Orchestrator=maf
```

To keep the default runtime, omit the setting or set `OpenClaw:Runtime:Orchestrator=native`.
