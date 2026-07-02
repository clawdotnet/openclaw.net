# Background Session Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Channel-activated OpenClaw.NET sessions continue running in the background after WebChat/WebSocket disconnects, Channel client exits, or Gateway restarts, with identical native `AgentRuntime` and `MafAgentRuntime` behavior.

**Architecture:** Add durable background run state to `Session`, add a shared result-bearing runtime contract implemented by both native and MAF runtimes, and teach `GatewayInboundMessageWorker` to re-enqueue validated `background_auto_continue` messages through the existing `MessagePipeline`. Startup recovery scans persistent stores for runnable background sessions and re-enqueues them with concurrency limits, while WebSocket disconnects affect only socket delivery, not session execution.

**Tech Stack:** C# / .NET, ASP.NET Core Gateway, System.Threading.Channels, System.Text.Json source generation, file and SQLite memory stores, xUnit, NSubstitute.

**Status:** Implemented — 7 implementation commits, 78 passing tests (9 new background session tests), full solution build passes.

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `src/OpenClaw.Core/Models/Session.cs` | Modify | Add `SessionRunState`, `BackgroundRunMetadata`, and background fields to `Session`; add JSON source-gen entries |
| `src/OpenClaw.Core/Models/GatewayConfig.cs` | Modify | Add `BackgroundExecutionConfig` to `GatewayConfig` |
| `src/OpenClaw.Core/Models/Messages.cs` | Modify | Add background continuation metadata to `InboundMessage` and `OutboundMessage` where needed for correlation |
| `src/OpenClaw.Agent/AgentTurnResult.cs` | Create | Shared runtime result DTO and stop reason enum |
| `src/OpenClaw.Agent/IAgentRuntime.cs` | Modify | Add result-bearing runtime method while preserving text-returning compatibility method |
| `src/OpenClaw.Agent/AgentRuntime.cs` | Modify | Implement result-bearing native turn execution and map Goal/budget/failure stop reasons |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs` | Modify | Implement the same result-bearing contract for MAF |
| `src/OpenClaw.Core/Abstractions/IBackgroundSessionStore.cs` | Create | Narrow persistent query interface for runnable background sessions |
| `src/OpenClaw.Core/Memory/FileMemoryStore.cs` | Modify | Implement runnable background session scan for file store |
| `src/OpenClaw.Core/Memory/SqliteMemoryStore.cs` | Modify | Implement runnable background session scan for SQLite store |
| `src/OpenClaw.Gateway/Background/BackgroundExecutionLimiter.cs` | Create | Limit concurrent background turns and validate per-session continuation admission |
| `src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs` | Create | Startup recovery worker that re-enqueues runnable sessions |
| `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs` | Modify | Use result-bearing runtime method, persist state, enqueue continuations, and avoid browser cancellation for background work |
| `src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs` | Modify | Start recovery worker and pass limiter into inbound worker |
| `src/OpenClaw.Gateway/Pipeline/PipelineExtensions.cs` | Modify | Resolve and pass background services into worker startup |
| `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs` | Modify | Register background execution services |
| `src/OpenClaw.Channels/WebSocketChannel.cs` | Modify | Ensure disconnected WebSocket delivery is non-fatal and reconnect loads persisted state through existing session APIs |
| `src/OpenClaw.Tests/BackgroundSessionModelTests.cs` | Create | Model/config/json tests |
| `src/OpenClaw.Tests/AgentRuntimeBackgroundResultTests.cs` | Create | Native runtime result tests |
| `src/OpenClaw.Tests/MafBackgroundResultTests.cs` | Create | MAF parity tests |
| `src/OpenClaw.Tests/BackgroundSessionStoreTests.cs` | Create | File/SQLite runnable scan tests |
| `src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs` | Create | Gateway self-requeue tests |
| `src/OpenClaw.Tests/WebSocketBackgroundSessionTests.cs` | Create | WebSocket disconnect/reconnect behavior tests |

---

## Task 1: Core Session State, Config, and JSON Coverage

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs`
- Test: `src/OpenClaw.Tests/BackgroundSessionModelTests.cs`

- [ ] **Step 1: Write failing model/config tests**

Create `src/OpenClaw.Tests/BackgroundSessionModelTests.cs`:

