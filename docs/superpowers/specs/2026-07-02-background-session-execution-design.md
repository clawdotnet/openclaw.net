# Background Session Execution Design

Date: 2026-07-02
Status: Implemented

## Summary

OpenClaw.NET should treat Channel-activated work as durable session work, not as work owned by a browser tab, WebSocket connection, or transient request. When a user starts a task from any Channel, including WebChat over WebSocket, the task must continue while the Gateway process and sandbox persist. Closing the browser, returning to the workbench, disconnecting WebSocket, or leaving the Channel client must not stop the session. Work stops only when the goal completes, becomes blocked, exhausts its budget, is explicitly paused/stopped, the Gateway is configured not to recover it after restart, or the instance sandbox is deleted/recreated.

The recommended design is a process-local background session execution loop built on the existing `MessagePipeline`, `SessionManager`, Goal integration, and checkpoint mechanism. The runtime executes work in bounded batches. When a batch ends but the active Goal still requires progress, the Gateway writes a low-priority internal continuation message back into the pipeline. Startup recovery scans persisted sessions with runnable background state and re-enqueues them with staggered concurrency.

The behavior must be identical for both configured runtime orchestrators: the native `AgentRuntime` and the Microsoft Agent Framework adapter `MafAgentRuntime`. Background execution is an `IAgentRuntime` contract requirement, not a native-runtime-only feature.

## Goals

### P0: Channel-consistent background execution

All Channel-originated sessions must share the same lifecycle semantics:

- WebChat over WebSocket is a Channel, not a special UI-owned session.
- Telegram, WhatsApp, Teams, Slack, Discord, Signal, SMS, email, webhooks, cron, spawned sessions, and WebChat must route through the same durable session model.
- A disconnected UI or offline Channel client means only that the observer is gone; the session and Goal continue in the Gateway.
- `InboundMessage.RequestCancellation` and WebSocket connection cancellation must not be treated as session lifetime cancellation for background-capable work.
- Failed outbound delivery to an offline WebChat socket or Channel client must not change task state; results remain available through persisted history and status APIs.

### P0: Self-driving Goal sessions

When a session has an active Goal and the current execution batch stops before completion, the Gateway must continue the session without waiting for another user message.

The continuation mechanism must:

- reuse `MessagePipeline.InboundWriter` rather than introduce a parallel execution path;
- mark continuation messages as internal/system messages with a distinct type such as `background_auto_continue`;
- preserve session history and checkpoint semantics;
- continue only for sessions whose run state and Goal state are runnable;
- stop on completion, blocker, budget limit, explicit pause/stop, or unrecoverable error.

### P0: Native and MAF runtime parity

The background execution contract must hold for both `Runtime:Orchestrator=native` and `Runtime:Orchestrator=maf`.

Requirements:

- `AgentRuntime` and `MafAgentRuntime` must expose equivalent result metadata through the shared `IAgentRuntime` contract.
- Both runtimes must honor Goal continuation, batch limits, checkpoint/resume semantics, tool approval, contract governance, and budget stop reasons.
- The Gateway must not silently downgrade MAF sessions to message-driven behavior.
- If a configured runtime cannot support the background contract, startup or runtime selection must fail fast with explicit diagnostics.
- Tests must run the same background continuation scenarios against native and MAF where the MAF adapter is available in the test host.

### P0: Restart recovery

Gateway startup should recover background work by default:

- scan the persistent session store for runnable sessions;
- select sessions with `RunState` in `Running` or `Continuing` and an active Goal;
- skip paused, blocked, budget-limited, completed, failed, and over-budget sessions;
- re-enqueue runnable sessions with staggered timing and concurrency limits;
- resume from the latest safe checkpoint when present.

### P0: Safety and fairness

Background execution must not starve foreground user messages or bypass governance:

- each batch has a maximum iteration count;
- background turns have a separate concurrency limit;
- user messages, approvals, stops, and resumes take priority over auto-continue work;
- tool approval and governance continue to apply;
- budgets cap token use, wall-clock duration, tool calls, and continuation count;
- state must be persisted before a later continuation depends on it.

