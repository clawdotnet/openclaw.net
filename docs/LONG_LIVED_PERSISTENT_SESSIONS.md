# OpenClaw.NET Long-Lived Persistent Sessions

> [中文](zh-CN/LONG_LIVED_PERSISTENT_SESSIONS.md) | English
>
> Technical deep-dive · Based on source code analysis · 2026-07-02

---

## 1. Overview

OpenClaw.NET's session system is not a simple "chat history store." It is a **complete lifecycle management mechanism for long-running AI Agents**, supporting:

- Multi-turn conversation persistence across process restarts
- Background agent task continuation across bounded batches
- Mid-execution checkpointing with precise resume
- Startup self-healing after gateway crashes
- Automatic history compaction and token audit trails
- Synchronous/asynchronous inter-session communication and agent delegation

**Core design philosophy: Sessions are state, not threads.** They are keyed rows with a conversation list and configuration overrides. `SessionManager` owns no execution context.

---

## 2. Core Session Model

Defined in `Session` class (`src/OpenClaw.Core/Models/Session.cs`):

### 2.1 Identity & Routing

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Unique session key, default `channelId:senderId` ("one user on one channel") |
| `ChannelId` | `string` | Channel identifier (e.g. `webchat`, `whatsapp`, `teams`) |
| `SenderId` | `string` | Sender identifier |
| `StableSessionBinding` | `StableSessionBindingInfo?` | External stable binding for cross-platform correlation |

### 2.2 Conversation History

```csharp
List<ChatTurn> History   // { Role, Content, Timestamp, ToolCalls? }
```

Each `ChatTurn` carries role (`user`/`assistant`/`system`), content, timestamp, and optional `ToolInvocation` list (tool name, arguments, result, duration, failure codes, etc.).

### 2.3 Lifecycle State

Dual state machines:

```
SessionState (session-level):   Active ──► Paused ──► Expired

SessionRunState (run-level):    Idle ──► Running ──► Continuing
                                     │         │
                                     ▼         ▼
                                Paused    Blocked
                                BudgetLimited
                                Completed / Failed
```

### 2.4 Timestamps

| Field | Purpose |
|-------|---------|
| `CreatedAt` | Session creation time |
| `LastActiveAt` | Last activity time — drives expiry sweep and capacity eviction |

### 2.5 Token Accounting (Lock-Free)

All counters use `Interlocked` atomic operations:

```csharp
long TotalInputTokens       // Interlocked.Read / Interlocked.Add
long TotalOutputTokens
long TotalCacheReadTokens
long TotalCacheWriteTokens
```

### 2.6 Per-Session Overrides

A session can independently override gateway defaults for:

- `ModelOverride` — model selection (`/model` command)
- `ModelProfileId` — named model profile
- `ReasoningEffort` — extended thinking level (`/think` command)
- `RoutePresetId` — tool preset
- `RouteAllowedTools` — tool allowlist
- `SystemPromptOverride` — system prompt
- `ContractPolicy` — contract-governed execution limits
- `Delegation` / `DelegatedSessions` — child session metadata

---

## 3. Dual-Layer Persistence Architecture

```
┌──────────────────────────────────────────────────┐
│                 SessionManager                    │
│                                                  │
│  ┌────────────────┐        ┌──────────────────┐  │
│  │   _active       │        │   IMemoryStore    │  │
│  │  (Concurrent    │ ◄──►   │  (SQLite default) │  │
│  │   Dictionary)   │        │                   │  │
│  │                 │        │  Supports:         │  │
│  │  In-memory hot  │        │  - SQLite (default)│  │
│  │  cache          │        │  - MemPalace (JIT) │  │
│  └────────────────┘        └──────────────────┘  │
│                                                  │
│  Capacity controls:                               │
│  - _maxSessions: in-memory ceiling               │
│  - _timeout: idle expiry (SessionTimeoutMinutes) │
│  - _admissionGate: single-permit semaphore        │
└──────────────────────────────────────────────────┘
```

### 3.1 Fast Path (Cache Hit)

```
Incoming message → _active.TryGetValue(key)
  ├─ Hit → bump LastActiveAt → return immediately (lock-free)
  └─ Miss → acquire _admissionGate → enter slow path
```

### 3.2 Slow Path (Store Rehydration)

```
Acquire _admissionGate semaphore
  ├─ Double-check _active (double-checked locking)
  ├─ _store.GetSessionAsync(key)  ← rehydrate from SQLite
  ├─ EnsureCapacityForAdmission()
  │   ├─ SweepExpiredActiveSessions()  sweep expired first
  │   └─ If still over capacity → evict oldest LastActiveAt
  └─ _active.TryAdd(key, session) → return
```

### 3.3 Persistence Writes

