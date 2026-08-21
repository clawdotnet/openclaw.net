# OpenClaw.StrategosWorkflowHost

A standalone ASP.NET Core 10 sidecar that hosts a real
[Strategos](https://github.com/levelup-software/strategos) event-sourced saga and
speaks OpenClaw's `maf-durable-http` contract so the gateway can drive it like any
other workflow backend — no gateway code change required.

## What it does

Implements the `durable-agent-review` workflow end-to-end:

1. **Plan** the request with `PlanExecutor`.
2. **Fork** three reviewer agents (security / architecture / cost) in parallel.
3. **Join** their verdicts via `AggregateReviews`.
4. **Assess** aggregate confidence; on low confidence, route to `RequestHumanReview`
   so the audit trail records the path (the `AwaitApproval` gate runs unconditionally
   afterward).
5. **`AwaitApproval<Operator>`** — pause for a human operator. After 4 h it
   escalates to an Admin.
6. **Execute** the approved action with `RevertApprovedAction` compensation.
7. **Audit** the trace via `EmitAuditTrace`.

Every step, including the AwaitApproval pause, is persisted as a Marten event so the
saga survives process crashes (see `KillRestartTests`).

## Endpoints

The host speaks the `maf-durable-http` contract on port 8080:

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/workflows/{workflowId}/run` | Kick off a saga; returns 202 + `AgentWorkflowRunResult`. |
| `GET`  | `/api/workflows/{workflowId}/status/{runId}` | Snapshot: state, pending inputs, events. |
| `POST` | `/api/workflows/{workflowId}/respond/{runId}` | Resume from `AwaitApproval` with the operator's decision. |
| `GET`  | `/api/workflows/{workflowId}` | Workflow summary (id / kind / enabled). |
| `GET`  | `/health` | Liveness probe. |

## Configuration

`appsettings.json` carries the Postgres connection string and the LLM mode. Mock
mode is the P0 default; `DirectOpenAI` and `BackThroughGateway` are wired in
config but throw at startup (P1 follow-up — see `Configuration/LlmMode.cs`).

## Thompson Sampling selector

The sidecar can wrap its `IChatClient` in a `SelectorBackedChatClient` that
routes every chat call through Strategos's Thompson Sampling
`IAgentSelector`. Selected agent ids are recorded in a sidecar-local cache
keyed by `(runId, stepName)` so the later outcome (delivered over
`/runtime-events` from the OpenClaw gateway) can be attributed back to the
agent that produced it.

Disabled by default. To enable:

```json
{
  "Strategos": {
    "Selector": {
      "Enabled": true,
      "AvailableAgents": ["mock"],
      "TaskCategory": "General"
    }
  }
}
```

When enabled, the sidecar also exposes `POST /runtime-events` on the same
port (8080) for the gateway to push workflow outcome events. Configure a
shared bearer token:

```json
{
  "Strategos": {
    "Selector": {
      "Webhook": {
        "TokenSecret": "env:OPENCLAW_SELECTOR_TOKEN"
      }
    }
  }
}
```

The gateway mirrors events via the `RuntimeEventWebhook` (gateway-side)
once its `OpenClaw:RuntimeEvents:Webhook:Url` is set to this sidecar's
`/runtime-events` endpoint and `OpenClaw:RuntimeEvents:Webhook:TokenSecret`
resolves to the same value.

In Mock mode (`Llm__Mode=Mock`), every `AvailableAgents` id resolves to
`MockReviewChatClient` — Thompson Sampling runs, but every "agent" returns
the same verdict, so belief deltas come from noise only. Useful for wiring
smoke tests; not useful for measuring selector quality.

## MCP App registration

The sidecar hosts a second HTTP surface at `/mcp` exposing the Strategos
ontology as MCP tools. When `Strategos:Ontology:ManifestOutputPath` is set, the
sidecar writes an `openclaw.mcpapp.json` manifest into that directory at startup.
The OpenClaw gateway's existing `McpAppDiscovery` + `McpAppRegistry` pick it up
automatically and register the tools as native OpenClaw plugins
(`pluginId = "mcpapp:strategos-ontology"`).

Five tools are advertised over `/mcp`:

| Tool | Purpose |
|------|---------|
| `ontology_explore` | Walk the domain: object types, properties, actions. |
| `ontology_query` | Read object instances / links from the event-sourced state. |
| `ontology_action` | Propose a mutation; hard constraints are enforced server-side. |
| `ontology_validate` | Check a proposed change against the ontology's invariants. |
| `ontology_traverse` | Instance-anchored traversal of a specific object graph. |

### Gateway-side config

Point the gateway's `OpenClaw:McpApps:DiscoveryPaths` at the same directory the
sidecar writes to:

```json
{
  "OpenClaw": {
    "McpApps": {
      "Enabled": true,
      "DiscoveryPaths": ["~/.openclaw/mcp-apps"]
    }
  }
}
```

### Sidecar-side config (`appsettings.Development.json`)

```json
{
  "Strategos": {
    "Ontology": {
      "Enabled": true,
      "Port": 5098,
      "ManifestOutputPath": "~/.openclaw/mcp-apps"
    }
  }
}
```

### docker-compose

Mount the manifest directory from the sidecar into the gateway container so the
two processes share the discovery path:

```yaml
services:
  strategos-host:
    # ... existing service
    volumes:
      - mcp-apps:/home/app/.openclaw/mcp-apps

  # Only if you also run the gateway in this compose:
  # gateway:
  #   image: clawdotnet/openclaw-gateway:latest
  #   ports: ["18789:18789"]
  #   environment:
  #     OpenClaw__McpApps__DiscoveryPaths: "/home/app/.openclaw/mcp-apps"
  #     OpenClaw__McpApps__Enabled: "true"
  #   volumes:
  #     - mcp-apps:/home/app/.openclaw/mcp-apps
  #   depends_on:
  #     - strategos-host

volumes:
  mcp-apps:
```

Once the gateway starts, the five ontology tools become OpenClaw native plugins
callable from any agent conversation turn (and from the gateway's `/mcp`
endpoint) without going through the workflow contract.

The sidecar's `/mcp` endpoint trusts loopback connections. If you tunnel the
gateway to the sidecar across machines, terminate the tunnel on
`127.0.0.1:5098` on the sidecar host or add appropriate auth (out of scope here).

Environment-variable overrides:

- `ConnectionStrings__Postgres` — e.g. `Host=postgres;Port=5432;Database=openclaw_strategos;Username=openclaw;Password=openclaw`
- `Llm__Mode` — `Mock` (default), `DirectOpenAI`, `BackThroughGateway`
- `Llm__OpenAIApiKey`, `Llm__OpenAIModel`
- `Llm__GatewayBaseUrl`, `Llm__GatewayApiToken`

## Running locally

```bash
# Start Postgres + the sidecar (Docker Compose).
docker compose -f samples/OpenClaw.StrategosWorkflowHost/docker-compose.yml up --build
```

Or run against a Postgres you already have:

```bash
export ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=openclaw_strategos;Username=openclaw;Password=openclaw"
dotnet run --project samples/OpenClaw.StrategosWorkflowHost
```

Then kick off a workflow:

```bash
curl -X POST http://localhost:8080/api/workflows/durable-agent-review/run \
  -H 'Content-Type: application/json' \
  -d '{"input":"deploy v2 to production"}'
```

…poll until `waiting_for_input`:

```bash
curl http://localhost:8080/api/workflows/durable-agent-review/status/<runId>
```

…and respond:

```bash
curl -X POST http://localhost:8080/api/workflows/durable-agent-review/respond/<runId> \
  -H 'Content-Type: application/json' \
  -d '{"portId":"operator-approval","approved":true,"comment":"ship it","actorId":"alice"}'
```

## Tests

```bash
# Unit tests (always run; no Postgres required).
dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests

# Kill+restart acceptance (requires Postgres). Defaults to localhost:5432/openclaw_strategos_test;
# override with TEST_PG=...
TEST_PG="Host=...;..." dotnet test samples/OpenClaw.StrategosWorkflowHost.Tests --filter KillRestartTests
```

## Selector webhook (gateway integration)

When `Strategos:Selector:Enabled=true`, the sidecar exposes
`POST /runtime-events` on the same port (8080). The OpenClaw gateway can
mirror its workflow events to this endpoint to close the Thompson Sampling
feedback loop. The shared bearer token is configured independently on each
side and resolved through `SecretResolver`:

| Side | Config key | Source |
|------|-----------|--------|
| Sidecar (receiver) | `Strategos:Selector:Webhook:TokenSecret` | `env:VAR_NAME` or `raw:LITERAL` |
| Gateway (sender)  | `OpenClaw:RuntimeEvents:Webhook:TokenSecret` | same |

Gateway-side config to enable the fan-out:

```json
{
  "OpenClaw": {
    "RuntimeEvents": {
      "Webhook": {
        "Url": "http://sidecar-host:8080/runtime-events",
        "TokenSecret": "env:OPENCLAW_SELECTOR_TOKEN"
      }
    }
  }
}
```

Behavior:

- The webhook fires after every `RuntimeEventStore.Append`, so JSONL stays
  the durable record; webhook failures don't lose data.
- 5xx and connection errors trigger one retry (~1s); 401/403 disable further
  sending until the config is corrected.
- Only entries with `component="workflow"` and `action` in
  `{run_started, run_completed, run_failed, response_sent}` plus the new
  per-step `run_completed`/`run_failed` events emitted by the gateway for
  Strategos saga steps are forwarded. Other components are filtered out.

## Notes on the build

- `TreatWarningsAsErrors=true`. The sample must build with **zero warnings**.
- Tests live in a sibling project `samples/OpenClaw.StrategosWorkflowHost.Tests/`
  rather than under `src/OpenClaw.Tests/` because the host itself is a sample, not a
  shipped library. This is a deliberate deviation from the usual convention.
- Strategos is MIT-licensed; see `NOTICE` for full third-party attribution.