### P1: User-visible state and notifications

Users should be able to leave and return without losing context:

- Channel completion, blocked, and budget notifications are sent through the original Channel when possible;
- WebChat reconnect loads persisted history and current run state;
- Dashboard/admin surfaces show background sessions, status, checkpoint, budget, and last activity;
- notification delivery failure is logged but does not stop work.

## Non-Goals

- Do not implement a token-burning idle daemon that runs when no Goal or background run is active.
- Do not replace `/loop`; periodic prompt injection remains a separate feature.
- Do not make Channel adapters own task lifecycle.
- Do not introduce a mandatory external workflow backend for the core path.
- Do not bypass tool approval, allowlists, sandboxing, contract governance, or public-bind hardening.
- Do not make background execution a native-only behavior while `MafAgentRuntime` remains request-bound.
- Do not claim cross-sandbox recovery. If the instance sandbox and persistent store are deleted, previous work is gone.

## Key Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Trigger scope | All Channel sessions are eligible once they have runnable Goal/background state | Matches the expected behavior that WebChat and external Channels behave identically |
| Completion model | Goal semantic judgment plus safety fallback | Lets tasks stop naturally while avoiding unbounded execution |
| Continuation path | Self-requeue through `MessagePipeline` | Reuses existing routing, backpressure, session locks, and observability |
| Runtime contract | Implemented by shared `IAgentRuntime` for native and MAF | Prevents orchestrator-specific lifecycle differences |
| Restart behavior | Configurable, default auto-resume | Meets durability expectations while preserving operator control |
| Notifications | Channel push plus persisted status/history | Users can be notified when possible and can still inspect state later |
| WebChat lifecycle | WebSocket connection is not task lifetime | Closing a browser must not cancel work |

## Architecture

### Components

| Component | Existing/New | Responsibility |
|---|---|---|
| `Session` | Existing, extended | Stores run state, background metadata, checkpoint, history, channel identity |
| `SessionManager` | Existing, extended | Loads, persists, admits, evicts, and lists runnable background sessions |
| `AgentRuntime` / `MafAgentRuntime` | Existing, adapted | Execute bounded batches and report equivalent continuation/stop metadata through `IAgentRuntime` |
| `GatewayInboundMessageWorker` | Existing, adapted | Runs turns, persists state, emits notifications, and re-enqueues continuation messages |
| `MessagePipeline` | Existing | Single ingress path for user messages, loop prompts, session sends, and background continuation |
| `BackgroundSessionRecoveryWorker` | New | Scans persisted sessions on startup and re-enqueues runnable background sessions |
| `BackgroundExecutionLimiter` | New or Gateway service | Enforces max concurrent background turns and requeue delay |
| Channel adapters | Existing | Input/output only; never own background task lifecycle |

### Core flow

```plaintext
User / Channel / WebChat
    │
    ▼
InboundMessage
    │
    ▼
MessagePipeline
    │
    ▼
GatewayInboundMessageWorker
    │
    ▼
Configured IAgentRuntime bounded batch
(AgentRuntime or MafAgentRuntime)
    │
    ├─ Goal complete / blocked / budget limited / failed → persist + notify + stop
    │
    └─ Goal still active → persist Continuing + enqueue background_auto_continue
                                      │
                                      ▼
                              MessagePipeline
```

The self-requeue message is not a user message. It carries the target `SessionId`, the current background `RunId`, a monotonic continuation sequence, and `IsSystem = true`. The worker validates the message against persisted session state before running it.

## Runtime Result Model

The current `IAgentRuntime.RunAsync` returns text. The background design needs the caller to know why execution stopped. To avoid a broad breaking change, introduce a result-bearing API while preserving the current method for compatibility.

This API belongs on the shared `IAgentRuntime` surface or an adjacent shared capability interface implemented by both runtime orchestrators. The Gateway must use the same result-bearing path regardless of whether the configured runtime is native or MAF.

Recommended shape:

