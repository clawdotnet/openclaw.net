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
|---|---|---|
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

4. **Inter-session routing.** Nothing about a session is tied to a single thread or request. All session traffic flows through `MessagePipeline.InboundWriter` keyed by `SessionId`. That means every tool that wants to "talk to another session" is really just queuing an `InboundMessage` with the target session's ID — the runtime picks it up and runs that session's turn the same way a human message would.

5. **Expiry and eviction.** Two forces remove sessions from the active cache:
   - **Time.** `SweepExpiredActiveSessions` marks anything idle past `_timeout` as `Expired`, removes it from `_active`, and decrements `_activeCount`. Sweeping is triggered both on a background cadence and inside `EnsureCapacityForAdmission` before any forced eviction.
   - **Capacity.** If admission would push `_activeCount` over `_maxSessions` after sweeping, the session with the oldest `LastActiveAt` is evicted (`RemoveActive`).

   Eviction is **not deletion** — the session remains in the store. The next message for that ID simply takes the rehydrate path in step 1.

6. **Disposal.** On shutdown, the manager awaits any in-flight background persistence tasks and disposes per-session semaphores.

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
- **The pipeline is the only way in.** `sessions_spawn`, `sessions_yield`, `sessions send` all produce the same `InboundMessage` shape that a user's channel message produces. Sub-agent sessions are not a separate runtime path.
- **Eviction is a cache decision.** Hitting the active cap or going idle past the timeout removes a session from memory, not from durable storage. The next message rehydrates it.
- **Spawn vs yield** is the async/sync axis: spawn returns immediately; yield polls for a reply with a bounded timeout.
- **Use `sessions list` / `sessions history`** when you want to introspect state without sending a message.

## Related

- [TOOLS_GUIDE.md](TOOLS_GUIDE.md) — the broader native tool catalog and how presets compose.
- [USER_GUIDE.md](USER_GUIDE.md) — operator-facing view of channels, providers, and sessions.
- [GLOSSARY.md](GLOSSARY.md) — definitions of *gateway*, *runtime*, *channel*, *profile*, etc.
