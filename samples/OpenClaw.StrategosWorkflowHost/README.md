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

## Notes on the build

- `TreatWarningsAsErrors=true`. The sample must build with **zero warnings**.
- Tests live in a sibling project `samples/OpenClaw.StrategosWorkflowHost.Tests/`
  rather than under `src/OpenClaw.Tests/` because the host itself is a sample, not a
  shipped library. This is a deliberate deviation from the usual convention.
- Strategos is MIT-licensed; see `NOTICE` for full third-party attribution.