```csharp
public sealed record AgentTurnResult
{
    public required string Text { get; init; }
    public bool ShouldContinue { get; init; }
    public AgentTurnStopReason StopReason { get; init; }
    public string? ContinuePrompt { get; init; }
}

public enum AgentTurnStopReason
{
    Completed,
    GoalContinuationRequired,
    BatchLimitReached,
    Blocked,
    BudgetLimited,
    Failed
}
```

`RunAsync` can remain as a compatibility wrapper over the result-bearing method, returning `AgentTurnResult.Text`. Gateway background execution uses the richer method.

`RunStreamingAsync` should either expose equivalent terminal metadata or delegate its finalization path to the same internal result builder. WebChat streaming must not be the only place where continuation state is computed; otherwise WebSocket disconnects could produce different behavior than non-streaming Channel turns.

`MafAgentRuntime` may keep MAF-specific internal state handling, but it must translate MAF stop, tool, approval, and Goal outcomes into the same `AgentTurnResult` values as native `AgentRuntime`.

## Session State Model

Add background execution state to `Session` because it is tightly coupled to history, checkpointing, Goal state, and Channel identity.

```csharp
public enum SessionRunState
{
    Idle,
    Running,
    Continuing,
    Paused,
    Blocked,
    BudgetLimited,
    Completed,
    Failed
}

public sealed class BackgroundRunMetadata
{
    public string RunId { get; set; } = string.Empty;
    public string? Objective { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastContinuedAtUtc { get; set; }
    public DateTimeOffset? LastNotificationAtUtc { get; set; }
    public int ContinuationCount { get; set; }
    public int ConsecutiveNoProgressCount { get; set; }
    public long ToolCallCount { get; set; }
    public long TokenBudget { get; set; }
    public int MaxContinuationTurns { get; set; }
    public string? LastCheckpointId { get; set; }
    public string? LastStopReason { get; set; }
}
```

Legacy sessions naturally deserialize as `Idle` with no background metadata and are not recovered.

## Lifecycle

```plaintext
Idle
  → Running        user starts/activates a Goal
Running
  → Continuing    batch ended, Goal still active
Continuing
  → Running       background_auto_continue accepted
Running
  → Completed     Goal complete
  → Blocked       user input, approval, or no-progress blocker required
  → BudgetLimited token/time/tool/continuation budget exhausted
  → Paused        user/operator pause
  → Failed        unrecoverable runtime or persistence failure
Blocked/BudgetLimited/Paused/Failed
  → Running       user/operator resume when allowed
Completed
  → Idle          run archived, session remains inspectable
```

The lifecycle is controlled by Gateway session state, not by UI connection state.

## WebChat and WebSocket Semantics

WebChat must use the same semantics as other Channels.

Required behavior:

- WebChat inbound messages create or load the same durable `Session` shape as other Channel messages.
- WebSocket disconnect cancels only socket receive/send operations, not the background session.
- Background turns use an application/Gateway cancellation token, not a browser request token.
- If the WebSocket is offline when an outbound response is produced, the send failure is logged and the assistant turn remains in persisted history.
- On reconnect, WebChat resolves the intended session, loads history and run state, and resumes observing live outbound updates.
- WebChat must not create a blank replacement session unless the user explicitly starts a new session.

This is a core requirement, because the user-visible bug is that work appears to disappear after returning to the workbench.

## Persistence Rules

Persist before depending on state:

- background run start;
- transition to `Running`, `Continuing`, `Paused`, `Blocked`, `BudgetLimited`, `Completed`, or `Failed`;
- checkpoint write after tool batch completion;
- continuation sequence increment;
- notification timestamp update;
- budget and tool-call counter updates that affect stop decisions.

If a required state update cannot be persisted, the Gateway must not enqueue the next background continuation. It should mark the run failed when possible and log the persistence failure.

## Store Query

Startup recovery requires persistent lookup, not active-cache lookup. Add a narrow store API such as:

```csharp
Task<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(
    int limit,
    CancellationToken ct);
```