```csharp
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed class BackgroundSessionModelTests
{
    [Fact]
    public void Session_Defaults_ToIdleBackgroundState()
    {
        var session = new Session
        {
            Id = "websocket:user-1",
            ChannelId = "websocket",
            SenderId = "user-1"
        };

        Assert.Equal(SessionRunState.Idle, session.RunState);
        Assert.Null(session.BackgroundRun);
    }

    [Fact]
    public void Session_BackgroundRun_RoundTripsThroughSourceGeneratedJson()
    {
        var session = new Session
        {
            Id = "telegram:42",
            ChannelId = "telegram",
            SenderId = "42",
            RunState = SessionRunState.Continuing,
            BackgroundRun = new BackgroundRunMetadata
            {
                RunId = "run_abc",
                Objective = "Fix failing tests",
                StartedAtUtc = DateTimeOffset.Parse("2026-07-02T10:00:00Z"),
                LastContinuedAtUtc = DateTimeOffset.Parse("2026-07-02T10:05:00Z"),
                ContinuationCount = 3,
                ContinuationSequence = 7,
                TokenBudget = 128_000,
                MaxContinuationTurns = 200,
                LastCheckpointId = "ckpt_1",
                LastStopReason = "batch_limit"
            }
        };

        var json = JsonSerializer.Serialize(session, CoreJsonContext.Default.Session);
        var loaded = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);

        Assert.NotNull(loaded);
        Assert.Equal(SessionRunState.Continuing, loaded!.RunState);
        Assert.NotNull(loaded.BackgroundRun);
        Assert.Equal("run_abc", loaded.BackgroundRun!.RunId);
        Assert.Equal(7, loaded.BackgroundRun.ContinuationSequence);
    }

    [Fact]
    public void LegacySessionJson_DeserializesAsIdle()
    {
        const string json = """
        {
          "Id": "websocket:user-1",
          "ChannelId": "websocket",
          "SenderId": "user-1",
          "CreatedAt": "2026-07-02T10:00:00Z",
          "LastActiveAt": "2026-07-02T10:00:00Z",
          "History": [],
          "State": 0
        }
        """;

        var loaded = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);

        Assert.NotNull(loaded);
        Assert.Equal(SessionRunState.Idle, loaded!.RunState);
        Assert.Null(loaded.BackgroundRun);
    }

    [Fact]
    public void GatewayConfig_BackgroundExecution_DefaultsToEnabledAndAutoResume()
    {
        var config = new GatewayConfig();

        Assert.True(config.BackgroundExecution.Enabled);
        Assert.True(config.BackgroundExecution.AutoResumeOnStartup);
        Assert.Equal(3, config.BackgroundExecution.MaxConcurrentBackgroundTurns);
        Assert.Equal(10, config.BackgroundExecution.MaxIterationsPerBatch);
        Assert.Equal(128_000, config.BackgroundExecution.DefaultTokenBudget);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter BackgroundSessionModelTests`

Expected: FAIL with compile errors for missing `SessionRunState`, `BackgroundRunMetadata`, and `GatewayConfig.BackgroundExecution`.

- [ ] **Step 3: Add session run state and metadata**

In `src/OpenClaw.Core/Models/Session.cs`, add properties after `Session.State`:

```csharp
    public SessionRunState RunState { get; set; } = SessionRunState.Idle;
    public BackgroundRunMetadata? BackgroundRun { get; set; }
```

Add these types after `SessionState`:

```csharp
public enum SessionRunState : byte
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
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastContinuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastNotificationAtUtc { get; set; }
    public int ContinuationCount { get; set; }
    public int ContinuationSequence { get; set; }
    public int ConsecutiveNoProgressCount { get; set; }
    public long ToolCallCount { get; set; }
    public long TokenBudget { get; set; }
    public int MaxContinuationTurns { get; set; }
    public string? LastCheckpointId { get; set; }
    public string? LastStopReason { get; set; }
}
```

Add JSON source-generation attributes near the existing `Session` entries:

```csharp
[JsonSerializable(typeof(SessionRunState))]
[JsonSerializable(typeof(BackgroundRunMetadata))]
```

- [ ] **Step 4: Add background execution config**

In `src/OpenClaw.Core/Models/GatewayConfig.cs`, add this property near the session settings:

```csharp
    public BackgroundExecutionConfig BackgroundExecution { get; set; } = new();
```

Add this class after `GatewayConfig`:

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

Add `BackgroundExecutionConfig` to `CoreJsonContext` attributes in `Session.cs` near `GatewayConfig`:

```csharp
[JsonSerializable(typeof(BackgroundExecutionConfig))]
```

- [ ] **Step 5: Run tests and verify they pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter BackgroundSessionModelTests`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Core/Models/GatewayConfig.cs src/OpenClaw.Tests/BackgroundSessionModelTests.cs
git commit -m "feat(sessions): add background run state"
```

---

## Task 2: Shared Runtime Result Contract

**Files:**
- Create: `src/OpenClaw.Agent/AgentTurnResult.cs`
- Modify: `src/OpenClaw.Agent/IAgentRuntime.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeBackgroundResultTests.cs`

- [ ] **Step 1: Write a compile-failing contract test**

Create `src/OpenClaw.Tests/AgentRuntimeBackgroundResultTests.cs` with this first test:

```csharp
using OpenClaw.Agent;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed partial class AgentRuntimeBackgroundResultTests
{
    [Fact]
    public void AgentTurnResult_CanRepresentContinuationStopReason()
    {
        var result = new AgentTurnResult
        {
            Text = "working",
            ShouldContinue = true,
            StopReason = AgentTurnStopReason.GoalContinuationRequired,
            ContinuePrompt = "continue checking the goal"
        };

        Assert.True(result.ShouldContinue);
        Assert.Equal(AgentTurnStopReason.GoalContinuationRequired, result.StopReason);
        Assert.Equal("continue checking the goal", result.ContinuePrompt);
    }
}
```

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentRuntimeBackgroundResultTests`

Expected: FAIL with compile errors for missing `AgentTurnResult` and `AgentTurnStopReason`.

- [ ] **Step 3: Add result DTO and stop reason enum**

Create `src/OpenClaw.Agent/AgentTurnResult.cs`:

```csharp
namespace OpenClaw.Agent;

public sealed record AgentTurnResult
{
    public required string Text { get; init; }
    public bool ShouldContinue { get; init; }
    public AgentTurnStopReason StopReason { get; init; } = AgentTurnStopReason.Completed;
    public string? ContinuePrompt { get; init; }

