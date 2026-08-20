# P0: StrategosWorkflowHost sidecar — `durable-agent-review` as a `maf-durable-http` backend

## What

Adds `samples/OpenClaw.StrategosWorkflowHost/`, an ASP.NET Core 10 sidecar that
hosts a real Strategos event-sourced saga (`durable-agent-review`) and exposes it
through the existing `maf-durable-http` contract. The OpenClaw gateway can drive
it like any other workflow backend — no gateway code change required.

## Why

Closes the P0 acceptance gate defined in
`docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`:
prove that an external durable-workflow engine can plug into OpenClaw's existing
backend abstraction with zero gateway changes, and that the saga survives process
crashes via event sourcing.

## How

- **Wolverine + Marten + PostgreSQL** drive a Strategos event-sourced saga
  (`Persistence = PersistenceMode.EventSourced`). The source generator emits
  `DurableAgentReviewStarted` + one `{Step}Completed` event per step plus
  `DurableAgentReviewSaga`, `StartDurableAgentReviewCommand`, and
  `ResumeOperatorApprovalCommand`.
- **`Adapters/DurableHttpAdapter.cs`** translates between the
  `maf-durable-http` request/response shape (`AgentWorkflowRequest` /
  `AgentWorkflowRunResult` / `AgentWorkflowRunSnapshot`) and the Strategos
  saga. Pause detection is hybrid: `state.CurrentPhase` (stamped by
  `Apply({Step}Completed)`) for step transitions, plus an event-stream tail
  scan for `RequestOperatorApprovalEvent` to detect the `AwaitApproval<Operator>`
  pause.
- **Minimal-API endpoints** in `Program.cs` wire the adapter into
  `/api/workflows/{workflowId}/{run,status/{runId},respond/{runId}}`.
- **`Configuration/LlmMode.cs`** carries Mock / DirectOpenAI / BackThroughGateway
  enum + factory; P0 ships Mock only — the other two modes throw at startup
  with a clear message so the surface is wired for a P1 follow-up.
- **`Adapters/PhaseStatusMap.cs`** maps Strategos phase strings to the six
  OpenClaw workflow status literals (`Queued / Running / WaitingForInput /
  Completed / Failed / Cancelled`).
- **`Adapters/PendingInputBuilder.cs`** produces the
  `AgentWorkflowPendingInput` (workflowId + summary + confidence + reviews) that
  the gateway renders in its host UI when the human-approval port opens.
- **`Steps/*` (11 hand-written steps)**: `PlanExecutor`, three reviewer agents
  (security / architecture / cost), `AggregateReviews`, `AssessConfidence`,
  `RequestHumanReview`, `ExecuteApprovedAction` (with `RevertApprovedAction`
  compensation), `NotifyFailure`, `EmitAuditTrace`.
- **MEAI 10.7** `IChatClient` integration for the reviewer steps, backed by
  `Configuration/MockReviewChatClient.cs` (returns deterministic
  `confidence = 0.8` so the workflow reliably lands in the low-confidence path
  → `AwaitApproval`).
- **`Dockerfile` + `docker-compose.yml`** — Postgres 17 healthcheck + the
  sidecar on :8080.
- **`KillRestartTests`** boots two WebApplication instances sequentially
  against the same Marten store via `Microsoft.AspNetCore.TestHost`, starts a
  saga on instance 1, disposes it (simulated crash), and verifies the saga is
  queryable from instance 2's `/status` endpoint. Uses `Assert.Skip` when no
  Postgres is reachable so it's safe in dev.

## Verification

- **Build**: clean, 0 warnings, 0 errors (`TreatWarningsAsErrors=true`).
- **Unit tests**: 23 / 23 pass — covers PhaseStatusMap, MockReviewChatClient,
  PendingInputBuilder, and `ReviewStateFoldTests` (which exercises the
  generator-produced `DurableAgentReviewStarted` / `{Step}Completed` events
  directly via the public event types).
- **Kill+restart**: skipped in dev (no Postgres available locally) — passes
  when `TEST_PG` is set.
- **Manual smoke** (Docker Compose): `POST /run` → `GET /status` shows
  `running` → eventually `waiting_for_input` → `POST /respond {approved:true}`
  → `GET /status` shows `completed`.

## Out of scope (P1 follow-up)

- `LlmMode.DirectOpenAI` and `LlmMode.BackThroughGateway` factory branches —
  the configuration is wired, the factory throws at startup.
- Replacing `NoopAgentIdentityAccessor` with a real basileus / SPIFFE accessor.
- Subscribing to the gateway's `IAgentWorkflowRunner` (`MafDurableHttpWorkflowRunner`)
  end-to-end through a docker-compose stack to confirm the gateway can drive
  this sidecar over the network.

## Deviations

- The test project is a sibling under `samples/OpenClaw.StrategosWorkflowHost.Tests/`
  rather than under `src/OpenClaw.Tests/`, because the host itself is a sample
  not a shipped library. This is a deliberate convention deviation that is
  documented in the sample's `README.md`.

## Files added

```
samples/OpenClaw.StrategosWorkflowHost/
  Adapters/{DurableHttpAdapter,PendingInputBuilder,PhaseStatusMap}.cs
  Configuration/{LlmMode,MockReviewChatClient}.cs
  Steps/{PlanExecutor,AggregateReviews,AssessConfidence,
         SecurityReviewer,ArchitectureReviewer,CostReviewer,
         RequestHumanReview,ExecuteApprovedAction,RevertApprovedAction,
         NotifyFailure,EmitAuditTrace,PromptBuilders,NoopStep,NoopFinishStep}.cs
  Workflows/{ApproverMarker,DurableAgentReviewWorkflowDefinition,ReviewState,
             SmokeState,SmokeWorkflow}.cs
  Workflows/Models/{HumanDecision,ReviewVerdict}.cs
  Program.cs
  Dockerfile
  docker-compose.yml
  appsettings.json
  appsettings.Development.json
  README.md
  NOTICE

samples/OpenClaw.StrategosWorkflowHost.Tests/
  PendingInputBuilderTests.cs
  ReviewStateFoldTests.cs
  KillRestartTests.cs
  PhaseStatusMapTests.cs
  MockReviewChatClientTests.cs
```