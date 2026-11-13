# OpenClaw.NET /loop Command — Technical Architecture

> A lightweight hosted-service scheduler for session-scoped recurring prompt injection. Users set a /loop command and the system automatically injects preset prompts at intervals of at least 60 seconds, driving agent turns forward. Active loop entries live in memory and do not survive process restarts.

- **Status:** Implemented
- **Minimum interval:** 60 seconds
- **Persistence:** In-memory; active loops do not survive process restarts
- **Validation:** Covered by the repository's loop, command, and integration test suites

---

## Table of Contents

1. [Problem & Motivation](#1-problem--motivation)
2. [Architecture Overview](#2-architecture-overview)
3. [Component Inventory](#3-component-inventory)
4. [Loop Lifecycle State Machine](#4-loop-lifecycle-state-machine)
5. [Hosted Scheduling Engine](#5-hosted-scheduling-engine)
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
- **Log polling:** Scan production logs for anomaly patterns every 60 seconds
- **Periodic code review:** Review the latest commits every 2 hours
- **Environment monitoring:** Check server resource usage every hour

Traditional approaches require users to repeatedly type the same instruction, or rely on external cron scripts—both of which operate outside the agent's session context, losing access to memory, tool chains, and reasoning capabilities.

### What /loop Is

`/loop` is a **recurring prompt injection** command. When a user types `/loop 5m check build status` in a session, the system:

1. Parses the interval as an elapsed duration anchored to registration time
2. Stores the loop entry in the in-memory scheduler registry
3. On each polling tick that finds the entry due, injects the preset prompt as a system message into the session
4. Advances the agent through a full tool-calling loop, producing a response
5. When the task is complete (model declaration or keyword match), removes the loop entry from the registry

### Ecosystem Comparison

| Feature | Codex /loop | OpenClaw.NET /loop |
|---------|-------------|---------------------|
| Scheduling engine | In-memory timer (dies with client) | **Hosted one-second poller + in-memory loop registry** |
| Restart recovery | ❌ Lost | ❌ Active loop registry lost on restart (persistence planned) |
| Idempotent override | ✅ Single per session | ✅ Single per session |
| Semantic auto-stop | ✅ Keyword detection | ✅ **Dual-path (tool + keyword)** |
| Trigger channels | CLI/TUI only | **CLI + chat channels** |
| Concurrency safety | No cluster support | `ConcurrentDictionary` + per-entry lock |
| Host language | Rust | **C# / .NET** |

---

## 2. Architecture Overview

```plaintext
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
│  │  · CancelLoop     │   │   (duration + next-run state)    │           │
│  │  · GetLoopStatus  │   └──────────┬───────────────────┘           │
│  └────────▲─────────┘               │ polled every second            │
│           │                         ▼                                │
│  ┌────────┴─────────┐   ┌──────────────────────────────┐           │
│  │ ILoopControlService│  │ AgentLoopJob                  │           │
│  │  · SignalComplete  │  │ BackgroundService +          │           │
│  └────────▲─────────┘   │ PeriodicTimer (one second)     │           │
│           │              │                                │           │
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

- **Core/Loops/** is a pure scheduling layer—no dependency on Agent or Gateway, only hosting, logging, and DI abstractions
- **Zero AgentRuntime invasion:** loop prompts are injected via `MessagePipeline.InboundWriter`, following the same path as normal user messages
- **Goal path unaffected:** `/loop` and `/goal` are independent scheduling entry points sharing AgentRuntime but with fully isolated lifecycle management

---

## 3. Component Inventory

| Component | Path | Responsibility |
|-----------|------|----------------|
| `AgentLoopRequestPayload` | `Core/Loops/AgentLoopRequestPayload.cs` | Strongly-typed JSON payload record (sessionId + prompt), AOT-safe source-gen serialization |
| `IAgentLoopDispatcher` | `Core/Loops/IAgentLoopDispatcher.cs` | Dispatch interface: injects loop prompt into session and advances the turn |
| `ILoopControlService` | `Core/Loops/ILoopControlService.cs` | Termination signal interface: receives completion notifications from tool or detector |
| `ClawLoopScheduler` | `Core/Loops/ClawLoopScheduler.cs` | Scheduling facade: manages `ConcurrentDictionary<string, LoopEntry>` registry, provides duration and cron scheduling, Cancel/Status, and `ParseInterval()` |
| `LoopEntry` | `Core/Loops/ClawLoopScheduler.cs` | Single loop entry: sessionId, prompt, fixed duration or NCrontab schedule, next trigger time |
| `AgentLoopJob` | `Core/Loops/AgentLoopJob.cs` | Hosted `BackgroundService`: polls the registry every second and dispatches due entries |
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
| **Running** | A loop turn is executing | Hosted polling tick finds a due entry |
| **Overridden** | Replaced by a new `/loop` command | Same session issues another `/loop` |
| **Terminated** | Auto or manually canceled (terminal) | Semantic detection fires or `/loop cancel` |

### Transition Diagram

```plaintext
                          /loop 5m <prompt>
                               │
                               ▼
                         ┌──────────┐
            ┌───────────│ Scheduled │◄──────────────┐
            │           └─────┬─────┘               │
            │    /loop cancel │  polling tick         │ /loop 10m <new>
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

## 5. Hosted Scheduling Engine

### Architecture Choice

The scheduler uses a hosted `BackgroundService` with `PeriodicTimer` and an in-memory registry. This avoids creating one operating-system timer or durable scheduler job per session:

```plaintext
Hosted worker                         Runtime registry
┌──────────────────────────┐      ┌──────────────────────────┐
│ AgentLoopJob              │      │ ClawLoopScheduler         │
│ BackgroundService         │      │ ConcurrentDictionary      │
│ PeriodicTimer(1 second)   │─────▶│ sessionId → LoopEntry    │
│ DispatchDueAsync          │      │ duration/cron + nextRun   │
└──────────────────────────┘      └──────────────────────────┘
```

### Duration Scheduling

`ScheduleIntervalLoopAsync()` accepts whole seconds, minutes, or hours from 60 seconds through 365 days. The first run is due one full interval after registration. Subsequent due times remain anchored to that registration, including across hour and day boundaries.

| User input | Elapsed time between due runs |
| --- | --- |
| `60s` | 60 seconds |
| `90s` | 90 seconds |
| `45m` | 45 minutes |
| `90m` | 90 minutes |
| `25h` | 25 hours |

The hosted dispatcher polls every second. Dispatch can be later under load. A late poll dispatches once and skips missed occurrences while preserving cadence. It does not emit a burst of catch-up runs. Active loops remain in memory and do not survive restart.

Explicit cron callers can still use `ScheduleLoopAsync()`. The legacy `IntervalToCron()` helper rejects durations that cannot be represented exactly instead of silently rounding them. Chat `/loop` uses duration scheduling directly.

### Retry & Failure Handling

Job-level retries are not implemented at this layer. Exceptions during `AgentLoopJob` dispatch are caught and logged so a single dispatch failure does not stop the hosted worker or affect the next tick.

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

Any whole-keyword hit triggers the same termination flow as Path 1. Substrings inside longer words do not match. No hit means no action—the loop waits for the next tick.

### Defense Design

| Scenario | Behavior |
|----------|----------|
| Tool calls `loop_control` but was denied by approval | Treated as not yet complete; loop continues |
| Response text contains a complete keyword but task is unfinished | False-positive risk (conservative strategy). Keywords are intentionally minimal and matched on word boundaries |
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

```plaintext
User: /loop 5m check latest CI build status

System: Loop started — interval: 5m, prompt: "check latest CI build status"

--- 5 minutes later ---
[System auto-injects prompt, Agent executes tool calls, returns results]

User: /loop status

System: Loop active — interval: 00:05:00, prompt: "check latest CI build status", scheduled at: 2026-06-20T14:35:00.0000000Z

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
services.AddHostedService(sp => sp.GetRequiredService<AgentLoopJob>());

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

### AgentLoopJob Hosting

`AgentLoopJob` is registered as both a singleton and an `IHostedService`, so the host starts one worker instance and reuses that instance for focused tests and service access.

---

## 9. Error Handling & Concurrency

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| Session deleted, loop fires | `AgentLoopJob` dispatch finds no session → logs Warning, silently skips entry (does not remove—recheck on next tick) |
| User rapidly issues multiple `/loop` | Last one wins. Response notes the interval change |
| `/loop` used while `/goal` is active | Allowed; both operate independently |
| `loop_control` tool denied by approval | Tool returns error; loop does not terminate |
| Interval is 0 or has invalid unit | `ParseInterval()` throws `ArgumentException`; callback catches and echoes `Error: ...` |

### Concurrency Safety

| Layer | Mechanism |
|-------|-----------|
| `ClawLoopScheduler._entries` | `ConcurrentDictionary<string, LoopEntry>` — thread-safe AddOrUpdate/TryRemove |
| `LoopEntry.IsDue()` | Intra-entry `_nextOccurrence` compare-and-update is protected by a per-entry lock |
| Agent layer | `AgentRuntime` already has session locks; only one turn per session at a time |
| Hosted worker | `PeriodicTimer` invokes a one-second polling pass; per-loop state remains in the in-memory registry |

---

## 10. OTel Context Cutting

To prevent nested OpenTelemetry span chains across loop iterations, `AgentLoopJob.DispatchDueAsync` explicitly cuts the context before each polling pass:

```csharp
internal async Task DispatchDueAsync(CancellationToken ct)
{
    // Cut OTel context to prevent nested span chains across loop iterations
    Activity.Current = null;

    var dueEntries = _scheduler.GetDueEntries();
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
| Scheduling engine | Hosted one-second poller + in-memory registry | AgentRuntime loop inline |
| Lifecycle | Scheduled → Running → Terminated | Active → Paused/Blocked/BudgetLimited → Complete |
| Model tools | `loop_control` (complete only) | `get_goal`, `create_goal`, `update_goal` |
| Budget system | None | Token budget + baseline mechanism |
| Persistence | In-memory loop registry (persistence planned) | InMemory + JSONL history |

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
| `AgentLoopJob` | Direct `BackgroundService` registration; no runtime discovery or reflection |
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
| `ClawLoopScheduler` | `ClawLoopSchedulerTests.cs` | Schedule → entry exists; invalid cron is normalized to `ArgumentException`; Cancel → entry removed; SignalComplete → cancels via explicit interface; idempotent override; due polling; concurrent `IsDue` access |
| Interval scheduling | `LoopIntervalTests.cs` | 60-second minimum; exact elapsed cadence; late-tick handling without drift or catch-up bursts; overflow and invalid input rejection |
| `LoopTerminationDetector` | `LoopControlToolTests.cs` | Whole-keyword hit triggers SignalComplete; substrings do not trigger; empty/null text returns false; OnToolComplete triggers SignalComplete |
| `LoopControlTool` | `LoopControlToolTests.cs` | `status="complete"` correctly forwarded; invalid status returns error; no session context returns error; tool metadata verification |

The loop tests run as part of the standard `OpenClaw.Tests` suite. Focused behavior
changes should add regression coverage there and remain compatible with the
repository's NativeAOT validation lane.

---

## 14. Future Extensions

### Approval Pause Protocol (Phase 2)

When a loop turn triggers an approval block (`ToolApprovalCallback` returns false or waits for human decision):
- Add explicit pause/resume state to the in-memory loop registry
- Auto-resume loop upon approval
- Solve the Codex-class "token burning" problem (retrying approval requests repeatedly while unattended)

### REST API Management Endpoints

Expose HTTP endpoints for programmatic loop management by external systems:
- `POST /api/loops` — create loop
- `DELETE /api/loops/{sessionId}` — cancel loop
- `GET /api/loops/{sessionId}` — query loop status
- `GET /api/loops` — list all active loops

### Persistence Enhancement

Currently loop entries are stored in an in-memory `ConcurrentDictionary` and are lost on restart. A future durable store could persist `LoopEntry` state and restore its next occurrence during startup.