```csharp
PersistAsync(session, ct):
  Acquire per-session SemaphoreSlim → serialize writes
  _store.SaveSessionAsync → 3 retries, exponential backoff
```

### 3.4 Eviction ≠ Deletion

Sessions evicted from `_active` **remain in IMemoryStore**. The next message rehydrates them automatically — this is the foundation of long-lived persistence.

---

## 4. Session Lifecycle

```
  ┌──────────┐
  │ Message   │
  │ Arrives   │
  └────┬─────┘
       ▼
  ┌──────────────┐
  │ 1. Admission  │  GetOrCreateByIdAsync → cache hit / rehydrate / create
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 2. Active     │  Touch LastActiveAt on every request
  │    Work       │  AgentRuntime appends ChatTurn to History
  │              │  Atomic token/cache counter updates
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 3. Checkpoint │  Write ExecutionCheckpoint after each tool batch
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 4. Persist    │  PersistAsync → IMemoryStore.SaveSessionAsync
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 5. Expiry /   │  Timeout → SweepExpiredActiveSessions
  │    Eviction   │  Over-capacity → RemoveActive (oldest-first)
  └──────────────┘
         │
  Shutdown → await all in-flight persists → dispose semaphores
```

---

## 5. Execution Checkpoints & Interruption Recovery

This is the **most critical** mechanism for long-lived sessions. The runtime writes a checkpoint immediately after every completed tool batch, ensuring tools are never re-executed on resume.

### 5.1 Checkpoint Model

```csharp
SessionExecutionCheckpoint {
    CheckpointId:      Guid unique identifier
    Kind:              "tool_batch"          // checkpoint type
    State:             "ready_to_resume"     // | "completed" | "failed"
    Sequence:          Monotonic sequence number
    Iteration:         Current iteration index
    HistoryCount:      History turn count at write time
    CorrelationId:     Trace correlation ID
    CreatedAtUtc:      Creation timestamp
    PersistedAtUtc:    Persistence timestamp
    LastResumeAttemptAtUtc:  Last resume attempt
    CompletedAtUtc:    Completion timestamp
    CompletionReason:  Why completed
    ToolCalls: [
        { CallId, ToolName, ResultStatus, FailureCode, DurationMs,
          ArgumentsBytes, ResultBytes }
    ]
}
```

### 5.2 Write Timing

```
AgentRuntime execution loop:
  LLM returns tool calls → execute tools → append assistant[tool_use] to History
  → PersistToolBatchCheckpointAsync()  ← checkpoint written here
  → Continue to next iteration
```

Checkpoints record only **completed tool batch boundaries** — the first durable point where resume is safe without duplicating tool execution.

### 5.3 Resume Logic

```
New message → RunTurnAsync():
  TryGetResumableCheckpoint(session)
    ├─ null → normal path: append user turn → compact/trim → build messages
    └─ checkpoint present, State=ready_to_resume:
         ├─ Don't append new user turn
         ├─ BuildMessages(exactLatestToolBatch: true)
         │   Precisely reconstruct last tool batch's Assistant+Tool message pair
         ├─ Insert resume system instruction:
         │   "The previous execution was interrupted after completing
         │    a tool batch. Resume from here. Do NOT re-execute the
         │    already-completed tools listed above."
         └─ If message is not bare resume/continue:
              Append user note as additional user message
```

**Bare resume triggers**: `resume`, `continue`, `/resume`, `/continue`

### 5.4 Resume Safety

- Tool arguments and results are preserved in `History.ToolInvocation` within persistent storage
- Resume reconstructs exact `FunctionCallContent` + `FunctionResultContent` message pairs from checkpoint metadata
- Checkpoint `CallId` ensures precise matching between function calls and results in `ChatMessage` lists

---

## 6. Background Continuation

When a single turn reaches the iteration limit, the session **does not terminate** — it automatically continues in bounded background batches.

### 6.1 Trigger

```
RunTurnAsync hits _maxIterations (default 20)
  → Returns AgentTurnResult {
      ShouldContinue: true,
      StopReason: BatchLimitReached,
      ContinuePrompt: "Continue working toward the active goal."
    }
```

### 6.2 Background Initialization & Continuation

```csharp
// Gateway lazily initializes on first continuation:
BackgroundRun = new BackgroundRunMetadata {
    RunId, Objective, StartedAtUtc, LastContinuedAtUtc,
    ContinuationCount, ContinuationSequence,
    ConsecutiveNoProgressCount,  // stall detection
    TokenBudget, MaxContinuationTurns,
    LastCheckpointId, LastStopReason
};

// Enqueue internal continuation message:
pipeline.WriteAsync(new InboundMessage {
    Type = "background_auto_continue",
    IsSystem = true
});
```