    public static AgentTurnResult Completed(string text)
        => new() { Text = text, ShouldContinue = false, StopReason = AgentTurnStopReason.Completed };
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

- [ ] **Step 4: Add result-bearing method to `IAgentRuntime`**

Modify `src/OpenClaw.Agent/IAgentRuntime.cs` by adding this method after `RunAsync`:

```csharp
    Task<AgentTurnResult> RunTurnAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null);
```

Add a default compatibility wrapper only if the project targets a C# language version that permits default interface methods. If not, each implementation must implement both methods explicitly.

- [ ] **Step 5: Run test and verify current implementers fail compile**

Run: `dotnet build OpenClaw.Net.slnx`

Expected: FAIL because `AgentRuntime`, `MafAgentRuntime`, and test fakes implementing `IAgentRuntime` do not yet implement `RunTurnAsync`.

- [ ] **Step 6: Commit only if build failure matches expected missing implementations**

Do not commit a non-compiling intermediate state. Continue directly to Task 3 and Task 4 before committing this contract change.

---

## Task 3: Native `AgentRuntime` Result-Bearing Execution

**Files:**
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: test fakes implementing `IAgentRuntime` as needed under `src/OpenClaw.Tests/`
- Test: extend `src/OpenClaw.Tests/AgentRuntimeBackgroundResultTests.cs`

- [ ] **Step 1: Add native runtime tests for budget stop result**

Append to `AgentRuntimeBackgroundResultTests`:

```csharp
    [Fact]
    public async Task NativeRunTurnAsync_ReturnsBudgetLimited_WhenSessionTokenBudgetAlreadyExceeded()
    {
        var runtime = AgentRuntimeTestFactory.Create(sessionTokenBudget: 1);
        var session = new Session
        {
            Id = "websocket:user-1",
            ChannelId = "websocket",
            SenderId = "user-1"
        };
        session.AddTokenUsage(1, 1);

        var result = await runtime.RunTurnAsync(session, "continue", TestContext.Current.CancellationToken);

        Assert.False(result.ShouldContinue);
        Assert.Equal(AgentTurnStopReason.BudgetLimited, result.StopReason);
        Assert.Contains("token limit", result.Text, StringComparison.OrdinalIgnoreCase);
    }
```

If `AgentRuntimeTestFactory` does not exist, create a private helper in this test file that constructs `AgentRuntime` with the same fake `IChatClient` pattern already used in `AgentRuntimeTests.cs`.

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter NativeRunTurnAsync_ReturnsBudgetLimited_WhenSessionTokenBudgetAlreadyExceeded`

Expected: FAIL because `AgentRuntime.RunTurnAsync` is not implemented.

- [ ] **Step 3: Implement `RunTurnAsync` wrapper in native runtime**

In `src/OpenClaw.Agent/AgentRuntime.cs`, replace the public `RunAsync` body with a wrapper and move current logic into `RunTurnAsync`:

```csharp
    public async Task<string> RunAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        var result = await RunTurnAsync(session, userMessage, ct, approvalCallback, responseSchema, correlationId);
        return result.Text;
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        // Move the existing RunAsync implementation here.
    }
```

Update each current `return "...";` inside the moved method:

```csharp
return AgentTurnResult.Completed("Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.");
```

For token-budget returns, use:

```csharp
return new AgentTurnResult
{
    Text = "You've reached the token limit for this session. Please start a new conversation.",
    ShouldContinue = false,
    StopReason = AgentTurnStopReason.BudgetLimited
};
```

- [ ] **Step 4: Return continuation metadata from Goal continuation checks**

Inside the no-tool-calls branch where `_goalIntegration.EvaluateGoalContinuation(...)` returns a prompt, keep the existing in-turn `continue` behavior until the batch limit is reached. At the max-iteration exit, return:

```csharp
return new AgentTurnResult
{
    Text = "I've reached the maximum number of iterations and will continue in the background.",
    ShouldContinue = true,
    StopReason = AgentTurnStopReason.BatchLimitReached,
    ContinuePrompt = "Continue working toward the active goal."
};
```

For normal final response, return:

```csharp
return AgentTurnResult.Completed(text);
```

- [ ] **Step 5: Update test fakes**

For every private test fake implementing `IAgentRuntime`, add:

```csharp
public Task<AgentTurnResult> RunTurnAsync(
    Session session,
    string userMessage,
    CancellationToken ct,
    ToolApprovalCallback? approvalCallback = null,
    JsonElement? responseSchema = null,
    string? correlationId = null)
    => Task.FromResult(AgentTurnResult.Completed("ok"));
```

If a fake already returns a configurable response from `RunAsync`, return that same response in `AgentTurnResult.Completed(response)`.

- [ ] **Step 6: Run native result tests and build**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentRuntimeBackgroundResultTests`

Expected: PASS.

Run: `dotnet build OpenClaw.Net.slnx`

Expected: PASS for native runtime and updated test fakes, except MAF may still fail until Task 4 is complete.

---

## Task 4: `MafAgentRuntime` Result Parity

**Files:**
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Test: `src/OpenClaw.Tests/MafBackgroundResultTests.cs`

- [ ] **Step 1: Write MAF budget parity test**

Create `src/OpenClaw.Tests/MafBackgroundResultTests.cs`:

