# OpenClaw.NET Goal Mechanism — Technical Architecture

> A session-scoped persistence mechanism that automatically continues agent execution when the model stops before completing its objective. Implements full upstream OpenClaw Goal semantics for the .NET runtime.

- **Status:** Implemented (branch: `goal`)
- **Commits:** `21e177b`, `02e9990`, `4c26f48`
- **Total delta:** +2,716 lines across 20 files
- **Tests:** 59 passing, 0 failing

---

## Table of Contents

1. [Problem & Motivation](#1-problem--motivation)
2. [Architecture Overview](#2-architecture-overview)
3. [Component Breakdown](#3-component-breakdown)
4. [6-State State Machine](#4-6-state-state-machine)
5. [Token Budget System](#5-token-budget-system)
6. [Model Tools](#6-model-tools)
7. [Runtime Integration](#7-runtime-integration)
   - [Native AgentRuntime](#71-native-agentruntime)
   - [MAF Adapter (MafAgentRuntime)](#72-maf-adapter-mafagentruntime)
8. [CLI Commands](#8-cli-commands)
9. [TUI Display](#9-tui-display)
10. [Prompt Design](#10-prompt-design)
11. [External Verification](#11-external-verification)
12. [Channel Gating](#12-channel-gating)
13. [DI Registration](#13-di-registration)
14. [NativeAOT Compatibility](#14-nativeaot-compatibility)
15. [Test Strategy](#15-test-strategy)
16. [Design Decisions (from Reviews)](#16-design-decisions-from-reviews)
17. [Future Extensions](#17-future-extensions)

---

## 1. Problem & Motivation

### The Lazy Model Problem

In long-running agent tasks (bug fixes, documentation, refactoring), Large Language Models consistently exhibit premature stopping behavior:

- **Partial completion:** The model finishes part of the work and stops, leaving the rest undone
- **False victory:** The model declares the task complete without verifying the full scope
- **Scope contraction:** The model substitutes a simpler solution that doesn't fully meet the objective

Users compensate by repeatedly typing "continue" — a manual, error-prone loop. The Goal mechanism automates this.

### What Goal Is

Goal is a session-scoped persistence mechanism. When the model stops (`toolCalls.Count == 0`), the runtime automatically:

1. Checks if an active goal exists
2. Evaluates whether the objective has been achieved
3. If **not achieved** and within limits → injects a "Goal Check" prompt and continues the iteration loop
4. If **achieved** or **blocked** or **budget exhausted** → returns normally

This transforms agent execution from a single-shot interaction into an async job model: fire and forget.

### Ecosystem Context

| Feature | Claude Code | Codex | Upstream OpenClaw | OpenClaw.NET |
|---------|-------------|-------|-------------------|--------------|
| Trigger | Hook on Stop | Hook on Stop | Hook on Stop | **Inline in turn loop** |
| States | 3 | 5 | 6 | **6** |
| Token budget | ✅ | ✅ | ✅ | ✅ (128K default) |
| Model tools | — | `update_goal` | 3 tools | **3 tools** |
| Block detection | Implicit | 3 repeats | 3 repeats | **3 repeats (text hash)** |
| CLR/Host Language | TypeScript | TypeScript | Go/TS | **C# / .NET** |

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                           User / Operator                            │
│       /goal start "fix CI for PR 87469"  +500k                      │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ChatCommandProcessor (/goal)                       │
│  start │ pause │ resume │ complete │ block │ clear │ status          │
│  Built-in command in session pipeline                                │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     IGoalService (InMemoryGoalService)                │
│  ┌──────────┐  ┌──────────┐  ┌───────────┐  ┌───────────────────┐  │
│  │CreateGoal│  │UpdateStat│  │TokenTrack │  │BlockerDetect      │  │
│  └──────────┘  └──────────┘  └───────────┘  └───────────────────┘  │
│              ConcurrentDictionary<string, SessionGoal>               │
└─────────────────────────────────────────────────────────────────────┘
                  │                  │                  │
                  ▼                  ▼                  ▼
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐
│   Goal Tools (3)  │  │AgentRuntimeGoal │  │   TUI Footer         │
│   ─────────────   │  │  Integration     │  │   ────────────       │
│  get_goal (read)  │  │ Activation prompt│  │  Status text         │
│  create_goal (w)  │  │ Check prompt     │  │  Progress bar        │
│  update_goal (w)  │  │ Budget gating    │  │  Per-state display   │
└──────────────────┘  │ Channel gating    │  └──────────────────────┘
                       │ Blocker detection│
                       └──────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    AgentRuntime (Turn Loop)                           │
│                                                                      │
│  for (i = 0; i < MaxIterations; i++)                                 │
│  {                                                                    │
│      messages = BuildMessages(session)                                │
│      if (Goal active) → Inject activation prompt at index 1          │
│                                                                      │
│      response = await LLM_Call(messages)                              │
│                                                                      │
│      if (toolCalls.Count == 0)  // Model stopped                     │
│      {                                                                │
│          if (GoalIntegration.ShouldContinue(session, i))              │
│          {                                                            │
│              messages.Add(goal_check_prompt)                          │
│              continue;  // ← Auto-continue                           │
│          }                                                            │
│          return finalResponse;  // Normal stop                       │
│      }                                                                │
│                                                                      │
│      Execute tools → feed results back to LLM                        │
│  }                                                                    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. Component Breakdown

| Component | Path | Responsibility |
|-----------|------|----------------|
| `GoalStatus` | `Core/Models/Goal/GoalStatus.cs` | 6-state enum + extension methods (`IsPursuable`, `IsTerminal`, `ToDisplayName`, `FormatGoalFooterLine`, `FormatGoalProgressBar`) |
| `SessionGoal` | `Core/Models/Goal/SessionGoal.cs` | Data model: objective, budget, tokens, blocker hashes, continuation tracking, normalization + SHA-256 hashing |
| `GoalHistoryRecord` | `Core/Models/Goal/GoalHistoryRecord.cs` | AOT-compatible serialization record for goal history JSONL |
| `IGoalService` | `Core/Abstractions/IGoalService.cs` | Service interface: CRUD, state transitions, token tracking, blocker detection, history persistence |
| `InMemoryGoalService` | `Core/Services/InMemoryGoalService.cs` | Thread-safe `ConcurrentDictionary` implementation. Validates state transitions, computes token usage from session baseline, records blocker hashes, appends history JSONL |
| `GetGoalTool` | `Agent/Tools/GetGoalTool.cs` | `IToolWithContext`. Read-only: returns goal status, objective, tokens, budget |
| `CreateGoalTool` | `Agent/Tools/CreateGoalTool.cs` | `IToolWithContext`. Creates goal with objective + optional budget. Fails if goal exists |
| `UpdateGoalTool` | `Agent/Tools/UpdateGoalTool.cs` | `IToolWithContext`. Sets `complete` or `blocked` only. Includes external verification gate |
| `AgentRuntimeGoalIntegration` | `Agent/Goal/AgentRuntimeGoalIntegration.cs` | Core integration: activation prompt building, continuation evaluation, budget checks, blocker detection, channel gating |
| `GoalPromptTemplates` | `Agent/Goal/GoalPromptTemplates.cs` | Prompt builders for activation (turn start) and check (on stop) |
| `ChatCommandProcessor` | `Core/Pipeline/ChatCommandProcessor.cs` | Built-in `/goal` command handler: start/pause/resume/complete/block/clear/status |
| `GoalTuiExtensions` | `Tui/GoalTuiExtensions.cs` | TUI footer formatting: status line, progress bar |
| `MafAgentRuntime` | `MAFAdapter/MafAgentRuntime.cs` | MAF runtime with parallel Goal integration: activation prompt + auto-continuation loop |

---

## 4. 6-State State Machine

### States

| State | Meaning | Transitions To | Trigger |
|-------|---------|---------------|---------|
| **Active** | Goal is being pursued | Paused, Blocked, BudgetLimited, UsageLimited, Complete | Default; `/goal resume` |
| **Paused** | Operator paused | Active | `/goal pause` |
| **Blocked** | Genuine blocker (3+ turns) | Active | Model or operator marks blocked |
| **BudgetLimited** | Token budget exhausted | Active | Token usage >= budget |
| **UsageLimited** | System usage limit (reserved) | Active | Future system-level limit |
| **Complete** | Goal achieved (terminal) | — | `update_goal(complete)` or `/goal complete` |

### Transition Diagram

```
                    ┌─────────────┐
         ┌─────────│   Active    │◄────────┐
         │         └──────┬──────┘         │
         │                │                │
    /goal pause    budget exceeded    /goal resume
         │                │                │
         ▼                ▼                │
   ┌─────────┐     ┌─────────────┐        │
   │ Paused  │     │BudgetLimited│        │
   └────┬────┘     └──────┬──────┘        │
        │                 │               │
        └─────────────────┘               │
              /goal resume                │
                                          │
        ┌─────────────────────────────────┘
        │
   update_goal blocked              update_goal complete
        │                                │
        ▼                                ▼
   ┌─────────┐                     ┌──────────┐
   │ Blocked │                     │ Complete │
   └────┬────┘                     └──────────┘
        │
   /goal resume
```

### Invalid Transition Guard

The service layer explicitly throws `InvalidOperationException` for disallowed transitions:

```
Active→Active        ✅ (no-op)
Complete→Active      ❌ (terminal state)
Complete→Paused      ❌ (terminal state)
Paused→Blocked       ❌ (must go through Active first)
```

### Blocker Detection Algorithm

Blocker identity is determined by **exact match of the model's assistant-turn text after whitespace normalization**:

1. Normalize: `Trim()` + collapse internal whitespace to single spaces
2. Compute SHA-256 hash of normalized text
3. Compare against `LastBlockerHash` on the `SessionGoal`
4. If match → increment `ConsecutiveBlockerCount`
5. If `ConsecutiveBlockerCount >= 3` → automically transition to `Blocked` status
6. If mismatch → reset counter to 1, update `LastBlockerHash`

This is a **conservative heuristic**: false positives (different blockers matching by coincidence) are considered safer than false negatives (same blocker escaping detection by rephrasing).

### Pursuability

Only `Active` is pursuable — the runtime only auto-continues when the goal is in Active state. All other states (Paused, Blocked, BudgetLimited, UsageLimited, Complete) cause the runtime to return normally.

---

## 5. Token Budget System

### Baseline Mechanism

Token budget is computed from a **session baseline** captured at goal creation time:

```csharp
// At goal creation: record the session's total token count
var tokensAtStart = session.GetTotalTokens();
var goal = goalService.CreateGoal(sessionId, objective, tokenBudget, tokensAtStart);

// At each check: usage = current total - baseline
goalService.UpdateTokenUsage(sessionId, session.GetTotalTokens());
// goal.TokensUsed = sessionTotal - goal.TokensAtStart
```

This ensures the goal does not retroactively bill for tokens consumed before it was created.

### Budget Enforcement

When `goal.IsBudgetExceeded` (`TokensUsed >= TokenBudget`):

1. Goal transitions to `BudgetLimited`
2. The current turn completes normally (returns generated text)
3. Subsequent turns do **not** auto-continue (BudgetLimited is not pursuable)
4. User can `/goal resume` to reactivate (potentially with increased budget)

### CLI Syntax

Parsed via regex in `ChatCommandProcessor.HandleGoalCommandAsync`:

| Input | Parsed Budget |
|-------|--------------|
| `/goal start fix bug +500k` | 500,000 |
| `/goal start write docs spend 2M tokens` | 2,000,000 |
| `/goal start refactor +1.5M` | 1,500,000 |
| `/goal start debug issue` | 0 (unlimited) |

### Default Budget

128K output tokens (matching Claude Code's default).

---

## 6. Model Tools

Three tools are exposed to the LLM via the `ITool` / `IToolWithContext` interface.

### get_goal (read-only)

```json
{
  "name": "get_goal",
  "parameters": {},
  "description": "Read the current session goal: status, objective, token usage, and budget."
}
```

Returns the goal's status, objective text, token usage, and budget information. Implements `IToolWithContext` to derive session ID from the execution context.

### create_goal (user-directed)

```json
{
  "name": "create_goal",
  "parameters": {
    "objective": "What to achieve",
    "token_budget": 500000
  }
}
```

Only succeeds when explicitly directed by the user/system prompt. Rejects with error if a goal already exists for the session.

### update_goal (complete/blocked only)

```json
{
  "name": "update_goal",
  "parameters": {
    "status": "complete|blocked",
    "note": "Optional explanation"
  }
}
```

**Restricted transitions:** The model can **only** set `complete` or `blocked`. Cannot pause, resume, clear, or replace the goal. These are operator-only operations via CLI.

**External verification gate:** Before accepting `update_goal(status="complete")`, the tool performs a plausibility check. If verification fails, the transition is rejected and the goal-check prompt is re-injected. The runtime-level `AgentRuntimeGoalIntegration.EvaluateGoalContinuation` provides additional iteration-based verification.

### Permission Boundaries

| Operation | Model | Operator (/goal) | System |
|-----------|-------|-----------------|--------|
| Read goal | ✅ get_goal | ✅ status | ✅ |
| Create goal | ✅ (user-directed) | ✅ start | — |
| Mark complete | ✅ update_goal | ✅ complete/done | — |
| Mark blocked | ✅ update_goal | ✅ block/blocked | — |
| Pause | ❌ | ✅ pause | — |
| Resume | ❌ | ✅ resume | ✅ (after BudgetLimited check) |
| Clear | ❌ | ✅ clear | ✅ (on /new, /reset) |
| BudgetLimited | — | — | ✅ (auto on budget exceeded) |

---

## 7. Runtime Integration

### 7.1 Native AgentRuntime

The native `AgentRuntime` (`src/OpenClaw.Agent/AgentRuntime.cs`) uses a `for` loop over iterations:

```
for (var i = 0; i < _maxIterations; i++)
{
    // 1. LLM call
    var response = await CallLlmAsync(messages);

    // 2. Extract tool calls
    var toolCalls = GetToolCalls(response);

    // 3. If model stopped
    if (toolCalls.Count == 0)
    {
        // ── Goal continuation check ──
        if (_goalIntegration is not null)
        {
            _goalIntegration.UpdateGoalTokenUsage(session);
            var prompt = _goalIntegration.EvaluateGoalContinuation(
                session, i, _maxIterations, response.Text);

            if (prompt is not null)
            {
                messages.Add(new ChatMessage(ChatRole.System, prompt));
                continue;  // ← Auto-continue
            }
        }

        // Normal stop
        return response.Text;
    }

    // 4. Execute tools
    await ExecuteTools(toolCalls);
    // Loop back to step 1
}
```

**Integration points:**

| Point | Location | Action |
|-------|----------|--------|
| Constructor | `AgentRuntime(...)` | Optional `IGoalService` → create `AgentRuntimeGoalIntegration` |
| After messages build | Before `for` loop | `messages.Insert(1, goalActivationPrompt)` — after system prompts |
| After model stops | Inside loop at `toolCalls.Count == 0` | `EvaluateGoalContinuation()` → inject check prompt or return |

### 7.2 MAF Adapter (MafAgentRuntime)

The Microsoft Agent Framework adapter (`src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`) delegates to `ChatClientAgent.RunAsync()` which handles its own internal tool loop. Goal integration wraps this in an outer iteration loop:

```
for (var i = 0; i < _maxIterations; i++)
{
    // 1. Push execution scope
    using var scope = MafExecutionContextScope.Push(context);

    // 2. MAF agent handles one turn (may include multiple internal LLM+tool cycles)
    var response = await agent.RunAsync(messages, mafSession, options);

    // 3. Extract and record response
    session.History.Add(assistantTurn);

    // 4. ── Goal continuation check ──
    if (_goalIntegration is not null)
    {
        _goalIntegration.UpdateGoalTokenUsage(session);
        var prompt = _goalIntegration.EvaluateGoalContinuation(
            session, i, _maxIterations, text);

        if (prompt is not null)
        {
            messages.Add(new ChatMessage(ChatRole.System, prompt));
            continue;  // ← Auto-continue
        }
    }

    // Normal completion
    return text;
}
```

The **streaming path** follows the same pattern but wraps a `Channel<AgentStreamEvent>` producer in the loop, cleaning up the previous producer before starting the next iteration.

**Key differences from native runtime:**

| Aspect | Native AgentRuntime | MafAgentRuntime |
|--------|-------------------|-----------------|
| Loop | One LLM call per iteration | One `agent.RunAsync()` per iteration (may contain multiple internal LLM calls) |
| Tool loop | Managed by AgentRuntime | Managed internally by ChatClientAgent |
| Scope | No execution scope needed | `MafExecutionContextScope` pushed per iteration |
| Streaming | `RunStreamingAsync` with parallel continue | Producer+Channel wrapped in `for` loop |

---

## 8. CLI Commands

The `/goal` command is a **built-in** command in `ChatCommandProcessor`, not a dynamic registration. It is registered in `BuiltInCommands` alongside `/status`, `/new`, `/model`, etc.

### Command Reference

| Command | Alias | Function |
|---------|-------|----------|
| `/goal` | `/goal status` | Display current goal details |
| `/goal start <obj>` | `/goal set`, `/goal create` | Create a new goal with optional `+Nk`/`+Nm` budget |
| `/goal pause [note]` | — | Pause an active goal |
| `/goal resume [note]` | — | Resume a paused/blocked/budget-limited goal |
| `/goal complete [note]` | `/goal done` | Mark goal as achieved |
| `/goal block [note]` | `/goal blocked` | Mark goal as blocked |
| `/goal clear` | — | Remove the current goal |

### Key Constraints

- **Single goal per session:** Creating a second goal fails with a descriptive error
- **`/new` and `/reset` clear goals:** These commands intentionally start a new session context and clear the active goal
- **Terminal states are immutable:** A `Complete` goal cannot be modified; must clear first
- **Model tools have disjoint capabilities:** CLI supports the full lifecycle; model tools are restricted to read/create/update(complete|blocked)

### Implementation

The `HandleGoalCommandAsync` method in `ChatCommandProcessor`:

1. Parses the subcommand (`start`, `pause`, etc.)
2. For `start`, parses budget from `+Nk`/`+Nm` or `spend N tokens` suffixes via regex
3. Validates state transitions via `IGoalService.UpdateStatus()`
4. Returns human-readable result text

---

## 9. TUI Display

### Footer Format

The TUI footer displays goal status using `GoalStatusExtensions.FormatGoalFooterLine()`:

| Goal State | Footer Text |
|-----------|------------|
| Active (with budget) | `Pursuing goal (12k/50k)` |
| Active (no budget) | `Pursuing goal: fix CI for PR 874...` |
| Paused | `Goal paused (/goal resume)` |
| Blocked | `Goal blocked (/goal resume)` |
| BudgetLimited | `Goal unmet (50k/50k)` |
| UsageLimited | `Goal hit usage limits (/goal resume)` |
| Complete | `Goal achieved (42k)` |

### Progress Bar

When a goal is Active with a token budget, an animated progress bar shows real-time progress:

```
[========>           ] 45% (58k/128k)
```

The bar is rendered by `GoalStatusExtensions.FormatGoalProgressBar()` using:
- 20-character bar width with `=` fill and `>` pointer
- Percentage calculated as `tokensUsed / tokenBudget`
- Falls back to static text if terminal doesn't support Unicode

---

## 10. Prompt Design

### Goal Activation Prompt

Injected once at the **start of each turn** when a goal is active:

> **Active Goal**
> A session-scoped goal is now active with the following objective:
> `<objective>{{objective}}</objective>`
>
> **Your Behavior**
> - Treat the objective itself as your directive. Do NOT pause to ask the user what to do.
> - The system will automatically continue you if you stop before the goal is achieved.
> - When the goal is fully achieved, use the update_goal tool with status='complete'.
> - If you're genuinely blocked after repeated attempts, use update_goal with status='blocked'.
>
> **Completion Audit**
> Before declaring the goal complete, derive concrete requirements from the objective. For each requirement, identify authoritative evidence. Uncertain evidence means NOT achieved.

### Goal Check Prompt

Injected on each **auto-continuation** when the model stops:

> **Goal Check — Continue Working**
> You were working toward this objective: `<objective>{{objective}}</objective>`
>
> 1. REVIEW all work done so far
> 2. DETERMINE whether the objective has been FULLY achieved
> 3. If ACHIEVED → use update_goal tool with status='complete'
> 4. If NOT ACHIEVED → CONTINUE working without asking the user
>
> **Budget**: Used {{tokens_used}} / Budget {{token_budget}} / Remaining {{remaining_tokens}}
> **Fidelity**: Optimize for movement toward the requested end state. Do NOT substitute easier solutions.
> **Blocked Audit**: Only mark blocked after 3+ consecutive turns with the same blocker.
> Iteration: {{iteration}}/{{max_iterations}}

### Injection Strategy

- **Activation prompt:** `messages.Insert(1, ...)` — placed after the system prompt but before user messages, ensuring the model sees the goal directive early
- **Check prompt:** `messages.Add(...)` — appended after the model's response, pushing the model to continue working

---

## 11. External Verification

Before accepting `update_goal(status="complete")`, the runtime performs a plausibility check:

1. **Not mid-tool-execution:** The model's last action must be a user-facing text response
2. **Iteration count >= 2:** Prevents immediate "I'm done" declarations at turn 1
3. **Not mid-tool-chain:** The agent must not be in the middle of executing a tool chain

If verification fails, the transition is rejected and the goal-check prompt is re-injected. This addresses the tension with the premise "models cannot be trusted to self-assess completion": the model self-reports, but the runtime cross-checks plausibility.

---

## 12. Channel Gating

**Decision source:** Outside voice (Codex) finding in CEO review.

Goal auto-continuation only fires in **interactive channels** — sessions where a human operator is present and can observe the auto-continuation. Non-interactive channels (HTTP API, webhook, scheduled tasks) return normally on first stop.

Implementation in `AgentRuntimeGoalIntegration`:

```csharp
private static readonly HashSet<string> InteractiveChannelPrefixes =
    new(StringComparer.OrdinalIgnoreCase) { "cli", "tui", "terminal", "console", "companion" };

private static bool IsInteractiveChannel(string? channelId)
{
    if (string.IsNullOrWhiteSpace(channelId)) return true; // Default to interactive
    return InteractiveChannelPrefixes.Contains(channelId);
}
```

The `Session.ChannelId` field gates the decision. Both `AgentRuntime` and `MafAgentRuntime` apply this gate.

---

## 13. DI Registration

Registered in `CoreServicesExtensions.AddOpenClawCoreServices()`:

```csharp
// Goal service (singleton, with optional history file)
services.AddSingleton<IGoalService>(sp =>
{
    var startup = sp.GetRequiredService<GatewayStartupContext>();
    var logger = sp.GetRequiredService<ILogger<InMemoryGoalService>>();
    var historyPath = !string.IsNullOrEmpty(startup.Config.Memory.StoragePath)
        ? Path.Combine(Path.GetFullPath(startup.Config.Memory.StoragePath), "goal-history.jsonl")
        : null;
    return new InMemoryGoalService(logger, historyPath);
});

// Goal tools (registered alongside other ITool implementations)
services.AddSingleton<ITool, GetGoalTool>();
services.AddSingleton<ITool, CreateGoalTool>();
services.AddSingleton<ITool, UpdateGoalTool>();
```

Goal tools are `ITool` singletons and are automatically available to both `AgentRuntime` (via constructor injection of `IReadOnlyList<ITool>`) and `MafAgentRuntime` (via `MafToolAdapter` wrapping).

---

## 14. NativeAOT Compatibility

The project targets NativeAOT (AOT-compiled .NET). All Goal code follows source-generator-friendly patterns:

| Risk | Mitigation |
|------|-----------|
| JSON serialization reflection | `GoalHistoryRecord` + `[JsonSerializable]` + `GoalJsonContext` partial class |
| Dependency injection | Standard `IServiceProvider` resolution |
| Concurrent access | `ConcurrentDictionary` + atomic operations |
| No `dynamic` or runtime code gen | All types statically known |
| No reflection | Extension methods on enums, not reflection-based dispatch |

The `GoalJsonContext` is a small source-generated serializer context:

```csharp
[JsonSerializable(typeof(GoalHistoryRecord))]
internal sealed partial class GoalJsonContext : JsonSerializerContext;
```

---

## 15. Test Strategy

### Unit Tests (5 classes, 34 tests)

| Test Class | File | Coverage |
|-----------|------|----------|
| `GoalStatusTests` | `GoalStatusTests.cs` | `IsPursuable`, `IsTerminal`, `ToDisplayName` for all 6 states |
| `SessionGoalTests` | `SessionGoalTests.cs` | Normalization, SHA-256 hashing, budget exceeded, max objective length |
| `InMemoryGoalServiceTests` | `InMemoryGoalServiceTests.cs` | CRUD, state transitions (valid/invalid), token tracking, blocker detection (3+ same), multi-session isolation, budget exceeded flow |
| `GoalPromptTemplatesTests` | `GoalPromptTemplatesTests.cs` | Footer format (all states), progress bar, activation prompt, check prompt (with/without budget) |
| `AgentRuntimeGoalIntegrationTests` | `AgentRuntimeGoalIntegrationTests.cs` | Goal activation prompt (active/paused/no-goal), continuation evaluation, **channel gating** (interactive/non-interactive), **budget exceeded** → BudgetLimited, **continuation limit** → auto-pause, **max iterations** guard, token usage tracking, **blocker detection** (3 consecutive) |

### Integration Test Scenarios

1. **Full lifecycle:** start → active → (auto-continue × N) → complete → clear
2. **Budget overage:** start +500k → work to budget → auto-transition to BudgetLimited → resume
3. **Blocked audit:** start → blocker × 3 → Blocked → resume → resolve → complete
4. **Session reset:** `/new` or `/reset` clears goal
5. **Channel gating:** `ChannelId=cli` continues vs `ChannelId=gateway` returns

### Test Framework

- **xUnit** (`.NET` test runner)
- **NSubstitute** (mocking)
- All tests are `[Fact]` (no `[Theory]` needed for current coverage)
- Session instances require `SenderId` (a `required` field on the Session model)

---

## 16. Design Decisions (from Reviews)

Decisions made during the three-stage review process (CEO, Design, Engineering):

| # | Decision | Choice | Source |
|---|----------|--------|--------|
| 1 | Implementation approach | Full upstream parity (Option A) | CEO review |
| 2 | Scope mode | SCOPE_EXPANSION | CEO review |
| 3 | Goal budget vs session budget | Goal budget overrides session budget | Eng review |
| 4 | Channel gating | Gate by `ChannelId` | Outside voice (Codex) |
| 5 | Tool dependency pattern | Constructor injection of `IGoalService` | Eng review |
| 6 | Activation prompt position | After last system/recall injection | Eng review |
| 7 | Tool file organization | One file per tool (3 files) | Eng review |
| 8 | Token accounting reliability | Fallback: 2+ consecutive unreliable reads → BudgetLimited | Outside voice |
| 9 | Auto-continuation detection | Accept the heuristic (matching upstream) | Outside voice |

### Expansions Accepted

1. **Goal History Persistence:** Append completed goals to `~/.openclaw/goal-history.jsonl` (JSONL format)
2. **Goal Fidelity Audit (lightweight):** Non-blocking audit of file changes and test status at completion
3. **TUI Progress Bar:** Animated `tokensUsed/tokenBudget` display in terminal footer

---

## 17. Future Extensions

1. **Distributed Goal Store:** Replace `InMemoryGoalService` with Redis/SQL backend for multi-instance deployments
2. **Review Agent:** Use a separate review agent to validate completion quality at goal-check time
3. **Goal History Browser:** UI to review completed goals across sessions
4. **Goal Templates:** Predefined templates (`/goal fix-bug`, `/goal write-docs`) with tuned audit prompts
5. **Sub-Goal Support:** Decompose large objectives into tracked sub-goals
6. **External Verification Enhancement:** File hash snapshot comparison before accepting "complete"

---

## Appendix A: Verification Checklist

Use this checklist after deploying Goal changes to confirm the feature is working correctly.

### Prerequisites
- [ ] Gateway service has been rebuilt and restarted after code changes
- [ ] `IGoalService` is registered in DI (`CoreServicesExtensions.cs`)
- [ ] `ChatCommandProcessor` has `IGoalService` injected (optional parameter)
- [ ] `AgentRuntime` constructor receives `IGoalService` (via `NativeAgentRuntimeFactory`)
- [ ] At least one runtime (native or MAF) has Goal integration active

### Step 1: Create a Goal
1. Open **webchat** or **CLI/TUI**
2. Send: `/goal start test the goal system +500k`
3. **Expected:** Response shows `Goal created: "test the goal system" with budget 500000`
4. **If fails:** Check that `ChatCommandProcessor.HandleGoalCommandAsync` is reachable and `IGoalService` is not null

### Step 2: Verify Command Processing
1. Send: `/goal`
2. **Expected:** Shows goal details (status: Active, objective, tokens used, budget)
3. Send: `/goal pause`
4. **Expected:** Shows `Goal paused`
5. Send: `/goal resume`
6. **Expected:** Shows `Goal resumed`
7. Send: `/goal clear`
8. **Expected:** Shows `Goal cleared`

### Step 3: Verify Auto-Continuation (Core Functionality)
1. Create a goal: `/goal start explore the codebase structure +500k`
2. Send a follow-up message: `List the top-level directories and their purposes`
3. **Expected:** Model starts working, reads files, then:
   - If model stops before completing → system auto-continues (look for `[goal_check:N]` in history)
   - If model completes → system respects the completion
4. **Check logs for:**
   - `Goal activation prompt injected`
   - `Goal auto-continue iteration N/M`
   - `[goal_check:N] Continue working toward objective...`

### Step 4: Verify Channel Gating
1. In **webchat** (ChannelId = `websocket`): Goal auto-continuation **should work**
2. In **CLI/TUI** (ChannelId = `cli` / `tui`): Goal auto-continuation **should work**
3. Via **HTTP API** (ChannelId = not in interactive list): Goal auto-continuation **should NOT fire**

### Step 5: Verify Budget Enforcement
1. Create a goal with small budget: `/goal start test +100`
2. Send a task that requires multiple tool calls
3. **Expected:** Goal transitions to `BudgetLimited` when tokens consumed, model stops normally
4. Verify: `/goal` shows status `Budget Limited`

### Step 6: Verify Blocker Detection
1. Create a goal for an impossible task: `/goal start delete non-existent file`
2. Let the model try and fail 3 times with the same error
3. **Expected:** After 3 consecutive same-blocker turns, goal transitions to `Blocked`
4. Verify: `/goal` shows status `Blocked`

### Step 7: Verify Session Reset Clears Goal
1. Create a goal: `/goal start temporary task`
2. Send: `/new` or `/reset`
3. Send: `/goal`
4. **Expected:** `No active goal` — session reset cleared the goal

### Log Keywords for Debugging

| Keyword | Meaning | When to Look |
|---------|---------|-------------|
| `Goal activation prompt injected` | Activation prompt was added to messages | Every turn with active goal |
| `Goal auto-continue iteration` | Auto-continuation fired | On model stop with active goal |
| `Goal {SessionId} budget exceeded` | Token budget exhausted | When `TokensUsed >= TokenBudget` |
| `Goal {SessionId} blocked after 3+` | Blocker threshold reached | After 3 same-blocker turns |
| `Goal {SessionId} auto-paused` | Per-turn continuation limit hit | After `MaxContinuationsPerTurn` (10) |
| `Goal auto-continuation skipped` | Non-interactive channel blocked | Non-interactive channel with active goal |

---

## References