### 6.3 Key Characteristics

| Feature | Description |
|---------|-------------|
| **Independent concurrency** | `MaxConcurrentBackgroundTurns` (default 3), isolated from user requests |
| **WebSocket disconnect safe** | Channel client exit does NOT cancel background work |
| **Goal-driven continuation** | If active Goal exists, continuation prompt includes goal-specific instructions |
| **Stall detection** | `ConsecutiveNoProgressCount` tracks consecutive zero-progress turns |
| **Budget control** | `TokenBudget` + `MaxContinuationTurns` prevent unbounded consumption |

### 6.4 Goal Auto-Continuation

```
AgentRuntime loop:
  LLM returns text (no tool calls)
    → GoalIntegration.EvaluateGoalContinuation()
      ├─ Goal not done → insert goal_check system message → continue loop
      └─ Goal complete → return normally
```

---

## 7. Startup Recovery — Self-Healing

`BackgroundSessionRecoveryWorker` executes on gateway startup:

```
Gateway starts
  → BackgroundSessionRecoveryWorker.RecoverOnceAsync()
    ├─ Check BackgroundExecution.Enabled && AutoResumeOnStartup
    ├─ Query IMemoryStore:
    │   ListBackgroundRunnableSessionsAsync(limit)
    │   WHERE RunState IN (Running, Continuing) AND Goal IS ACTIVE
    └─ Enqueue each:
        pipeline.WriteAsync(new InboundMessage {
            Type = "background_auto_resume",
            Text = "Resume the active background goal from the latest checkpoint.",
            IsSystem = true
        });
        + Optional AutoResumeStaggerSeconds stagger
```

### Configuration

```yaml
OpenClaw:
  BackgroundExecution:
    Enabled: true
    AutoResumeOnStartup: true
    AutoResumeStaggerSeconds: 5
    AutoResumeMaxConcurrent: 3
```

---

## 8. Inter-Session Communication & Child Sessions

All inter-session traffic flows through a single `MessagePipeline.InboundWriter`. Child sessions, background continuations, and startup recovery all use the same code path as user messages.

### 8.1 Tool Matrix

| Tool | Mode | Timeout | Description |
|------|------|---------|-------------|
| `sessions_spawn` | Async (fire-and-forget) | — | Create child session, return session ID immediately |
| `sessions_yield` | Sync (rendezvous) | 5-300s (default 60s) | Send message, poll for reply |
| `sessions send` | Async | — | Send to existing session, return immediately |
| `sessions list` | Query | — | List all active sessions |
| `sessions history` | Query | — | Read last N turns of any session |

### 8.2 `sessions_yield` Internals

```
1. Deadlock guard: targetSessionId == currentSessionId → reject
2. Snapshot: target.History.Count
3. Enqueue: pipeline.WriteAsync(message)
4. Poll: TryGetActiveById → detect new assistant turn
   Poll interval: 500ms → 1s → 2s (backoff)
5. Eviction fallback: target evicted → LoadAsync from store once
6. Return: target reply text or timeout message
```

### 8.3 Registration

All session tools are grouped under the `group:sessions` preset in `ToolPresetResolver`, allowing operators to enable/disable the entire cluster with one preset entry.

---

## 9. History Compaction

Long conversations may exceed LLM context windows. The system provides LLM-based automatic compaction:

### 9.1 Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `enableCompaction` | `false` | Whether to enable |
| `compactionThreshold` | `40` | Turns threshold to trigger compaction |
| `compactionKeepRecent` | `10` | Most recent turns kept verbatim |

### 9.2 Compaction Flow

```
History.Count > compactionThreshold
  → Keep last keepRecent turns verbatim
  → Format older turns as summarization request
  → LLM generates 2-3 sentence summary (MaxOutputTokens=256, Temperature=0.3)
  → Remove old turns, insert [Previous conversation summary: ...]
  → Compaction fails → auto fallback to simple trim
```

### 9.3 Notes

- Compaction does NOT run inside the iteration loop (avoids cascading LLM calls)
- Runs once at turn start
- Existing summaries are re-summarized on next compaction pass (merged)

---

## 10. Persistent Token Audit

Every turn's token usage is written as an append-only JSONL ledger:

### 10.1 Ledger Model

```json
{
  "CorrelationId": "a1b2c3d4...",
  "SessionId": "webchat:user123",
  "ChannelId": "webchat",
  "ProviderId": "openai",
  "ModelId": "gpt-4o",
  "InputTokens": 1234,
  "OutputTokens": 567,
  "CacheReadTokens": 200,
  "CacheWriteTokens": 100,
  "EstimatedInputTokensByComponent": { "system": 500, "history": 600, "..." },
  "IsEstimated": false,
  "TimestampUtc": "2026-07-02T12:00:00Z"
}
```