```csharp
using OpenClaw.Agent;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed class MafBackgroundResultTests
{
    [Fact]
    public async Task MafRunTurnAsync_ReturnsBudgetLimited_WhenSessionTokenBudgetAlreadyExceeded()
    {
        var runtime = MafRuntimeTestFactory.Create(sessionTokenBudget: 1);
        var session = new Session
        {
            Id = "websocket:user-1",
            ChannelId = "websocket",
            SenderId = "user-1"
        };
        session.AddTokenUsage(1, 1);

        var result = await runtime.RunTurnAsync(session, "continue", TestContext.Current.CancellationToken);

        Assert.False(result.ShouldContinue);
        Assert.Equal(AgentTurnStopReason.BudgetLimited, result.StopReason);
        Assert.Contains("token limit", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}
```

If `MafRuntimeTestFactory` does not exist, copy the minimal runtime construction helper from existing `MafAdapterTests.cs` into this test file as a private static helper.

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter MafRunTurnAsync_ReturnsBudgetLimited_WhenSessionTokenBudgetAlreadyExceeded`

Expected: FAIL because `MafAgentRuntime.RunTurnAsync` is not implemented.

- [ ] **Step 3: Implement MAF `RunTurnAsync` wrapper**

In `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`, change public `RunAsync` to:

```csharp
    public async Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        var result = await RunTurnAsync(session, userMessage, ct, approvalCallback, responseSchema, correlationId);
        return result.Text;
    }
```

Add `RunTurnAsync` with the moved current MAF implementation:

```csharp
    public async Task<AgentTurnResult> RunTurnAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null)
    {
        // Move the existing MAF RunAsync implementation here.
    }
```

Map current return sites to `AgentTurnResult` exactly as native runtime does:

```csharp
return new AgentTurnResult
{
    Text = contractBudgetMessage,
    ShouldContinue = false,
    StopReason = AgentTurnStopReason.BudgetLimited
};
```

For the max-iteration path, return:

```csharp
return new AgentTurnResult
{
    Text = "I've reached the maximum number of iterations and will continue in the background.",
    ShouldContinue = true,
    StopReason = AgentTurnStopReason.BatchLimitReached,
    ContinuePrompt = "Continue working toward the active goal."
};
```

- [ ] **Step 4: Keep MAF sidecar persistence before continuation result**

In the MAF max-iteration path, ensure this remains before returning `ShouldContinue = true`:

```csharp
await _sessionStateStore.SaveAsync(agent, session, mafSession, ct);
```

This preserves MAF sidecar state before Gateway re-enqueues a continuation.

- [ ] **Step 5: Run native and MAF parity tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "AgentRuntimeBackgroundResultTests|MafBackgroundResultTests"`

Expected: PASS.

