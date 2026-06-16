# Session Handling

This doc describes what an OpenClaw *session* is, how its lifecycle is managed, and what each session-related tool (`sessions_spawn`, `sessions_yield`, `sessions`) actually does. It is aimed at contributors and operators who see these tools during request processing and want to understand the model underneath.

## What a session is

A session is the unit of conversational state that the gateway routes messages to. It is defined in [src/OpenClaw.Core/Models/Session.cs](../src/OpenClaw.Core/Models/Session.cs):

- **Identity**: `Id`, `ChannelId`, `SenderId` (lines [22-24](../src/OpenClaw.Core/Models/Session.cs#L22-L24)).
- **Conversation**: `History` — an ordered `List<ChatTurn>` of `{ Role, Content, Timestamp, ToolCalls? }` ([line 27](../src/OpenClaw.Core/Models/Session.cs#L27), turn shape at [lines 137-143](../src/OpenClaw.Core/Models/Session.cs#L137-L143)).
- **Lifecycle state**: `SessionState` enum — `Active`, `Paused`, `Expired` ([line 28](../src/OpenClaw.Core/Models/Session.cs#L28), enum at [130-135](../src/OpenClaw.Core/Models/Session.cs#L130-L135)).
- **Timestamps**: `CreatedAt`, `LastActiveAt` ([lines 25-26](../src/OpenClaw.Core/Models/Session.cs#L25-L26)) — `LastActiveAt` is what drives expiry.
- **Per-session overrides**: model, reasoning effort, tool preset, system prompt, route-scoped allowlist, contract policy, delegation metadata. These let one session opt into different behavior than the gateway default without polluting other sessions.
- **Token counters**: `TotalInputTokens`, `TotalOutputTokens`, cache-read/write tokens — updated atomically via `Interlocked` ([lines 60-86](../src/OpenClaw.Core/Models/Session.cs#L60-L86)) so cost accounting is thread-safe.

The default key for a session is `channelId:senderId`, so "a given user on a given channel" maps to one session by default. Explicit session IDs are used for sub-agent sessions, cron jobs, webhooks, and anything else that needs a stable named session independent of a user.

## The owner: `SessionManager`

[src/OpenClaw.Core/Sessions/SessionManager.cs](../src/OpenClaw.Core/Sessions/SessionManager.cs) is the single owner of session state. It is an `IAsyncDisposable` singleton with:

- `ConcurrentDictionary<string, Session> _active` — the in-memory cache of live sessions ([line 15](../src/OpenClaw.Core/Sessions/SessionManager.cs#L15)).
- `IMemoryStore _store` — the persistent backing store (SQLite in the default setup).
- `_timeout` (from `GatewayConfig.SessionTimeoutMinutes`) — idle timeout before a session is swept.
- `_maxSessions` (from `GatewayConfig.MaxConcurrentSessions`) — hard cap on the in-memory active set.
- `_admissionGate` — a single-permit semaphore serializing admission so capacity accounting is race-free.

The manager's public surface that matters for the lifecycle:

| Method | Purpose | File ref |
| --- | --- | --- |
| `GetOrCreateAsync(channelId, senderId, ct)` | Default admission path. Key = `channelId:senderId`. | [line 45](../src/OpenClaw.Core/Sessions/SessionManager.cs#L45) |
| `GetOrCreateByIdAsync(sessionId, channelId, senderId, ct)` | Admission with an explicit ID. Used by `sessions_spawn`, cron, webhooks. | [line 55](../src/OpenClaw.Core/Sessions/SessionManager.cs#L55) |
| `TryGetActiveById(sessionId)` | Non-allocating active-cache lookup. Used by `sessions_yield` polling. | [line 273](../src/OpenClaw.Core/Sessions/SessionManager.cs#L273) |
| `LoadAsync(sessionId, ct)` | Active cache with store fallback. | [line 297](../src/OpenClaw.Core/Sessions/SessionManager.cs#L297) |
| `ListActiveAsync(ct)` | Snapshot of the active set (used by `sessions list`). | [line 255](../src/OpenClaw.Core/Sessions/SessionManager.cs#L255) |
| `PersistAsync(session, ct)` | Save to store with retry/backoff (3 attempts, exponential). | [line 132](../src/OpenClaw.Core/Sessions/SessionManager.cs#L132) |
| `RemoveActive(sessionId)` | Evict from the active cache; session remains in the store. | [line 309](../src/OpenClaw.Core/Sessions/SessionManager.cs#L309) |
| `SweepExpiredActiveSessions()` | Bulk eviction of sessions past `_timeout`. | [line 338](../src/OpenClaw.Core/Sessions/SessionManager.cs#L338) |
| `EnsureCapacityForAdmission()` | Called under `_admissionGate`; sweeps expired sessions, then evicts the stalest if still at `_maxSessions`. | [line 443](../src/OpenClaw.Core/Sessions/SessionManager.cs#L443) |

## Lifecycle, step by step

1. **Admission.** A new message, or `sessions_spawn`, calls `GetOrCreateByIdAsync`. Fast path: if the session is already in `_active`, bump `LastActiveAt` and return it ([lines 63-67](../src/OpenClaw.Core/Sessions/SessionManager.cs#L63-L67)). Slow path: acquire `_admissionGate`, re-check the cache, then try to rehydrate from the store (`_store.GetSessionAsync` at [line 81](../src/OpenClaw.Core/Sessions/SessionManager.cs#L81)). If nothing exists yet, a new `Session { State = Active }` is constructed at [lines 104-110](../src/OpenClaw.Core/Sessions/SessionManager.cs#L104-L110). Before insertion, `EnsureCapacityForAdmission()` runs — it sweeps expired sessions first and only evicts non-expired sessions (oldest-`LastActiveAt` wins) if the cap is still breached.

2. **Active work.** Every request that resolves a session touches `LastActiveAt`. Turns are appended to `History` as the agent produces them. Token counters are updated atomically via `AddTokenUsage` / `AddCacheUsage`. The manager takes a per-session `SemaphoreSlim` (`_sessionLocks`) when mutating state that must be serialized against persistence.

3. **Persistence.** `PersistAsync` writes the session through `IMemoryStore.SaveSessionAsync` under the per-session lock, with up to 3 attempts and exponential backoff on transient failures ([lines 144-165](../src/OpenClaw.Core/Sessions/SessionManager.cs#L144-L165)). Writes happen after turn completion and opportunistically in the background — there is no "commit the session" call from tools.

4. **Checkpointing during long turns.** During multi-step agent execution, the native runtime writes `Session.ExecutionCheckpoint` immediately after each completed tool batch. The checkpoint records the kind (`tool_batch`), resume state, sequence, iteration, history count, correlation id, timestamps, and per-tool metadata. The corresponding tool arguments/results remain in the persisted `History` tool-use turn, so a restarted runtime can rebuild the last completed batch without calling those tools again. When a later message arrives for a session whose latest checkpoint is `ready_to_resume` and whose history still ends at that checkpoint, the runtime resumes from that checkpoint instead of appending a new user turn. A bare `resume`, `continue`, `/resume`, or `/continue` message is treated as the resume trigger; any other text is passed as a resume note.

5. **Inter-session routing.** Nothing about a session is tied to a single thread or request. All session traffic flows through `MessagePipeline.InboundWriter` keyed by `SessionId`. That means every tool that wants to "talk to another session" is really just queuing an `InboundMessage` with the target session's ID — the runtime picks it up and runs that session's turn the same way a human message would.

6. **Expiry and eviction.** Two forces remove sessions from the active cache:
   - **Time.** `SweepExpiredActiveSessions` marks anything idle past `_timeout` as `Expired`, removes it from `_active`, and decrements `_activeCount`. Sweeping is triggered both on a background cadence and inside `EnsureCapacityForAdmission` before any forced eviction.
   - **Capacity.** If admission would push `_activeCount` over `_maxSessions` after sweeping, the session with the oldest `LastActiveAt` is evicted (`RemoveActive`).

   Eviction is **not deletion** — the session remains in the store. The next message for that ID simply takes the rehydrate path in step 1.

7. **Disposal.** On shutdown, the manager awaits any in-flight background persistence tasks and disposes per-session semaphores.

## The tools

### `sessions_spawn` — fire-and-forget

Gateway tool defined in [src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs](../src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs).

- **Args**: `prompt` (required), optional `session_id` and `channel_id`.
- **Effect**: creates or retrieves a session with the given ID via `GetOrCreateByIdAsync` ([line 43](../src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs#L43)), then writes the initial prompt to the inbound pipeline as a system message ([lines 45-54](../src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs#L45-L54)).
- **Returns**: immediately — the new session ID. The spawned agent processes asynchronously.

Use when the parent does not need the child's reply to continue its own turn.

### `sessions_yield` — synchronous rendezvous

Gateway tool defined in [src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs](../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs).

- **Args**: `session_id` (required), `message` (required), `timeout_seconds` (default 60, clamped 5–300).
- **Effect**: refuses self-yield (deadlock guard at [lines 43-44](../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs#L43-L44)), snapshots `target.History.Count`, queues the message through the pipeline ([line 72](../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs#L72)), then polls the target session for a new `assistant` turn past the snapshot. Poll delay starts at 500 ms and backs off to 2 s ([lines 80-85](../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs#L80-L85)). If the target is evicted mid-wait, the tool falls back to the store once ([lines 91-95](../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs#L91-L95)).
- **Returns**: the target's assistant reply, or a timeout message.

This is effectively a synchronous RPC between two sessions on top of the same async message pipeline.

### `sessions` — list / history / send

Agent tool defined in [src/OpenClaw.Agent/Tools/SessionsTool.cs](../src/OpenClaw.Agent/Tools/SessionsTool.cs).

- **`list`**: returns every active session with `Id`, `ChannelId`, `SenderId`, `State`. Backed by `ListActiveAsync`.
- **`history`**: returns the last N turns (`limit`, default 10 for the agent tool) of a named session, resolved via `LoadAsync` so evicted sessions still read correctly.
- **`send`**: queues an `InboundMessage` for the named session and returns immediately — identical plumbing to `sessions_spawn`, just targeting an existing session.

There is no yield-equivalent on this agent-facing tool; use the gateway-facing `sessions_yield` for synchronous waits.

### Registration

All three are grouped under the `group:sessions` tool preset in [src/OpenClaw.Gateway/ToolPresetResolver.cs](../src/OpenClaw.Gateway/ToolPresetResolver.cs) ([line 52](../src/OpenClaw.Gateway/ToolPresetResolver.cs#L52)). Operators can enable or restrict the whole cluster with one preset entry.

## Mental model

- **Sessions are state, not threads.** They are just rows keyed by ID with a conversation list and some overrides. Nothing in `SessionManager` owns an execution context.
- **Checkpoints are save points, not full runtime snapshots.** They are written after completed tool batches, which is the first durable point where OpenClaw can resume without duplicating already-run tools. They intentionally do not snapshot every internal token, stream, or provider transition.
- **The pipeline is the only way in.** `sessions_spawn`, `sessions_yield`, `sessions send` all produce the same `InboundMessage` shape that a user's channel message produces. Sub-agent sessions are not a separate runtime path.
- **Eviction is a cache decision.** Hitting the active cap or going idle past the timeout removes a session from memory, not from durable storage. The next message rehydrates it.
- **Spawn vs yield** is the async/sync axis: spawn returns immediately; yield polls for a reply with a bounded timeout.
- **Use `sessions list` / `sessions history`** when you want to introspect state without sending a message.

## Per-turn Token Accounting

OpenClaw tracks token usage at multiple levels during each turn. The same model output can update turn-local diagnostics, session totals, runtime counters, provider aggregates, and (when enabled) contract-governance cost tracking.

### How one turn is accounted

1. **Turn context is established.** `AgentRuntime` creates a `TurnContext` for correlation and per-turn observability before model/tool execution begins.
2. **Usage is ingested at turn accounting boundaries.** `AgentTurnAccounting` records usage for non-streaming and streaming paths, normalizing input/output/cache components when available.
3. **Fallback estimation is applied when needed.** If a provider does not return usage on a streaming response, runtime estimation can backfill token counts for accounting continuity.
4. **The same turn is written to multiple sinks.**
   - `Session` counters (`TotalInputTokens`, `TotalOutputTokens`, cache counters)
   - `RuntimeMetrics` global process counters
   - `ProviderUsageTracker` provider/model totals and recent-turn entries
   - Contract governance turn-cost tracking when contract mode is active
5. **Operator/user surfaces read from those sinks.** `/status`, `/usage`, metrics/admin endpoints, and OpenAI-compatible usage fields are all projections over these counters.

### Where to observe token usage

- **Turn level:** `TurnContext` summaries and per-turn logging.
- **Session cumulative:** `/status`, `/usage`, and session-bound summaries.
- **Runtime/provider counters:** `/metrics`, `/metrics/providers`, and provider snapshots.
- **Operator investigation views:** `/admin/providers` and `/admin/sessions/{id}/timeline`.
- **Compatibility responses:** OpenAI-compatible `usage` payloads returned by chat/responses endpoints.

### Per-session Task Token Ledger (Persistent)

OpenClaw now records each turn's token usage as an append-only audit stream, so "every session task's token consumption" can be traced beyond the in-memory recent-turn window.

- **Write model:** append-only JSONL, one line per turn.
- **Default file path:** `<Memory.StoragePath>/audit/turn-token-usage.jsonl`.
- **Record shape:** `TurnTokenUsageRecord` (`SessionId`, `ChannelId`, `ProviderId`, `ModelId`, input/output/cache tokens, `EstimatedInputTokensByComponent`, `IsEstimated`, `TimestampUtc`).
- **Execution path:** turn accounting emits `ITurnTokenUsageObserver` records; default gateway wiring uses a composite observer that writes to both `ProviderUsageTracker` (bounded recent-turn investigative view) and `TurnTokenUsageAuditLog` (persistent append-only ledger).

Operational notes:

- This ledger is the durable source for per-turn/session-task audits.
- Dashboard provider timeline remains a bounded recent-turn view for troubleshooting.
- `IsEstimated=true` indicates provider usage was missing and accounting relied on estimation.

### Viewing token usage in Dashboard

The visual entry point is the **Sessions** page in Dashboard (implemented in [src/OpenClaw.Dashboard/Pages/Sessions.razor](../src/OpenClaw.Dashboard/Pages/Sessions.razor)):

1. Open Sessions. The left session list shows a `Σ` total token badge per session (`input + output`).
2. Select a session. The right detail panel shows token summary cards:
   - `Input tokens`
   - `Output tokens`
   - `Cache read tokens`
   - `Cache write tokens`
   - `Total tokens`
3. In the same detail view, check **Provider token timeline** (backed by `/admin/sessions/{id}/timeline`) for per-turn rows:
   - timestamp
   - provider/model
   - input/output/cache/total tokens

Semantics notes:

- Summary cards are cumulative session counters (`Session.Total*Tokens`).
- Timeline rows come from a bounded recent-turn provider window, intended for investigation rather than long-term audit.
- If upstream usage is missing on some paths, some token values may be estimated.

### Current Semantics (Important)

- OpenAI-compatible `usage` fields are currently emitted from session cumulative counters, not per-request deltas.
- When upstream/provider usage is missing in some paths, token values may be estimated rather than provider-billed exact values.
- Provider recent-turn usage is a bounded in-memory window, not a long-term audit ledger.
- Per-turn persistent token audit is available via JSONL ledger (`turn-token-usage.jsonl`).
- `/status` and `/usage` reflect cumulative session counters, not just the most recent turn.

### Implementation anchors

- Turn accounting entry points: [src/OpenClaw.Agent/Runtime/AgentTurnAccounting.cs](../src/OpenClaw.Agent/Runtime/AgentTurnAccounting.cs)
- Session token/cache counters: [src/OpenClaw.Core/Models/Session.cs](../src/OpenClaw.Core/Models/Session.cs)
- Turn context observability: [src/OpenClaw.Core/Observability/TurnContext.cs](../src/OpenClaw.Core/Observability/TurnContext.cs)
- Provider aggregates and recent turns: [src/OpenClaw.Core/Observability/ProviderUsageTracker.cs](../src/OpenClaw.Core/Observability/ProviderUsageTracker.cs)
- Turn token observer contract: [src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs](../src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs)
- Turn token record model: [src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs](../src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs)
- Persistent turn token ledger: [src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs](../src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs)
- Runtime totals: [src/OpenClaw.Core/Observability/RuntimeMetrics.cs](../src/OpenClaw.Core/Observability/RuntimeMetrics.cs)
- Session command projections: [src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs](../src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs)
- Metrics/admin endpoints: [src/OpenClaw.Gateway/Endpoints/DiagnosticsEndpoints.cs](../src/OpenClaw.Gateway/Endpoints/DiagnosticsEndpoints.cs), [src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Runtime.cs](../src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Runtime.cs), [src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Sessions.cs](../src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Sessions.cs)
- Gateway observer wiring: [src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs](../src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs), [src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs](../src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs)
- OpenAI-compatible usage serialization: [src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.ChatCompletions.cs](../src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.ChatCompletions.cs), [src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.Responses.cs](../src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.Responses.cs)

## Related

- [TOOLS_GUIDE.md](TOOLS_GUIDE.md) — the broader native tool catalog and how presets compose.
- [USER_GUIDE.md](USER_GUIDE.md) — operator-facing view of channels, providers, and sessions.
- [GLOSSARY.md](GLOSSARY.md) — definitions of *gateway*, *runtime*, *channel*, *profile*, etc.
- [PROMPT_CACHING.md](PROMPT_CACHING.md) — cache-read/cache-write usage semantics and provider-aware cache behavior.