The store implementation should return sessions whose persisted state is runnable. SQLite can initially scan stored session JSON if that matches existing storage conventions, but a later optimization may project `RunState`, `GoalState`, and `LastContinuedAtUtc` into indexed columns.

## Configuration

Recommended config section:

```csharp
public sealed class BackgroundExecutionConfig
{
    public bool Enabled { get; set; } = true;
    public bool AutoResumeOnStartup { get; set; } = true;
    public int AutoResumeStaggerSeconds { get; set; } = 5;
    public int AutoResumeMaxConcurrent { get; set; } = 3;
    public int MaxConcurrentBackgroundTurns { get; set; } = 3;
    public int MaxIterationsPerBatch { get; set; } = 10;
    public long DefaultTokenBudget { get; set; } = 128_000;
    public int MaxWallClockMinutes { get; set; } = 360;
    public int MaxToolCalls { get; set; } = 1_000;
    public int MaxContinuationTurns { get; set; } = 200;
    public int ProgressNotifyIntervalMinutes { get; set; } = 10;
    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool NotifyOnBlocked { get; set; } = true;
    public bool NotifyOnBudgetLimited { get; set; } = true;
}
```

Preferred ownership is `GatewayConfig.Sessions.BackgroundExecution` if a sessions sub-configuration exists. If the current config shape makes that disruptive, the first implementation may use `GatewayConfig.BackgroundExecution` and later move it with compatibility binding.

## Fairness and Concurrency

Background work is cooperative:

- `MaxIterationsPerBatch` bounds each runtime slice.
- `MaxConcurrentBackgroundTurns` limits concurrent background turns.
- user messages, approval decisions, pause, resume, and stop commands have priority over auto-continuations.
- auto-continuation messages validate `RunId` and continuation sequence before running.
- stale or duplicate continuation messages are dropped or delayed rather than running a second turn for the same session.

The MVP can keep one bounded `MessagePipeline` and enforce background permits in `GatewayInboundMessageWorker`. A future high-load implementation may split the pipeline into priority lanes.

## Notifications and User Visibility

Send Channel notifications for lifecycle events when possible:

- started: background task accepted and can continue while the user is away;
- resumed after restart: service recovered and work continues;
- progress: rate-limited updates, default every 10 minutes;
- completed: final summary;
- blocked: user action needed;
- budget-limited: resume or increase budget needed;
- failed: retry or inspect logs needed.

Persisted state and history are authoritative. Notification delivery is best-effort.

Minimum admin/session status fields:

```json
{
  "sessionId": "websocket:user-123",
  "channelId": "websocket",
  "senderId": "user-123",
  "runState": "Running",
  "goalState": "Active",
  "goalSummary": "Fix failing tests",
  "startedAt": "2026-07-02T10:00:00Z",
  "lastContinuedAt": "2026-07-02T10:18:00Z",
  "continuationCount": 12,
  "tokenUsed": 45231,
  "tokenBudget": 128000,
  "lastCheckpointId": "ckpt_..."
}
```

## Error Handling

| Scenario | Behavior |
|---|---|
| LLM transient failure | Retry through existing runtime policy, then requeue with backoff or mark `Failed` after limit |
| Tool transient failure | Existing tool/runtime handling first; repeated no-progress failures become `Blocked` |
| Tool approval required | Enter `Blocked`, send approval notification, do not auto-continue until approved/resumed |
| Budget exceeded | Enter `BudgetLimited`, persist, notify, stop auto-continuation |
| Persistence failure | Do not enqueue next continuation; mark/log `Failed` when possible |
| Channel/WebSocket outbound failure | Log only; persisted history remains authoritative |
| Gateway shutdown | Respect application cancellation for current batch; rely on checkpoint and startup recovery |
| Duplicate continuation message | Validate run id/sequence and drop stale duplicates |
| Same session already running | Delay or drop stale auto-continuation; never execute concurrent turns for one session |

## NativeAOT and JIT Implications

This design remains NativeAOT-friendly:

- no reflection-based scheduler;
- no dynamic proxies;
- no required external workflow runtime;
- new records and enums are added to existing System.Text.Json source-generation contexts;
- store queries use explicit APIs and concrete model types;
- Channel adapters remain optional integrations.