Run: `dotnet build OpenClaw.Net.slnx`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Agent/AgentTurnResult.cs src/OpenClaw.Agent/IAgentRuntime.cs src/OpenClaw.Agent/AgentRuntime.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs src/OpenClaw.Tests/AgentRuntimeBackgroundResultTests.cs src/OpenClaw.Tests/MafBackgroundResultTests.cs src/OpenClaw.Tests
git commit -m "feat(runtime): add background turn result contract"
```

---

## Task 5: Runnable Background Session Store Query

**Files:**
- Create: `src/OpenClaw.Core/Abstractions/IBackgroundSessionStore.cs`
- Modify: `src/OpenClaw.Core/Memory/FileMemoryStore.cs`
- Modify: `src/OpenClaw.Core/Memory/SqliteMemoryStore.cs`
- Test: `src/OpenClaw.Tests/BackgroundSessionStoreTests.cs`

- [ ] **Step 1: Write file and SQLite scan tests**

Create `src/OpenClaw.Tests/BackgroundSessionStoreTests.cs`:

```csharp
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed class BackgroundSessionStoreTests
{
    [Fact]
    public async Task FileStore_ListsOnlyRunnableBackgroundSessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-bg-file-" + Guid.NewGuid().ToString("N"));
        await using var store = new FileMemoryStore(dir);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    [Fact]
    public async Task SqliteStore_ListsOnlyRunnableBackgroundSessions()
    {
        var db = Path.Combine(Path.GetTempPath(), "openclaw-bg-" + Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteMemoryStore(db, enableFts: false);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    private static async Task SeedSessionsAsync(IMemoryStore store, CancellationToken ct)
    {
        await store.SaveSessionAsync(NewSession("websocket:runnable", SessionRunState.Continuing), ct);
        await store.SaveSessionAsync(NewSession("websocket:paused", SessionRunState.Paused), ct);
        await store.SaveSessionAsync(NewSession("websocket:done", SessionRunState.Completed), ct);
        await store.SaveSessionAsync(NewSession("websocket:idle", SessionRunState.Idle), ct);
    }

    private static Session NewSession(string id, SessionRunState state)
        => new()
        {
            Id = id,
            ChannelId = "websocket",
            SenderId = id.Split(':')[1],
            RunState = state,
            BackgroundRun = state is SessionRunState.Running or SessionRunState.Continuing
                ? new BackgroundRunMetadata
                {
                    RunId = "run_" + id.Split(':')[1],
                    Objective = "Fix tests",
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    LastContinuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    TokenBudget = 128_000,
                    MaxContinuationTurns = 200
                }
                : null
        };
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter BackgroundSessionStoreTests`

Expected: FAIL with compile errors for missing `IBackgroundSessionStore`.

- [ ] **Step 3: Add narrow store interface**

Create `src/OpenClaw.Core/Abstractions/IBackgroundSessionStore.cs`:

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IBackgroundSessionStore
{
    ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct);
}
```

- [ ] **Step 4: Implement file store scan**

In `FileMemoryStore`, add `IBackgroundSessionStore` to the implemented interfaces and add:

```csharp
public async ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct)
{
    limit = Math.Clamp(limit, 1, 500);
    if (!Directory.Exists(_sessionsPath))
        return [];

    var sessions = new List<Session>();
    foreach (var file in Directory.EnumerateFiles(_sessionsPath, "*.json"))
    {
        ct.ThrowIfCancellationRequested();
        await using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        var session = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.Session, ct);
        if (IsBackgroundRunnable(session))
            sessions.Add(session!);

        if (sessions.Count >= limit)
            break;
    }

    return sessions
        .OrderBy(static s => s.BackgroundRun?.LastContinuedAtUtc ?? s.LastActiveAt)
        .ToArray();
}

private static bool IsBackgroundRunnable(Session? session)
    => session is
    {
        BackgroundRun: not null,
        RunState: SessionRunState.Running or SessionRunState.Continuing
    };
```

- [ ] **Step 5: Implement SQLite scan**

In `SqliteMemoryStore`, add `IBackgroundSessionStore` to the implemented interfaces and add:

```csharp
public async ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct)
{
    limit = Math.Clamp(limit, 1, 500);
    var sessions = new List<Session>();

    await using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync(ct);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT json FROM sessions ORDER BY updated_at ASC LIMIT $limit;";
    cmd.Parameters.AddWithValue("$limit", Math.Max(limit * 4, limit));

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        ct.ThrowIfCancellationRequested();
        var json = reader.GetString(0);
        var session = JsonSerializer.Deserialize(json, CoreJsonContext.Default.Session);
        if (session is { BackgroundRun: not null, RunState: SessionRunState.Running or SessionRunState.Continuing })
            sessions.Add(session);

        if (sessions.Count >= limit)
            break;
    }

    return sessions
        .OrderBy(static s => s.BackgroundRun?.LastContinuedAtUtc ?? s.LastActiveAt)
        .ToArray();
}
```

- [ ] **Step 6: Run store tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter BackgroundSessionStoreTests`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Core/Abstractions/IBackgroundSessionStore.cs src/OpenClaw.Core/Memory/FileMemoryStore.cs src/OpenClaw.Core/Memory/SqliteMemoryStore.cs src/OpenClaw.Tests/BackgroundSessionStoreTests.cs
git commit -m "feat(sessions): list runnable background sessions"
```

---

## Task 6: Gateway Self-Requeue Continuation Loop

**Files:**
- Modify: `src/OpenClaw.Core/Models/Messages.cs`
- Create: `src/OpenClaw.Gateway/Background/BackgroundExecutionLimiter.cs`
- Modify: `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs`
- Modify: `src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs`
- Modify: `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`
- Test: `src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs`

- [ ] **Step 1: Write continuation metadata test**

Create `src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs`:

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed class GatewayBackgroundContinuationTests
{
    [Fact]
    public void BackgroundContinuationMessage_CarriesRunIdAndSequence()
    {
        var message = new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "user-1",
            SessionId = "websocket:user-1",
            Text = "Continue working toward the active goal.",
            Type = BackgroundMessageTypes.AutoContinue,
            IsSystem = true,
            BackgroundRunId = "run_1",
            BackgroundContinuationSequence = 2
        };

        Assert.Equal(BackgroundMessageTypes.AutoContinue, message.Type);
        Assert.True(message.IsSystem);
        Assert.Equal("run_1", message.BackgroundRunId);
        Assert.Equal(2, message.BackgroundContinuationSequence);
    }
}
```

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter BackgroundContinuationMessage_CarriesRunIdAndSequence`

Expected: FAIL with compile errors for missing fields and `BackgroundMessageTypes`.

- [ ] **Step 3: Add background message metadata**

In `src/OpenClaw.Core/Models/Messages.cs`, add to `InboundMessage`:

```csharp
    public string? BackgroundRunId { get; init; }
    public int? BackgroundContinuationSequence { get; init; }
```

Add to `OutboundMessage`:

```csharp
    public string? BackgroundRunId { get; init; }
```

Add after `OutboundMessage`:

```csharp
public static class BackgroundMessageTypes
{
    public const string AutoContinue = "background_auto_continue";
    public const string AutoResume = "background_auto_resume";
}
```

- [ ] **Step 4: Add limiter**

Create `src/OpenClaw.Gateway/Background/BackgroundExecutionLimiter.cs`:

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Background;

internal sealed class BackgroundExecutionLimiter : IAsyncDisposable
{
    private readonly SemaphoreSlim _permits;

    public BackgroundExecutionLimiter(GatewayConfig config)
    {
        var max = Math.Max(1, config.BackgroundExecution.MaxConcurrentBackgroundTurns);
        _permits = new SemaphoreSlim(max, max);
    }

    public async ValueTask<Releaser?> TryAcquireAsync(InboundMessage message, CancellationToken ct)
    {
        if (!IsBackgroundContinuation(message))
            return new Releaser(null);

        if (!await _permits.WaitAsync(TimeSpan.Zero, ct))
            return null;

        return new Releaser(_permits);
    }

    public static bool IsBackgroundContinuation(InboundMessage message)
        => string.Equals(message.Type, BackgroundMessageTypes.AutoContinue, StringComparison.Ordinal)
        || string.Equals(message.Type, BackgroundMessageTypes.AutoResume, StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        _permits.Dispose();
        return ValueTask.CompletedTask;
    }

    public readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim? semaphore) => _semaphore = semaphore;

        public void Dispose() => _semaphore?.Release();
    }
}
```

- [ ] **Step 5: Register limiter**

In `CoreServicesExtensions.cs`, register:

```csharp
services.AddSingleton<BackgroundExecutionLimiter>();
```

Add `using OpenClaw.Gateway.Background;`.

- [ ] **Step 6: Thread limiter into workers**

Update `GatewayWorkers.Start` and `GatewayInboundMessageWorker.Start` signatures to accept `BackgroundExecutionLimiter backgroundLimiter`.

In `PipelineExtensions.StartWorkers`, pass:

```csharp
app.Services.GetRequiredService<BackgroundExecutionLimiter>(),
```

- [ ] **Step 7: Validate background message before runtime call**

In `GatewayInboundMessageWorker`, before calling `agentRuntime.RunTurnAsync`, add:

```csharp
if (BackgroundExecutionLimiter.IsBackgroundContinuation(msg))
{
    if (session.BackgroundRun is null ||
        !string.Equals(session.BackgroundRun.RunId, msg.BackgroundRunId, StringComparison.Ordinal) ||
        session.BackgroundRun.ContinuationSequence != msg.BackgroundContinuationSequence)
    {
        logger.LogInformation("Dropping stale background continuation for session {SessionId}", session.Id);
        continue;
    }

    if (session.RunState is not (SessionRunState.Running or SessionRunState.Continuing))
    {
        logger.LogInformation("Dropping non-runnable background continuation for session {SessionId} state {RunState}", session.Id, session.RunState);
        continue;
    }
}
```

- [ ] **Step 8: Use `RunTurnAsync` and enqueue continuation**

Replace the non-streaming `agentRuntime.RunAsync` call with:

```csharp
AgentTurnResult turnResult;
try
{
    turnResult = await agentRuntime.RunTurnAsync(session, messageText, processingCt, approvalCallback: approvalCallback);
}
finally
{
    session.ResponseMode = originalResponseMode;
}

