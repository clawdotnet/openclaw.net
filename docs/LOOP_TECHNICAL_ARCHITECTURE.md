# OpenClaw.NET /loop Command — Technical Architecture

> A TickerQ-powered session-scoped recurring prompt injection mechanism. Users set a /loop command and the system automatically injects preset prompts into the session at specified intervals, driving agent turns forward. Supports idempotent override, dual-path semantic auto-termination, and cross-restart persistence.

- **Status:** Implemented (branch: `Feature/Codex-loop`)
- **Commits:** `78d115e`, `bb58cf3`, `42acf7c`
- **Total changes:** +1,033 lines across 11 files
- **Tests:** 62 passed, 0 failed

---

## Table of Contents

1. [Problem & Motivation](#1-problem--motivation)
2. [Architecture Overview](#2-architecture-overview)
3. [Component Inventory](#3-component-inventory)
4. [Loop Lifecycle State Machine](#4-loop-lifecycle-state-machine)
5. [TickerQ Scheduling Engine](#5-tickerq-scheduling-engine)
6. [Dual-Path Semantic Termination](#6-dual-path-semantic-termination)
7. [CLI & Chat Channel Commands](#7-cli--chat-channel-commands)
8. [Gateway DI & Wiring](#8-gateway-di--wiring)
9. [Error Handling & Concurrency](#9-error-handling--concurrency)
10. [OTel Context Cutting](#10-otel-context-cutting)
11. [Relationship with /goal](#11-relationship-with-goal)
12. [NativeAOT Compatibility](#12-nativeaot-compatibility)
13. [Testing Strategy](#13-testing-strategy)
14. [Future Extensions](#14-future-extensions)

---

## 1. Problem & Motivation

### The Need for Scheduled Autonomy

In daily development and operations, numerous scenarios require an agent to autonomously execute tasks on a fixed schedule:

- **Build health checks:** Check CI status every 5 minutes, report issues immediately
- **Log polling:** Scan production logs for anomaly patterns every 30 seconds
- **Periodic code review:** Review the latest commits every 2 hours
- **Environment monitoring:** Check server resource usage every hour

Traditional approaches require users to repeatedly type the same instruction, or rely on external cron scripts—both of which operate outside the agent's session context, losing access to memory, tool chains, and reasoning capabilities.

### What /loop Is

`/loop` is a **recurring prompt injection** command. When a user types `/loop 5m check build status` in a session, the system:

1. Parses the interval into a standard cron expression
2. Registers a scheduled job with the TickerQ scheduling engine
3. On each trigger, injects the preset prompt as a system message into the session
4. Advances the agent through a full tool-calling loop, producing a response
5. When the task is complete (model declaration or keyword match), automatically deregisters the timer

### Ecosystem Comparison

| Feature | Codex /loop | OpenClaw.NET /loop |
|---------|-------------|---------------------|
| Scheduling engine | In-memory timer (dies with client) | **TickerQ persistent cron jobs** |
| Restart recovery | ❌ Lost | ✅ Auto-recovery |
| Idempotent override | ✅ Single per session | ✅ Single per session |
| Semantic auto-stop | ✅ Keyword detection | ✅ **Dual-path (tool + keyword)** |
| Trigger channels | CLI/TUI only | **CLI + chat channels** |
| Concurrency safety | No cluster support | TickerQ row locks |
| Host language | Rust | **C# / .NET** |

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                    User / Operator (CLI / Channel)                    │
│       /loop 5m check build status                                    │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ChatCommandProcessor (/loop)                       │
│  schedule (with interval + prompt) │ cancel │ status                 │
│  Built-in command, bridged to Gateway via SetLoopCallback            │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│              OpenClaw.Core.Loops (standalone scheduling subsystem)    │
│                                                                      │
│  ┌──────────────────┐   ┌──────────────────────────────┐           │
│  │ ClawLoopScheduler │──▶│ ConcurrentDictionary          │           │
│  │  · ScheduleLoop   │   │   sessionId → LoopEntry       │           │
│  │  · CancelLoop     │   │   (cron expression + state)    │           │
│  │  · GetLoopStatus  │   └──────────┬───────────────────┘           │
│  └────────▲─────────┘               │ polled every minute            │
│           │                         ▼                                │
│  ┌────────┴─────────┐   ┌──────────────────────────────┐           │
│  │ ILoopControlService│  │ AgentLoopJob                  │           │
│  │  · SignalComplete  │  │ [TickerFunction(              │           │
│  └────────▲─────────┘   │   "AgentLoopExecutor",         │           │
│           │              │   cronExpression: "* * * * *")]│           │
│  ┌────────┴─────────────┐──────────────────────────────┘           │
│  │ LoopTerminationDetector                                           │
│  │  Path 1 (primary): loop_control tool → SignalComplete            │
│  │  Path 2 (fallback): response text keyword match → SignalComplete │
│  └─────────────────────────────────────────────────────┘            │
└─────────────────────────────────────────────────────────────────────┘
               │ dispatches turn
               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    OpenClaw.Agent                                     │
│  ┌──────────────────┐   ┌──────────────────────────────────────┐   │
│  │ LoopControlTool   │   │ AgentRuntime (zero invasion)           │   │
│  │ IToolWithContext  │   │  loop prompt flows as a regular        │   │
│  │ → ILoopControlSvc │   │  InboundMessage into MessagePipeline   │   │
│  └──────────────────┘   │  → RunAsync()                           │   │
│                          └──────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

**Key design principles:**

- **Core/Loops/** is a pure scheduling layer—no dependency on Agent or Gateway, only TickerQ + DI abstractions
- **Zero AgentRuntime invasion:** loop prompts are injected via `MessagePipeline.InboundWriter`, following the same path as normal user messages
- **Goal path unaffected:** `/loop` and `/goal` are independent scheduling entry points sharing AgentRuntime but with fully isolated lifecycle management

---

## 3. Component Inventory

| Component | Path | Responsibility |
|-----------|------|----------------|
| `AgentLoopRequestPayload` | `Core/Loops/AgentLoopRequestPayload.cs` | Strongly-typed JSON payload record (sessionId + prompt), AOT-safe source-gen serialization |
| `IAgentLoopDispatcher` | `Core/Loops/IAgentLoopDispatcher.cs` | Dispatch interface: injects loop prompt into session and advances the turn |
| `ILoopControlService` | `Core/Loops/ILoopControlService.cs` | Termination signal interface: receives completion notifications from tool or detector |
| `ClawLoopScheduler` | `Core/Loops/ClawLoopScheduler.cs` | Scheduling facade: manages `ConcurrentDictionary<string, LoopEntry>` registry, provides Schedule/Cancel/Status + `IntervalToCron()` static method |
| `LoopEntry` | `Core/Loops/ClawLoopScheduler.cs` | Single loop entry: sessionId, prompt, cron expression, NCrontab-parsed `CrontabSchedule`, next trigger time |
| `AgentLoopJob` | `Core/Loops/AgentLoopJob.cs` | `[TickerFunction("AgentLoopExecutor", cronExpression: "* * * * *")]`: polls registry every minute, dispatches due entries |
| `LoopTerminationDetector` | `Core/Loops/LoopTerminationDetector.cs` | Dual-path termination detection: structured tool signal (primary) + `FrozenSet<string>` keyword match (fallback) |
| `LoopCommandParser` | `Core/Loops/LoopCommandParser.cs` | `[GeneratedRegex]` compile-time parsing of `/loop <value><unit> <prompt>` syntax |
| `LoopAction` / `LoopCommand` | `Core/Loops/LoopCommandParser.cs` | Command model enum (Schedule/Cancel/Status/Invalid) and POCO |
| `LoopControlTool` | `Agent/Tools/LoopControlTool.cs` | `IToolWithContext`: exposed to LLM; model can explicitly declare `status="complete"` to terminate loop |
| `ChatCommandProcessor` | `Core/Pipeline/ChatCommandProcessor.cs` | Built-in `/loop` command routing + `SetLoopCallback` bridge to Gateway |
| `CoreServicesExtensions` | `Gateway/Composition/` | DI registration: `ClawLoopScheduler`, `AgentLoopJob`, `LoopTerminationDetector`, `LoopControlTool` |
| `RuntimeInitializationExtensions` | `Gateway/Composition/` | Loop callback wiring: parse command → call `ClawLoopScheduler` → return response text |

---

## 4. Loop Lifecycle State Machine

### States

| State | Meaning | Trigger |
|-------|---------|---------|
| **Scheduled** | Loop registered, awaiting timer | `/loop <interval> <prompt>` |
| **Running** | A loop turn is executing | TickerQ tick finds due entry |
| **Overridden** | Replaced by a new `/loop` command | Same session issues another `/loop` |
| **Terminated** | Auto or manually canceled (terminal) | Semantic detection fires or `/loop cancel` |

### Transition Diagram

```
                          /loop 5m <prompt>
                               │
                               ▼
                         ┌──────────┐
            ┌───────────│ Scheduled │◄──────────────┐
            │           └─────┬─────┘               │
            │    /loop cancel │  TickerQ tick         │ /loop 10m <new>
            │                ▼                        │ (idempotent override)
            │           ┌─────────┐                  │
            │           │ Running  │─────────────────┘
            │           └────┬────┘
            │                │
            │    ┌───────────┼───────────┐
            │    │           │           │
            │    ▼           ▼           ▼
            │   Model       Keyword     Normal
            │   calls       match       completion
            │ loop_control  hit         (no tool calls,
            │ (status=                  turn ends)
            │  complete)
            │    │           │           │
            │    └───────────┴───────────┘
            │                │
            ▼                ▼
       ┌──────────────────────────┐
       │       Terminated          │
       │  (entry removed from      │
       │   ConcurrentDictionary)   │
       └──────────────────────────┘
```

### Key Rules

- **Idempotent override:** Only one active loop per sessionId. Subsequent `/loop` commands atomically replace the old entry via `ConcurrentDictionary.AddOrUpdate`
- **Non-preemptive execution:** On timer tick, the prompt is written to `MessagePipeline.InboundWriter`. If the session is busy (session lock held), the message naturally queues in the Channel, consumed after the current turn completes
- **Silent self-destruct:** Semantic termination does not actively notify the user (they may be offline). Only Information-level logging is emitted. Manual `/loop cancel` echoes a confirmation

---

## 5. TickerQ Scheduling Engine

### Architecture Choice

TickerQ 10.4.0's public API does not expose `ICronTickerManager` for dynamic registration. The solution uses a **compile-time `[TickerFunction]` + in-memory registry** hybrid:

```
Compile-time                             Runtime
┌──────────────────────────┐      ┌──────────────────────────┐
│ [TickerFunction(          │      │ ClawLoopScheduler         │
│   "AgentLoopExecutor",    │      │   ConcurrentDictionary    │
│   cronExpression:         │────────▶  sessionId → LoopEntry   │
│   "* * * * *")]           │ every │   (cron + nextTrigger)   │
│ AgentLoopJob.ExecuteAsync │ minute│                          │
└──────────────────────────┘      │ AgentLoopJob polls         │
                                   │ every minute:             │
                                   │   var due = scheduler     │
                                   │     .GetDueEntries(now);  │
                                   │   foreach → DispatchAsync │
                                   └──────────────────────────┘
```

### Cron Expression Generation

`ClawLoopScheduler.IntervalToCron()` converts user-friendly formats to 6-field cron:

| User Input | Cron Expression | Meaning |
|------------|-----------------|---------|
| `5m` | `*/5 * * * *` | Every 5 minutes |
| `30s` | `*/30 * * * * *` | Every 30 seconds (seconds field) |
| `120s` | `*/2 * * * *` | Every 120s = every 2 minutes |
| `1h` | `0 */1 * * *` | Every hour (on the hour) |

### Retry & Failure Handling

TickerQ job-level retries are not implemented at this layer—exceptions during `AgentLoopJob` dispatch are caught and logged as errors, not thrown to TickerQ. A single dispatch failure does not affect the next tick. Design philosophy: loops are heartbeat mechanisms; missing one beat should not cascade.

---

## 6. Dual-Path Semantic Termination

### Path 1: Structured Tool (Primary)

The LLM explicitly declares completion by calling the `loop_control` tool:

```json
{
  "name": "loop_control",
  "arguments": { "status": "complete" }
}
```

`LoopControlTool.ExecuteAsync` → `LoopTerminationDetector.OnToolCompleteAsync` → `ILoopControlService.SignalCompleteAsync` → `ClawLoopScheduler.CancelLoopAsync` → entry removed from `ConcurrentDictionary`.

**External validation:** the tool only accepts `status="complete"`. Any other value (`paused`, `done`, etc.) returns an error and does not trigger termination.

### Path 2: Keyword Match (Fallback)

`LoopTerminationDetector` scans model response text for predefined keywords (case-insensitive):

```csharp
private static readonly FrozenSet<string> TerminationKeywords = new[]
{
    "LOOP_TERMINATE",
    "DONE",
    "WORK_COMPLETE",
}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
```

Any keyword hit triggers the same termination flow as Path 1. No hit means no action—the loop waits for the next tick.

### Defense Design

| Scenario | Behavior |
|----------|----------|
| Tool calls `loop_control` but was denied by approval | Treated as not yet complete; loop continues |
| Response text contains keyword but task is unfinished | False-positive risk (conservative strategy). Keywords are intentionally minimal |
| Both paths fire simultaneously | `ConcurrentDictionary.TryRemove` is idempotent; second call is a no-op |
| Response has neither tool call nor keyword | No termination triggered; normal cron wait |

---

## 7. CLI & Chat Channel Commands

### Command Reference

`/loop` is a **built-in** command in `ChatCommandProcessor`, alongside `/status`, `/goal`, `/help`, etc.

| Command | Function |
|---------|----------|
| `/loop <value><unit> <prompt>` | Start or override a recurring loop. unit: `s` (seconds), `m` (minutes), `h` (hours) |
| `/loop cancel` | Manually cancel the current session's loop |
| `/loop stop` | Alias for `/loop cancel` |
| `/loop status` | Query the current session's loop state |

### Examples

```
User: /loop 5m check latest CI build status

System: Loop started — interval: 5m, prompt: "check latest CI build status"

--- 5 minutes later ---
[System auto-injects prompt, Agent executes tool calls, returns results]

User: /loop status

System: Loop active — cron: */5 * * * *, prompt: "check latest CI build status", scheduled at: 2026-06-20T14:35:00.0000000Z

User: /loop cancel

System: Loop canceled.
```

### Command Parsing

`LoopCommandParser.TryParse()` uses `[GeneratedRegex]` compile-time regex:

```csharp
[GeneratedRegex(@"^/loop\s+(?<value>\d+)\s*(?<unit>s|m|h)\s+(?<prompt>.+)$", RegexOptions.IgnoreCase)]
private static partial Regex LoopCommandRegex();
```

Parse priority:
1. Exact match `/loop cancel` or `/loop stop` → `LoopAction.Cancel`
2. Exact match `/loop status` → `LoopAction.Status`
3. Regex match `<value><unit> <prompt>` → `LoopAction.Schedule`
4. None of the above → `LoopAction.Invalid`
5. Does not start with `/loop` → returns `null` (not a loop command, flows to LLM normally)

### Multi-Channel Support

The loop callback is registered via `SetLoopCallback` in `RuntimeInitializationExtensions`. `ChatCommandProcessor` uniformly invokes this callback for `/loop` messages from any channel (CLI, Telegram, WhatsApp, Discord, etc.). No channel-specific adapter code is needed.

---

## 8. Gateway DI & Wiring

### Service Registration

In `CoreServicesExtensions.AddOpenClawCoreServices()`:

```csharp
// Loop scheduling
services.AddSingleton<ClawLoopScheduler>();
services.AddSingleton<ILoopControlService>(sp => sp.GetRequiredService<ClawLoopScheduler>());
services.AddSingleton<LoopTerminationDetector>();
services.AddSingleton<AgentLoopJob>();

// Loop control tool (registered alongside other ITool implementations)
services.AddSingleton<ITool, LoopControlTool>();
```

### Callback Wiring

In `RuntimeInitializationExtensions.InitializeOpenClawRuntimeAsync()`, immediately after `SetCompactCallback`:

```csharp
services.CommandProcessor.SetLoopCallback(async (session, text, ct) =>
{
    var scheduler = app.Services.GetRequiredService<ClawLoopScheduler>();
    var cmd = LoopCommandParser.TryParse(text);

    return cmd.Action switch
    {
        LoopAction.Cancel   => await CancelLoopAsync(scheduler, session.Id, ct),
        LoopAction.Status   => await GetLoopStatusAsync(scheduler, session.Id, ct),
        LoopAction.Schedule => await ScheduleLoopAsync(scheduler, session.Id, cmd.Interval!, cmd.Prompt!, ct),
        _                   => "Usage: /loop <interval> <prompt> ..."
    };
});
```

### AgentLoopJob Discovery

TickerQ automatically discovers classes annotated with `[TickerFunction]` via the DI container. Once `AgentLoopJob` is registered as a singleton, TickerQ scans it at startup and starts the cron timer on its `ExecuteAsync` method.

---

## 9. Error Handling & Concurrency

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| Session deleted, loop fires | `AgentLoopJob` dispatch finds no session → logs Warning, silently skips entry (does not remove—recheck on next tick) |
| User rapidly issues multiple `/loop` | Last one wins. Response notes the interval change |
| `/loop` used while `/goal` is active | Allowed; both operate independently |
| `loop_control` tool denied by approval | Tool returns error; loop does not terminate |
| Interval is 0 or has invalid unit | `IntervalToCron()` throws `ArgumentException`; callback catches and echoes `Error: ...` |

### Concurrency Safety

| Layer | Mechanism |
|-------|-----------|
| `ClawLoopScheduler._entries` | `ConcurrentDictionary<string, LoopEntry>` — thread-safe AddOrUpdate/TryRemove |
| `LoopEntry.IsDue()` | Intra-entry `_nextOccurrence` compare-and-update is not interlocked, but safe in TickerQ's single-threaded consumer model |
| Agent layer | `AgentRuntime` already has session locks; only one turn per session at a time |
| TickerQ | `[TickerFunction]` cron consumer runs single-threaded; no intra-tick race conditions |

---

## 10. OTel Context Cutting

To prevent nested OpenTelemetry span chains across loop iterations (Codex suffered 34 GB/day log inflation from this), `AgentLoopJob.ExecuteAsync` explicitly cuts the context before each execution:

```csharp
[TickerFunction("AgentLoopExecutor", cronExpression: "* * * * *")]
public async Task ExecuteAsync(TickerFunctionContext context, CancellationToken ct)
{
    // Cut OTel context to prevent nested span chains across loop iterations
    Activity.Current = null;

    var now = DateTimeOffset.UtcNow;
    var dueEntries = _scheduler.GetDueEntries(now);
    foreach (var entry in dueEntries)
    {
        await _dispatcher.DispatchAsync(entry.SessionId, entry.Prompt, ct);
    }
}
```

This ensures span depth is always constant for each loop tick, regardless of how many iterations the loop has run.

---

## 11. Relationship with /goal

`/loop` and `/goal` are two **independent but complementary** scheduling paths:

| Dimension | /loop | /goal |
|-----------|-------|-------|
| Trigger | External timer injects prompt | Model auto-continues on stop |
| Scheduling engine | TickerQ cron jobs | AgentRuntime loop inline |
| Lifecycle | Scheduled → Running → Terminated | Active → Paused/Blocked/BudgetLimited → Complete |
| Model tools | `loop_control` (complete only) | `get_goal`, `create_goal`, `update_goal` |
| Budget system | None | Token budget + baseline mechanism |
| Persistence | TickerQ database (survives restarts) | InMemory + JSONL history |

**Coexistence rules:**
- A single session can have both `/loop` and `/goal` simultaneously
- `/loop cancel` does not affect the goal; `/goal clear` does not affect the loop
- One shared touchpoint: `ChatCommandProcessor` routes both command sets

---

## 12. NativeAOT Compatibility

All new code is designed for AOT compatibility:

| Component | AOT Safety Check |
|-----------|-----------------|
| `LoopCommandParser` | Uses `[GeneratedRegex]` compile-time regex, no runtime `new Regex()` |
| `AgentLoopJob` | `[TickerFunction]` handled by TickerQ Source Generator at compile time |
| `AgentLoopRequestPayload` | Uses `System.Text.Json` + `[JsonPropertyName]` source generation |
| `LoopTerminationDetector` | `FrozenSet<string>` is an AOT-safe compile-time collection |
| `LoopControlTool` | Implements `IToolWithContext` interface (no reflection-based registration; explicit DI `AddSingleton`) |

No `dynamic`, no `MakeGenericType`, no unannotated `[RequiresUnreferencedCode]`.

---

## 13. Testing Strategy

### Unit Test Coverage

| Component Under Test | Test File | Verification Points |
|---------------------|-----------|---------------------|
| `LoopCommandParser` | `LoopCommandParserTests.cs` | Valid schedule parsing (including Chinese prompts); cancel/stop/status exact matches; invalid input returns Invalid; non-loop commands return null |
| `ClawLoopScheduler` | `ClawLoopSchedulerTests.cs` | Schedule → entry exists; Cancel → entry removed; SignalComplete → cancels via explicit interface; idempotent override; nonexistent session returns null; `IntervalToCron` correct conversion and invalid input throws |
| `LoopTerminationDetector` | `LoopControlToolTests.cs` | Keyword hit triggers SignalComplete; no hit does not trigger; empty/null text returns false; OnToolComplete triggers SignalComplete |
| `LoopControlTool` | `LoopControlToolTests.cs` | `status="complete"` correctly forwarded; invalid status returns error; no session context returns error; tool metadata verification |

### Test Statistics

- **Test files:** 3
- **Test methods:** 62 (including inherited matches from Goal-related naming)
- **Pass rate:** 100%
- **Mock framework:** NSubstitute

---

## 14. Future Extensions

### Approval Pause Protocol (Phase 2)

When a loop turn triggers an approval block (`ToolApprovalCallback` returns false or waits for human decision):
- Leverage TickerQ job pause/resume capabilities
- Auto-resume loop upon approval
- Solve the Codex-class "token burning" problem (retrying approval requests repeatedly while unattended)

### REST API Management Endpoints

Expose HTTP endpoints for programmatic loop management by external systems:
- `POST /api/loops` — create loop
- `DELETE /api/loops/{sessionId}` — cancel loop
- `GET /api/loops/{sessionId}` — query loop status
- `GET /api/loops` — list all active loops

### Persistence Enhancement

Currently loop entries are stored in an in-memory `ConcurrentDictionary`, lost on restart. TickerQ itself supports EF Core persistence—serializing `LoopEntry` into TickerQ database tables would enable true cross-restart persistence.