### 10.2 File Path

```
Default: {Memory.StoragePath}/audit/turn-token-usage.jsonl
```

### 10.3 Three-Way Correlation

The same `CorrelationId` flows through three dimensions:

1. **Structured logs** — `[{CorrelationId}]` prefix
2. **Upstream provider HTTP headers** — `X-OpenClaw-Correlation-Id` (configurable)
3. **JSONL audit file** — `CorrelationId` field

External systems can inject their own trace ID via `X-Request-Id` / `X-Trace-Id` HTTP headers, propagated transparently as `CorrelationId`.

### 10.4 Estimated vs. Precise

- `IsEstimated: false` → Provider returned actual usage
- `IsEstimated: true` → Provider didn't return usage, heuristic estimation from message length applied

---

## 11. Optional Enhancements: Fractal Memory / MemPalace

### 11.1 Fractal Memory

| Dimension | Description |
|-----------|-------------|
| **Role** | Structured project memory (file-tree model: index/state/timeline/decisions/children/artifacts) |
| **Integration** | MCP protocol, stdio launch of `fractalmem-mcp` command |
| **vs. Core Sessions** | Complementary — Fractal Memory owns project knowledge, Core owns session state |
| **Auto-injection** | `AutoContextMode=auto` auto-searches and injects compact context each turn |
| **Writes** | V1 read-first; writes require `AllowWrites=true` + approval |

### 11.2 MemPalace

| Dimension | Description |
|-----------|-------------|
| **Role** | JIT-only knowledge-graph-enhanced memory backend |
| **Constraint** | No NativeAOT support; requires JIT mode + dynamic native plugin |
| **Storage model** | Wings → Rooms → Drawers three-tier namespace |
| **Tools** | `mempalace_kg`: triple add/query/timeline |

---

## 12. Architecture Diagram

```
                        ┌──────────────────────────┐
                        │    External Entrypoints   │
                        │  WebSocket / REST /       │
                        │  WhatsApp / Teams / CLI   │
                        └──────────┬───────────────┘
                                   │
                                   ▼
                        ┌──────────────────────┐
                        │    MessagePipeline    │
                        │   InboundWriter       │
                        │   (routed by SessionId)│
                        └──────────┬───────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              │                    │                    │
              ▼                    ▼                    ▼
    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
    │ Session A    │    │ Session B    │    │ Session C    │
    │ (User chat)  │    │ (Child agent)│    │ (Background) │
    └──────┬───────┘    └──────┬───────┘    └──────┬───────┘
           │                   │                   │
           └───────────────────┼───────────────────┘
                               │
                               ▼
                    ┌────────────────────┐
                    │   SessionManager   │
                    │                    │
                    │  ┌──────────────┐  │
                    │  │ _active cache │  │
                    │  └──────┬───────┘  │
                    │         │          │
                    │  ┌──────▼───────┐  │
                    │  │ IMemoryStore  │  │
                    │  │  Persistent   │  │
                    │  └──────────────┘  │
                    └────────────────────┘
                               │
          ┌────────────────────┼────────────────────┐
          │                    │                    │
          ▼                    ▼                    ▼
  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
  │ Checkpoints  │   │ Token Audit   │   │ Compaction   │
  │ System       │   │ JSONL Ledger  │   │ Engine       │
  └──────────────┘   └──────────────┘   └──────────────┘
          │
          ▼
  ┌──────────────┐
  │ Startup      │
  │ Recovery     │
  │ Worker       │
  └──────────────┘
```

---

## 13. Key Source Index

| Component | Path |
|-----------|------|
| Session model | `src/OpenClaw.Core/Models/Session.cs` |
| Session admin models | `src/OpenClaw.Core/Models/SessionAdminModels.cs` |
| SessionManager | `src/OpenClaw.Core/Sessions/SessionManager.cs` |
| AgentRuntime (checkpoints, resume, compaction) | `src/OpenClaw.Agent/AgentRuntime.cs` |
| AgentCheckpointManager | `src/OpenClaw.Agent/Runtime/AgentCheckpointManager.cs` |
| SessionsSpawnTool | `src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs` |
| SessionsYieldTool | `src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs` |
| SessionsTool (list/history/send) | `src/OpenClaw.Agent/Tools/SessionsTool.cs` |
| BackgroundSessionRecoveryWorker | `src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs` |
| GatewayInboundMessageWorker (continuation logic) | `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs` |
| TurnTokenUsageAuditLog | `src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs` |
| ToolPresetResolver (sessions preset) | `src/OpenClaw.Gateway/ToolPresetResolver.cs` |
| IGoalService | `src/OpenClaw.Core/Abstractions/IGoalService.cs` |