responseText = turnResult.Text;
```

After `sessionManager.PersistAsync(...)`, add:

```csharp
if (turnResult.ShouldContinue && config.BackgroundExecution.Enabled && session.BackgroundRun is not null)
{
    session.RunState = SessionRunState.Continuing;
    session.BackgroundRun.ContinuationCount++;
    session.BackgroundRun.ContinuationSequence++;
    session.BackgroundRun.LastContinuedAtUtc = DateTimeOffset.UtcNow;
    session.BackgroundRun.LastStopReason = turnResult.StopReason.ToString();

    await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);

    await pipeline.InboundWriter.WriteAsync(new InboundMessage
    {
        ChannelId = msg.ChannelId,
        SenderId = msg.SenderId,
        AccountId = msg.AccountId,
        SessionId = session.Id,
        Text = turnResult.ContinuePrompt ?? "Continue working toward the active goal.",
        Type = BackgroundMessageTypes.AutoContinue,
        IsSystem = true,
        BackgroundRunId = session.BackgroundRun.RunId,
        BackgroundContinuationSequence = session.BackgroundRun.ContinuationSequence
    }, lifetime.ApplicationStopping);
}
```

- [ ] **Step 9: Acquire background permit around runtime execution**

Before runtime execution in `GatewayInboundMessageWorker`, add:

```csharp
await using var backgroundPermit = await backgroundLimiter.TryAcquireAsync(msg, processingCt);
if (BackgroundExecutionLimiter.IsBackgroundContinuation(msg) && backgroundPermit is null)
{
    await pipeline.InboundWriter.WriteAsync(msg, lifetime.ApplicationStopping);
    continue;
}
```

If `await using` is not accepted for the readonly struct, make `Releaser` implement `IAsyncDisposable` or use a nullable `IDisposable` variable and dispose it in a `finally` block.

- [ ] **Step 10: Run targeted tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter GatewayBackgroundContinuationTests`

Expected: PASS.

Run: `dotnet build OpenClaw.Net.slnx`

Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add src/OpenClaw.Core/Models/Messages.cs src/OpenClaw.Gateway/Background/BackgroundExecutionLimiter.cs src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs src/OpenClaw.Gateway/Pipeline/PipelineExtensions.cs src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs
git commit -m "feat(gateway): requeue background session continuations"
```

---

## Task 7: WebChat/WebSocket Disconnect Semantics

**Files:**
- Modify: `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs`
- Modify: `src/OpenClaw.Channels/WebSocketChannel.cs`
- Test: `src/OpenClaw.Tests/WebSocketBackgroundSessionTests.cs`

- [ ] **Step 1: Write disconnect cancellation test**

Create `src/OpenClaw.Tests/WebSocketBackgroundSessionTests.cs`:

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public sealed class WebSocketBackgroundSessionTests
{
    [Fact]
    public void WebSocketInboundCancellation_IsNotSessionLifetimeForBackgroundContinuation()
    {
        using var browserCts = new CancellationTokenSource();
        browserCts.Cancel();

        var message = new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "user-1",
            SessionId = "websocket:user-1",
            Text = "Continue working toward the active goal.",
            Type = BackgroundMessageTypes.AutoContinue,
            IsSystem = true,
            RequestCancellation = browserCts.Token,
            BackgroundRunId = "run_1",
            BackgroundContinuationSequence = 1
        };

        var effective = GatewayBackgroundCancellation.ResolveProcessingCancellation(message, CancellationToken.None);

        Assert.False(effective.IsCancellationRequested);
    }
}
```

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter WebSocketInboundCancellation_IsNotSessionLifetimeForBackgroundContinuation`

Expected: FAIL because `GatewayBackgroundCancellation` does not exist.

- [ ] **Step 3: Add cancellation helper**

Create `src/OpenClaw.Gateway/Background/GatewayBackgroundCancellation.cs`:

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Background;

internal static class GatewayBackgroundCancellation
{
    public static CancellationToken ResolveProcessingCancellation(InboundMessage message, CancellationToken applicationStopping)
        => BackgroundExecutionLimiter.IsBackgroundContinuation(message)
            ? applicationStopping
            : message.RequestCancellation.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(message.RequestCancellation, applicationStopping).Token
                : applicationStopping;
}
```