The design does not add JIT-only requirements to the native runtime path. `MafAgentRuntime` is not allowed to have different user-visible background lifecycle behavior when selected as the orchestrator. If a specific MAF deployment lane has additional hosting or AOT limitations, those limitations must be explicit diagnostics, not silent fallback to non-background execution.

## Testing

### Unit tests

- `SessionRunState` and `BackgroundRunMetadata` JSON roundtrip.
- Old session JSON deserializes as `Idle` with no background metadata.
- Active Goal plus incomplete result yields `ShouldContinue = true`.
- Goal complete yields `ShouldContinue = false` and `Completed`.
- Budget and continuation limits yield `BudgetLimited`.
- Paused, blocked, failed, and completed sessions do not continue.
- Duplicate or stale continuation sequence is rejected.

### Gateway integration tests

- User message starts a Goal-backed background run.
- Batch end writes a `background_auto_continue` message.
- Pipeline processes continuation without a user message.
- WebSocket disconnect does not cancel the background run.
- Offline outbound delivery failure does not stop the run.
- Reconnect loads history and current run state.
- Completion writes assistant history and sends best-effort notification.
- The same continuation scenario passes with `Runtime:Orchestrator=native` and `Runtime:Orchestrator=maf`.

### Startup recovery tests

- `Running` or `Continuing` plus active Goal is re-enqueued.
- `Paused`, `Blocked`, `BudgetLimited`, `Completed`, and `Failed` are not re-enqueued.
- Recovery honors stagger and max concurrent settings.
- Recovery resumes from checkpoint without re-running completed tool batches.
- Missing persistent store after sandbox deletion produces no recovery.

### Regression tests

- ordinary single-turn chat still returns normally;
- `/loop` still schedules periodic prompts independently;
- `sessions_spawn`, `sessions_yield`, and `sessions send` continue to route through the pipeline;
- tool approval still blocks unsafe work;
- native and MAF runtimes produce equivalent stop reasons for Goal complete, continue, blocked, budget-limited, and failed outcomes;
- session eviction remains cache eviction, not deletion;
- NativeAOT JSON source-generation coverage includes new models.

## Acceptance Criteria

1. A WebChat user can start a Goal task, close the browser, return later, and see continued history and final/background state.
2. WebChat WebSocket disconnect does not cancel the running background task.
3. Telegram or any other Channel behaves the same as WebChat for background execution.
4. Gateway restart auto-recovers runnable sessions by default from persisted state and checkpoint.
5. Background work stops on completion, blocker, budget limit, explicit pause/stop, or missing sandbox persistence.
6. Foreground messages and approvals remain responsive while background work is running.
7. No background task bypasses existing tool approval, allowlist, sandbox, or governance controls.
8. The same background lifecycle works under both `Runtime:Orchestrator=native` and `Runtime:Orchestrator=maf`, or the unsupported orchestrator path fails fast with explicit diagnostics.

## Implementation Phases

### Phase 1: Core state and result plumbing

- Add session run state and background metadata.
- Add result-bearing runtime API while preserving current text-returning compatibility method.
- Implement the result-bearing contract in both `AgentRuntime` and `MafAgentRuntime`.
- Add config model and JSON source-generation coverage.
- Add store query for runnable background sessions.

### Phase 2: Gateway continuation loop

- Teach `GatewayInboundMessageWorker` to interpret turn results.
- Enqueue validated `background_auto_continue` messages.
- Enforce background concurrency and stale-message checks.
- Persist state before continuation.

### Phase 3: WebChat and Channel parity

- Ensure WebSocket disconnect does not propagate as session cancellation.
- Make reconnect load persisted session history and status.
- Make outbound failures non-fatal to task state.
- Add Channel parity tests.

### Phase 4: Startup recovery and notifications

- Add recovery worker.
- Add staggered auto-resume.
- Add lifecycle notifications and rate-limited progress updates.
- Add admin/session status surface.