If the linked token source lifetime is needed outside the helper, return a small disposable holder instead of a bare token:

```csharp
internal sealed class ProcessingCancellation : IDisposable
{
    private readonly CancellationTokenSource? _linked;
    public ProcessingCancellation(CancellationToken token, CancellationTokenSource? linked)
    {
        Token = token;
        _linked = linked;
    }
    public CancellationToken Token { get; }
    public void Dispose() => _linked?.Dispose();
}
```

- [ ] **Step 4: Use helper in inbound worker**

Replace creation of `processingCts` / `processingCt` with a helper that uses application lifetime only for background continuation messages. The behavior must be:

```csharp
using var processingLease = CreateProcessingLease(msg, lifetime.ApplicationStopping);
var processingCt = processingLease.Token;
```

`CreateProcessingLease` should return `lifetime.ApplicationStopping` for `background_auto_continue` and `background_auto_resume`, even if `msg.RequestCancellation` was canceled by a WebSocket disconnect.

- [ ] **Step 5: Make WebSocket outbound failure non-fatal**

In `WebSocketChannel.SendAsync` and `SendStreamEventAsync`, ensure disconnected clients result in a logged best-effort failure rather than an exception that bubbles back into session state. If the current implementation already swallows disconnected sends, add a test assertion around the existing behavior and leave production code unchanged.

- [ ] **Step 6: Run WebSocket tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter WebSocketBackgroundSessionTests`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Gateway/Background/GatewayBackgroundCancellation.cs src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs src/OpenClaw.Channels/WebSocketChannel.cs src/OpenClaw.Tests/WebSocketBackgroundSessionTests.cs
git commit -m "fix(websocket): keep background sessions alive after disconnect"
```

---

## Task 8: Startup Recovery and Lifecycle Notifications

**Files:**
- Create: `src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs`
- Modify: `src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs`
- Modify: `src/OpenClaw.Gateway/Pipeline/PipelineExtensions.cs`
- Test: extend `src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs`

- [ ] **Step 1: Add recovery test with fake store and pipeline**

Append to `GatewayBackgroundContinuationTests`:

```csharp
    [Fact]
    public async Task RecoveryWorker_EnqueuesRunnableBackgroundSession()
    {
        var store = new FakeBackgroundSessionStore([
            new Session
            {
                Id = "websocket:user-1",
                ChannelId = "websocket",
                SenderId = "user-1",
                RunState = SessionRunState.Continuing,
                BackgroundRun = new BackgroundRunMetadata
                {
                    RunId = "run_1",
                    Objective = "Fix tests",
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    LastContinuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    ContinuationSequence = 4,
                    TokenBudget = 128_000,
                    MaxContinuationTurns = 200
                }
            }
        ]);
        var pipeline = new OpenClaw.Core.Pipeline.MessagePipeline();
        var config = new GatewayConfig();
        var worker = new BackgroundSessionRecoveryWorker(store, pipeline, config, Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundSessionRecoveryWorker>.Instance);

        await worker.RecoverOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(pipeline.InboundReader.TryRead(out var message));
        Assert.Equal(BackgroundMessageTypes.AutoResume, message.Type);
        Assert.Equal("websocket:user-1", message.SessionId);
        Assert.Equal("run_1", message.BackgroundRunId);
        Assert.Equal(4, message.BackgroundContinuationSequence);
    }

    private sealed class FakeBackgroundSessionStore(IReadOnlyList<Session> sessions) : IBackgroundSessionStore
    {
        public ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct)
            => ValueTask.FromResult(sessions);
    }
```

- [ ] **Step 2: Run test and verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter RecoveryWorker_EnqueuesRunnableBackgroundSession`

Expected: FAIL because `BackgroundSessionRecoveryWorker` does not exist.

- [ ] **Step 3: Create recovery worker**

Create `src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Background;

internal sealed class BackgroundSessionRecoveryWorker
{
    private readonly IBackgroundSessionStore? _store;
    private readonly MessagePipeline _pipeline;
    private readonly GatewayConfig _config;
    private readonly ILogger<BackgroundSessionRecoveryWorker> _logger;

    public BackgroundSessionRecoveryWorker(
        IBackgroundSessionStore? store,
        MessagePipeline pipeline,
        GatewayConfig config,
        ILogger<BackgroundSessionRecoveryWorker> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _config = config;
        _logger = logger;
    }

    public async Task RecoverOnceAsync(CancellationToken ct)
    {
        if (!_config.BackgroundExecution.Enabled || !_config.BackgroundExecution.AutoResumeOnStartup)
            return;

        if (_store is null)
        {
            _logger.LogWarning("Background session recovery is enabled, but the configured memory store does not support runnable session listing.");
            return;
        }

        var limit = Math.Max(1, _config.BackgroundExecution.AutoResumeMaxConcurrent * 20);
        var sessions = await _store.ListBackgroundRunnableSessionsAsync(limit, ct);
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (session.BackgroundRun is null)
                continue;

            await _pipeline.InboundWriter.WriteAsync(new InboundMessage
            {
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                SessionId = session.Id,
                Text = "Resume the active background goal from the latest checkpoint.",
                Type = BackgroundMessageTypes.AutoResume,
                IsSystem = true,
                BackgroundRunId = session.BackgroundRun.RunId,
                BackgroundContinuationSequence = session.BackgroundRun.ContinuationSequence
            }, ct);

            if (_config.BackgroundExecution.AutoResumeStaggerSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_config.BackgroundExecution.AutoResumeStaggerSeconds), ct);
        }
    }
}
```

- [ ] **Step 4: Start recovery during worker startup**

In `GatewayWorkers.Start`, after `GatewaySessionCleanupWorker` starts, add:

```csharp
var backgroundStore = sessionManager.Store as IBackgroundSessionStore;
new BackgroundSessionRecoveryWorker(backgroundStore, pipeline, config, loggerFactory.CreateLogger<BackgroundSessionRecoveryWorker>())
    .Start(lifetime);
```

If `SessionManager.Store` is not exposed, add an internal property:

```csharp
internal IMemoryStore Store => _store;
```

If `GatewayWorkers.Start` does not have an `ILoggerFactory`, pass `IServiceProvider` or a typed logger from `PipelineExtensions.StartWorkers`.

Wrap `RecoverOnceAsync` in a fire-and-observe task that logs exceptions and respects `lifetime.ApplicationStopping`.

- [ ] **Step 5: Add lifecycle notification helper**

In `GatewayInboundMessageWorker`, when `turnResult.StopReason` is `Completed`, `Blocked`, `BudgetLimited`, or `Failed`, write an `OutboundMessage` with a short state summary if `config.BackgroundExecution.NotifyOnCompletion`, `NotifyOnBlocked`, or `NotifyOnBudgetLimited` is enabled. Use the original `ChannelId`, `SenderId`, `AccountId`, and `SessionId`.

Use this text mapping:

```csharp
static string BuildBackgroundNotification(Session session, AgentTurnResult result)
    => result.StopReason switch
    {
        AgentTurnStopReason.Completed => $"Background task completed: {result.Text}",
        AgentTurnStopReason.Blocked => $"Background task blocked: {result.Text}",
        AgentTurnStopReason.BudgetLimited => $"Background task paused because its budget was reached: {result.Text}",
        AgentTurnStopReason.Failed => $"Background task failed: {result.Text}",
        _ => result.Text
    };
```

- [ ] **Step 6: Run recovery tests and build**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "GatewayBackgroundContinuationTests|BackgroundSessionStoreTests"`

Expected: PASS.

Run: `dotnet build OpenClaw.Net.slnx`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs src/OpenClaw.Gateway/Pipeline/PipelineExtensions.cs src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs src/OpenClaw.Core/Sessions/SessionManager.cs src/OpenClaw.Tests/GatewayBackgroundContinuationTests.cs
git commit -m "feat(gateway): recover background sessions on startup"
```

---

## Final Verification

- [ ] **Run targeted background test set**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "BackgroundSessionModelTests|AgentRuntimeBackgroundResultTests|MafBackgroundResultTests|BackgroundSessionStoreTests|GatewayBackgroundContinuationTests|WebSocketBackgroundSessionTests"
```

Expected: all selected tests pass.

- [ ] **Run broader affected tests**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "AgentRuntimeTests|MafAdapterTests|SessionManagerTests|FileMemoryStoreTests|SqliteMemoryStoreRetentionTests|GatewayAdminEndpointTests"
```

Expected: all selected tests pass. If an unrelated pre-existing test fails, capture the failing test name and error output in the handoff notes.

- [ ] **Run build**

Run:

```bash
dotnet build OpenClaw.Net.slnx
```

Expected: build succeeds.

- [ ] **Update docs if runtime behavior changes during implementation**

If implementation deviates from [docs/superpowers/specs/2026-07-02-background-session-execution-design.md](../specs/2026-07-02-background-session-execution-design.md), update the spec in the same commit as the deviation and explain the reason in the commit message.

---

## Self-Review Notes

- Spec coverage: Channel/WebChat parity is covered by Task 7; native/MAF parity by Tasks 2-4; durable state by Tasks 1 and 5; self-requeue by Task 6; startup recovery by Task 8; notifications by Task 8; fairness by Task 6 limiter.
- Placeholder scan: no placeholder tokens are intentionally left in the plan; every code-writing step includes concrete file paths and code shape.
- Type consistency: `SessionRunState`, `BackgroundRunMetadata`, `AgentTurnResult`, `AgentTurnStopReason`, `BackgroundMessageTypes`, `IBackgroundSessionStore`, `BackgroundExecutionLimiter`, and `BackgroundSessionRecoveryWorker` are introduced before later tasks depend on them.
