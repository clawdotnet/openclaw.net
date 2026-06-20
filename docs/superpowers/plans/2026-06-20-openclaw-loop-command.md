# OpenClaw /loop Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `/loop` recurring-prompt command using TickerQ cron scheduling with CLI + chat channel support, semantic auto-termination, and AOT-compatible design.

**Architecture:** New `OpenClaw.Core/Loops/` namespace with `ClawLoopScheduler`, `AgentLoopJob` (TickerFunction), `LoopTerminationDetector`, and interfaces `ILoopControlService`/`IAgentLoopDispatcher`. `LoopCommandParser` in CLI, `LoopControlTool` (ITool) in Agent. ChatCommandProcessor gains /loop routes. Gateway wires dispatcher. Zero AgentRuntime changes.

**Tech Stack:** C# / .NET 10, TickerQ 10.4.0, System.Text.Json source-gen, [GeneratedRegex], xUnit + NSubstitute

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `src/OpenClaw.Core/Loops/AgentLoopRequestPayload.cs` | Create | Strongly-typed JSON payload record |
| `src/OpenClaw.Core/Loops/IAgentLoopDispatcher.cs` | Create | Dispatch interface |
| `src/OpenClaw.Core/Loops/ILoopControlService.cs` | Create | Termination signal interface |
| `src/OpenClaw.Core/Loops/LoopTerminationDetector.cs` | Create | Dual-path termination detection |
| `src/OpenClaw.Core/Loops/ClawLoopScheduler.cs` | Create | TickerQ scheduling facade |
| `src/OpenClaw.Core/Loops/AgentLoopJob.cs` | Create | [TickerFunction] executor |
| `src/OpenClaw.Cli/Commands/LoopCommandParser.cs` | Create | [GeneratedRegex] /loop parser |
| `src/OpenClaw.Agent/Tools/LoopControlTool.cs` | Create | ITool for model-driven termination |
| `src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs` | Modify | Add /loop command branches |
| `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs` | Modify | Register loop services + tool |
| `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs` | Modify | Wire loop parse before command processor |
| `src/OpenClaw.Tests/LoopCommandParserTests.cs` | Create | Parser unit tests |
| `src/OpenClaw.Tests/ClawLoopSchedulerTests.cs` | Create | Scheduler unit tests |
| `src/OpenClaw.Tests/AgentLoopJobTests.cs` | Create | Job execution tests |
| `src/OpenClaw.Tests/LoopTerminationDetectorTests.cs` | Create | Detector tests |
| `src/OpenClaw.Tests/LoopControlToolTests.cs` | Create | Tool tests |

---

### Task 1: Core Types and Interfaces

**Files:** Create `src/OpenClaw.Core/Loops/AgentLoopRequestPayload.cs`, `src/OpenClaw.Core/Loops/IAgentLoopDispatcher.cs`, `src/OpenClaw.Core/Loops/ILoopControlService.cs`

- [ ] **Step 1: Create `src/OpenClaw.Core/Loops/AgentLoopRequestPayload.cs`**

```csharp
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Strongly-typed payload carried by TickerQ cron jobs for loop dispatch.
/// Uses source-generated JSON to stay AOT-safe.
/// </summary>
public sealed record AgentLoopRequestPayload(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("prompt")] string Prompt
);
```

- [ ] **Step 2: Create `src/OpenClaw.Core/Loops/IAgentLoopDispatcher.cs`**

```csharp
namespace OpenClaw.Core.Loops;

/// <summary>
/// Dispatches a loop prompt into a session and advances the agent turn.
/// Implemented by the Gateway host to bridge loop scheduling with AgentRuntime.
/// </summary>
public interface IAgentLoopDispatcher
{
    /// <summary>
    /// Injects the loop prompt as a user message into the session and runs one agent turn.
    /// Returns true if the turn was dispatched and completed (success or failure).
    /// Returns false if the session does not exist or is locked.
    /// </summary>
    Task<bool> DispatchAsync(string sessionId, string prompt, CancellationToken ct);
}
```

- [ ] **Step 3: Create `src/OpenClaw.Core/Loops/ILoopControlService.cs`**

```csharp
namespace OpenClaw.Core.Loops;

/// <summary>
/// Receives termination signals from the LoopControlTool or LoopTerminationDetector.
/// Implemented by ClawLoopScheduler to bridge tool/detector with TickerQ cancellation.
/// </summary>
public interface ILoopControlService
{
    /// <summary>
    /// Signals that the loop for a session should be terminated.
    /// Called by LoopControlTool when the model declares completion,
    /// or by LoopTerminationDetector when keyword match fires.
    /// </summary>
    Task SignalCompleteAsync(string sessionId, CancellationToken ct);
}
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds, new files under `Loops/` namespace.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Loops/AgentLoopRequestPayload.cs src/OpenClaw.Core/Loops/IAgentLoopDispatcher.cs src/OpenClaw.Core/Loops/ILoopControlService.cs
git commit -m "feat(loop): add core loop types and interfaces"
```

---

### Task 2: ClawLoopScheduler — TickerQ Scheduling Facade

**Files:** Create `src/OpenClaw.Core/Loops/ClawLoopScheduler.cs`

- [ ] **Step 1: Create the scheduler**

```csharp
using Microsoft.Extensions.Logging;
using TickerQ;
using TickerQ.Utilities;
using TickerQ.Utilities.Base;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Manages /loop command lifecycle by registering, overriding, and canceling
/// TickerQ cron jobs. One loop per session (idempotent).
/// </summary>
public sealed class ClawLoopScheduler : ILoopControlService
{
    private readonly ICronTickerManager _cronTickerManager;
    private readonly ILogger<ClawLoopScheduler> _logger;

    // Fixed job ID pattern ensures at most one loop per session
    private static string JobId(string sessionId) => $"LoopJob_{sessionId}";

    public ClawLoopScheduler(ICronTickerManager cronTickerManager, ILogger<ClawLoopScheduler> logger)
    {
        _cronTickerManager = cronTickerManager ?? throw new ArgumentNullException(nameof(cronTickerManager));
        _logger = logger;
    }

    /// <summary>
    /// Schedules (or overwrites) a recurring loop for the given session.
    /// </summary>
    public async Task<string> ScheduleLoopAsync(string sessionId, string cronExpression, string prompt, CancellationToken ct)
    {
        var jobId = JobId(sessionId);
        _logger.LogInformation("Registering loop for session {SessionId} with cron '{Cron}'", sessionId, cronExpression);

        var payload = new AgentLoopRequestPayload(sessionId, prompt);

        var ticker = new CronTicker
        {
            Function = "AgentLoopExecutor",
            Expression = cronExpression,
            Request = TickerHelper.CreateTickerRequest(payload),
            Retries = 2,
            RetryIntervals = new[] { 15, 45 }
        };

        await _cronTickerManager.CreateOrUpdateAsync(jobId, ticker, ct);

        return jobId;
    }

    /// <summary>
    /// Cancels the loop for the given session. No-op if none exists.
    /// </summary>
    public async Task CancelLoopAsync(string sessionId, CancellationToken ct)
    {
        var jobId = JobId(sessionId);
        _logger.LogInformation("Canceling loop for session {SessionId}", sessionId);
        await _cronTickerManager.RemoveAsync(jobId, ct);
    }

    /// <summary>
    /// Returns loop status info or null if no loop is active.
    /// </summary>
    public async Task<string?> GetLoopStatusAsync(string sessionId, CancellationToken ct)
    {
        var jobId = JobId(sessionId);
        var ticker = await _cronTickerManager.GetAsync(jobId, ct);
        if (ticker is null) return null;

        var payload = TickerHelper.ReadRequest<AgentLoopRequestPayload>(ticker.Request);
        return $"Loop active — interval: {ticker.Expression}, prompt: \"{payload?.Prompt}\", next trigger: per cron schedule";
    }

    /// <inheritdoc />
    async Task ILoopControlService.SignalCompleteAsync(string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("Loop termination signal received for session {SessionId}", sessionId);
        await CancelLoopAsync(sessionId, ct);
    }

    /// <summary>
    /// Converts a user-facing interval (e.g. "5m", "30s", "2h") to a 6-field cron expression.
    /// </summary>
    public static string IntervalToCron(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
            throw new ArgumentException("Interval must not be empty.", nameof(interval));

        var unit = interval[^1];
        if (!int.TryParse(interval.AsSpan(0, interval.Length - 1), out var val) || val <= 0)
            throw new ArgumentException($"Invalid interval: {interval}", nameof(interval));

        return unit switch
        {
            's' when val >= 60 => $"*/{val / 60} * * * *",
            's' => $"*/{val} * * * * *",
            'm' => $"*/{val} * * * *",
            'h' => $"0 */{val} * * *",
            _ => throw new ArgumentException($"Unknown interval unit: {unit}", nameof(interval))
        };
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Loops/ClawLoopScheduler.cs
git commit -m "feat(loop): add ClawLoopScheduler — TickerQ scheduling facade"
```

---

### Task 3: AgentLoopJob — TickerFunction Executor

**Files:** Create `src/OpenClaw.Core/Loops/AgentLoopJob.cs`

- [ ] **Step 1: Create the job**

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace OpenClaw.Core.Loops;

/// <summary>
/// TickerQ function that fires on each loop interval tick.
/// Dispatches the loop prompt to the agent via IAgentLoopDispatcher.
/// Cuts OTel context to prevent span nesting across loop iterations.
/// </summary>
public sealed class AgentLoopJob
{
    private readonly IAgentLoopDispatcher _dispatcher;
    private readonly ILogger<AgentLoopJob> _logger;

    public AgentLoopJob(IAgentLoopDispatcher dispatcher, ILogger<AgentLoopJob> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
    }

    [TickerFunction("AgentLoopExecutor")]
    public async Task ExecuteAsync(TickerFunctionContext<AgentLoopRequestPayload> ctx, CancellationToken ct)
    {
        // Cut OTel context to prevent nested span chains across loop iterations
        Activity.Current = null;

        var payload = ctx.Request;
        _logger.LogInformation(
            "Loop tick: dispatching prompt for session {SessionId}",
            payload.SessionId);

        try
        {
            var dispatched = await _dispatcher.DispatchAsync(payload.SessionId, payload.Prompt, ct);
            if (!dispatched)
            {
                _logger.LogWarning(
                    "Loop dispatch returned false for session {SessionId} (session may be busy or gone)",
                    payload.SessionId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Loop tick canceled for session {SessionId}", payload.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loop dispatch failed for session {SessionId}", payload.SessionId);
            throw; // Let TickerQ retry mechanism handle it
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Loops/AgentLoopJob.cs
git commit -m "feat(loop): add AgentLoopJob TickerFunction executor"
```

---

### Task 4: LoopTerminationDetector

**Files:** Create `src/OpenClaw.Core/Loops/LoopTerminationDetector.cs`

- [ ] **Step 1: Create the detector**

```csharp
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Loops;

/// <summary>
/// Detects loop termination signals through two paths:
/// 1. (Primary) Explicit tool call from the model via ILoopControlService.SignalCompleteAsync
/// 2. (Fallback) Keyword matching in model response text
/// </summary>
public sealed class LoopTerminationDetector
{
    private readonly ILoopControlService _loopControl;
    private readonly ILogger<LoopTerminationDetector> _logger;

    private static readonly FrozenSet<string> TerminationKeywords = new[]
    {
        "LOOP_TERMINATE",
        "任务完成",
        "DONE",
        "WORK_COMPLETE",
        "任务已全部完成",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public LoopTerminationDetector(ILoopControlService loopControl, ILogger<LoopTerminationDetector> logger)
    {
        _loopControl = loopControl ?? throw new ArgumentNullException(nameof(loopControl));
        _logger = logger;
    }

    /// <summary>
    /// Scans a chunk of response text for termination keywords.
    /// Returns true if termination was triggered.
    /// </summary>
    public async Task<bool> ScanTextAsync(string sessionId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in TerminationKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Loop termination keyword '{Keyword}' detected in response for session {SessionId}",
                    keyword, sessionId);
                await _loopControl.SignalCompleteAsync(sessionId, ct);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Called when the LoopControlTool fires. Primary termination path.
    /// </summary>
    public async Task OnToolCompleteAsync(string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("LoopControlTool signaled completion for session {SessionId}", sessionId);
        await _loopControl.SignalCompleteAsync(sessionId, ct);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Core/Loops/LoopTerminationDetector.cs
git commit -m "feat(loop): add LoopTerminationDetector — dual-path termination"
```

---

### Task 5: LoopCommandParser — CLI & Chat Parser

**Files:** Create `src/OpenClaw.Cli/Commands/LoopCommandParser.cs`

- [ ] **Step 1: Create the parser**

```csharp
using System.Text.RegularExpressions;

namespace OpenClaw.Cli.Commands;

/// <summary>
/// Parses /loop commands from CLI and chat channels.
/// Uses [GeneratedRegex] for AOT safety.
/// </summary>
public static partial class LoopCommandParser
{
    /// <summary>
    /// /loop cancel — cancel the active loop
    /// </summary>
    public const string CancelCommand = "cancel";

    /// <summary>
    /// /loop status — query loop state
    /// </summary>
    public const string StatusCommand = "status";

    [GeneratedRegex(@"^/loop\s+(?<value>\d+)\s*(?<unit>s|m|h)\s+(?<prompt>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LoopCommandRegex();

    /// <summary>
    /// Tries to parse a /loop command. Returns null if the text is not a /loop command.
    /// </summary>
    public static LoopCommand? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/loop", StringComparison.OrdinalIgnoreCase))
            return null;

        var trimmed = text.Trim();

        // /loop cancel
        if (trimmed.Equals("/loop cancel", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/loop stop", StringComparison.OrdinalIgnoreCase))
        {
            return new LoopCommand { Action = LoopAction.Cancel };
        }

        // /loop status
        if (trimmed.Equals("/loop status", StringComparison.OrdinalIgnoreCase))
        {
            return new LoopCommand { Action = LoopAction.Status };
        }

        // /loop <value><unit> <prompt>
        var match = LoopCommandRegex().Match(trimmed);
        if (!match.Success)
        {
            return new LoopCommand { Action = LoopAction.Invalid };
        }

        var interval = $"{match.Groups["value"].Value}{match.Groups["unit"].Value}";
        var prompt = match.Groups["prompt"].Value.Trim();

        return new LoopCommand
        {
            Action = LoopAction.Schedule,
            Interval = interval,
            Prompt = prompt
        };
    }
}

public enum LoopAction
{
    Schedule,
    Cancel,
    Status,
    Invalid
}

public sealed class LoopCommand
{
    public LoopAction Action { get; init; }
    public string? Interval { get; init; }
    public string? Prompt { get; init; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/OpenClaw.Cli/OpenClaw.Cli.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Cli/Commands/LoopCommandParser.cs
git commit -m "feat(loop): add LoopCommandParser — [GeneratedRegex] /loop parser"
```

---

### Task 6: LoopControlTool — Model-Driven Termination

**Files:** Create `src/OpenClaw.Agent/Tools/LoopControlTool.cs`

- [ ] **Step 1: Create the tool**

```csharp
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Tool that lets the model explicitly declare a loop task is complete.
/// When called with status="complete", signals the LoopTerminationDetector
/// to cancel the TickerQ cron job for this session.
/// </summary>
public sealed class LoopControlTool : IToolWithContext
{
    private readonly LoopTerminationDetector _detector;

    public LoopControlTool(LoopTerminationDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public string Name => "loop_control";

    public string Description =>
        "Control the active /loop recurring task. Use status='complete' when the loop task is fully done. " +
        "Do NOT use this for ongoing progress — only for final completion.";

    public string ParameterSchema => """
        {"type":"object","properties":{"status":{"type":"string","enum":["complete"],"description":"Set to 'complete' when the loop task is finished."}},"required":["status"]}
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        return ValueTask.FromResult("Error: loop_control requires session context.");
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "Error: arguments payload is empty.";

        string? status;
        try
        {
            using var args = JsonDocument.Parse(argumentsJson);
            if (!args.RootElement.TryGetProperty("status", out var statusElement))
                return "Error: status is required.";

            status = statusElement.GetString();
        }
        catch (JsonException)
        {
            return "Error: arguments must be valid JSON.";
        }

        if (string.IsNullOrWhiteSpace(status))
            return "Error: status is required.";

        if (!status.Equals("complete", StringComparison.OrdinalIgnoreCase))
            return $"Error: unsupported status '{status}'. Only 'complete' is allowed.";

        await _detector.OnToolCompleteAsync(context.Session.Id, ct);
        return "Loop marked as complete. The recurring task has been stopped.";
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/OpenClaw.Agent/OpenClaw.Agent.csproj`
Expected: Build succeeds (Agent references Core which now has Loops namespace).

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Agent/Tools/LoopControlTool.cs
git commit -m "feat(loop): add LoopControlTool — model-driven loop termination"
```

---

### Task 7: ChatCommandProcessor — /loop Routes

**Files:** Modify `src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs`

- [ ] **Step 1: Add /loop to BuiltInCommands and constructor**

In `ChatCommandProcessor.cs`, modify `BuiltInCommands` (add `"/loop"` after `"/goal"`):

```csharp
private static readonly FrozenSet<string> BuiltInCommands = new[]
{
    "/status",
    "/new",
    "/reset",
    "/model",
    "/usage",
    "/think",
    "/compact",
    "/concise",
    "/verbose",
    "/goal",
    "/loop",
    "/help"
}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
```

Add a settable callback for loop command handling:

```csharp
private Func<Session, LoopAction, string?, string?, CancellationToken, Task<string?>>? _loopCallback;

public void SetLoopCallback(Func<Session, LoopAction, string?, string?, CancellationToken, Task<string?>> callback)
    => _loopCallback = callback;
```

Add needed using at top:

```csharp
using OpenClaw.Cli.Commands;
```

- [ ] **Step 2: Add /loop case in TryProcessCommandAsync**

Inside the `switch (command)` block in `TryProcessCommandAsync`, add after the `/goal` case:

```csharp
case "/loop":
    if (_loopCallback is null)
        return (true, "Loop scheduling is not available in this configuration.");

    var loopCmd = LoopCommandParser.TryParse(text);
    if (loopCmd is null)
        return (true, "Usage: /loop <interval> <prompt>  — e.g. /loop 5m check build status\n       /loop cancel  — cancel active loop\n       /loop status  — show loop status");

    if (loopCmd.Action == LoopAction.Invalid)
        return (true, "Invalid /loop syntax. Usage: /loop <interval> <prompt>  (e.g. /loop 5m check build status)");

    var loopResult = await _loopCallback(session, loopCmd.Action, loopCmd.Interval, loopCmd.Prompt, ct);
    return (true, loopResult);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds (Core references CLI project - verify the dependency). If Core doesn't reference CLI, adjust: move `LoopAction`/`LoopCommand` to a shared location in Core. Let's verify the dependency first.

Actually, check if `OpenClaw.Core.csproj` references `OpenClaw.Cli`. If not, we need to either add the reference or relocate the types.

Let me check: Run `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj` first.

If build fails due to missing CLI reference, add to `OpenClaw.Core.csproj`:
```xml
<ProjectReference Include="..\OpenClaw.Cli\OpenClaw.Cli.csproj" />
```

Or better: Move `LoopAction` and `LoopCommand` POCOS to `src/OpenClaw.Core/Loops/LoopCommandModel.cs` to avoid the reverse dependency. This is cleaner.

- [ ] **Step 4: If needed, move LoopAction/LoopCommand to Core**

Create `src/OpenClaw.Core/Loops/LoopCommandModel.cs`:

```csharp
namespace OpenClaw.Core.Loops;

public enum LoopAction
{
    Schedule,
    Cancel,
    Status,
    Invalid
}

public sealed class LoopCommand
{
    public LoopAction Action { get; init; }
    public string? Interval { get; init; }
    public string? Prompt { get; init; }
}
```

Update `LoopCommandParser.cs` to use `OpenClaw.Core.Loops.LoopAction` and `OpenClaw.Core.Loops.LoopCommand` instead of its own nested types. Remove the duplicate definitions from `LoopCommandParser.cs`.

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/OpenClaw.Core/OpenClaw.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Core/Loops/LoopCommandModel.cs src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs src/OpenClaw.Cli/Commands/LoopCommandParser.cs
git commit -m "feat(loop): integrate /loop routes into ChatCommandProcessor"
```

---

### Task 8: Gateway — DI Registration and Dispatcher

**Files:** Modify `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`, Modify `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs`

- [ ] **Step 1: Register loop services in CoreServicesExtensions.cs**

In the `ConfigureCoreServices` method (or equivalent composition method), add after TickerQ registration:

```csharp
// Loop scheduling
services.AddSingleton<ILoopControlService>(sp => sp.GetRequiredService<ClawLoopScheduler>());
services.AddSingleton<ClawLoopScheduler>();
services.AddSingleton<LoopTerminationDetector>();
services.AddSingleton<AgentLoopJob>();
services.AddSingleton<ITool, LoopControlTool>();
```

Add usings at top:
```csharp
using OpenClaw.Core.Loops;
using OpenClaw.Agent.Tools;
```

- [ ] **Step 2: Implement IAgentLoopDispatcher**

Create a simple dispatcher class or register as a factory. The simplest approach: register as a singleton that captures the necessary services. Add to CoreServicesExtensions.cs:

```csharp
services.AddSingleton<IAgentLoopDispatcher>(sp =>
{
    var sessionManager = sp.GetRequiredService<SessionManager>();
    var runtimeFactory = sp.GetRequiredService<IAgentRuntimeFactory>();
    var pipeline = sp.GetRequiredService<MessagePipeline>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentLoopDispatcher");

    return new GatewayAgentLoopDispatcher(sessionManager, runtimeFactory, pipeline, logger);
});
```

- [ ] **Step 3: Create GatewayAgentLoopDispatcher**

Create `src/OpenClaw.Gateway/Pipeline/GatewayAgentLoopDispatcher.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Pipeline;

internal sealed class GatewayAgentLoopDispatcher : IAgentLoopDispatcher
{
    private readonly SessionManager _sessionManager;
    private readonly IAgentRuntimeFactory _runtimeFactory;
    private readonly MessagePipeline _pipeline;
    private readonly ILogger _logger;

    public GatewayAgentLoopDispatcher(
        SessionManager sessionManager,
        IAgentRuntimeFactory runtimeFactory,
        MessagePipeline pipeline,
        ILogger logger)
    {
        _sessionManager = sessionManager;
        _runtimeFactory = runtimeFactory;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(string sessionId, string prompt, CancellationToken ct)
    {
        // Look up session by ID
        var session = await _sessionManager.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            _logger.LogWarning("Loop dispatch: session {SessionId} not found", sessionId);
            return false;
        }

        // Inject loop prompt as an inbound message into the pipeline
        await _pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            SessionId = sessionId,
            ChannelId = session.ChannelId,
            SenderId = "loop",
            Text = prompt,
            IsSystem = true
        }, ct);

        return true;
    }
}
```

- [ ] **Step 4: Wire LoopCallback in GatewayInboundMessageWorker**

In the `RunWorkerAsync` method, after `commandProcessor` is constructed/available, set the loop callback:

```csharp
commandProcessor.SetLoopCallback(async (session, action, interval, prompt, ct) =>
{
    var scheduler = serviceProvider.GetRequiredService<ClawLoopScheduler>();

    return action switch
    {
        LoopAction.Schedule when interval is not null && prompt is not null =>
            await ScheduleLoopAsync(scheduler, session.Id, interval, prompt, ct),

        LoopAction.Cancel =>
            await CancelLoopAsync(scheduler, session.Id, ct),

        LoopAction.Status =>
            await GetLoopStatusAsync(scheduler, session.Id, ct),

        _ => "Invalid loop command. Usage: /loop <interval> <prompt>"
    };
});

async Task<string> ScheduleLoopAsync(ClawLoopScheduler scheduler, string sessionId, string interval, string prompt, CancellationToken ct)
{
    try
    {
        var cron = ClawLoopScheduler.IntervalToCron(interval);
        var jobId = await scheduler.ScheduleLoopAsync(sessionId, cron, prompt, ct);
        return $"Loop started — interval: {interval}, prompt: \"{prompt}\"";
    }
    catch (ArgumentException ex)
    {
        return $"Error: {ex.Message}";
    }
}

async Task<string> CancelLoopAsync(ClawLoopScheduler scheduler, string sessionId, CancellationToken ct)
{
    await scheduler.CancelLoopAsync(sessionId, ct);
    return "Loop canceled.";
}

async Task<string> GetLoopStatusAsync(ClawLoopScheduler scheduler, string sessionId, CancellationToken ct)
{
    var status = await scheduler.GetLoopStatusAsync(sessionId, ct);
    return status ?? "No active loop for this session.";
}
```

Note: `serviceProvider` needs to be captured from the method scope. The `RunWorkerAsync` method already receives an `IServiceProvider` or has access to one through `lifetime`. Adjust the capture accordingly based on the actual method signature.

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Gateway/
git commit -m "feat(loop): wire Gateway — DI registration, dispatcher, and loop callback"
```

---

### Task 9: Tests — LoopCommandParser

**Files:** Create `src/OpenClaw.Tests/LoopCommandParserTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using OpenClaw.Core.Loops;
using OpenClaw.Cli.Commands;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopCommandParserTests
{
    [Theory]
    [InlineData("/loop 5m check build status", LoopAction.Schedule, "5m", "check build status")]
    [InlineData("/loop 30s ping server", LoopAction.Schedule, "30s", "ping server")]
    [InlineData("/loop 1h run full audit", LoopAction.Schedule, "1h", "run full audit")]
    [InlineData("/loop 10m 检查构建状态", LoopAction.Schedule, "10m", "检查构建状态")]
    public void Parse_ValidSchedule_ReturnsScheduleAction(string input, LoopAction expectedAction, string expectedInterval, string expectedPrompt)
    {
        var cmd = LoopCommandParser.TryParse(input);

        Assert.NotNull(cmd);
        Assert.Equal(expectedAction, cmd.Action);
        Assert.Equal(expectedInterval, cmd.Interval);
        Assert.Equal(expectedPrompt, cmd.Prompt);
    }

    [Fact]
    public void Parse_Cancel_ReturnsCancelAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop cancel");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Cancel, cmd.Action);
    }

    [Fact]
    public void Parse_Stop_ReturnsCancelAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop stop");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Cancel, cmd.Action);
    }

    [Fact]
    public void Parse_Status_ReturnsStatusAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop status");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Status, cmd.Action);
    }

    [Theory]
    [InlineData("/loop")]
    [InlineData("/loop ")]
    [InlineData("/loop 5m")]
    [InlineData("/loop abc check")]
    [InlineData("/loop 5x check")]
    public void Parse_Invalid_ReturnsInvalidAction(string input)
    {
        var cmd = LoopCommandParser.TryParse(input);
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Invalid, cmd.Action);
    }

    [Theory]
    [InlineData("not a loop command")]
    [InlineData("/help")]
    [InlineData("/goal complete")]
    public void Parse_NotLoopCommand_ReturnsNull(string input)
    {
        var cmd = LoopCommandParser.TryParse(input);
        Assert.Null(cmd);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoopCommandParserTests" -v n`
Expected: All tests pass.

- [ ] **Step 3: Build the test project first if needed**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj`
Then: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoopCommandParserTests"`

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/LoopCommandParserTests.cs
git commit -m "test(loop): add LoopCommandParser unit tests"
```

---

### Task 10: Tests — ClawLoopScheduler

**Files:** Create `src/OpenClaw.Tests/ClawLoopSchedulerTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Loops;
using TickerQ;
using TickerQ.Utilities.Base;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ClawLoopSchedulerTests
{
    private readonly ICronTickerManager _mockManager = Substitute.For<ICronTickerManager>();
    private readonly ILogger<ClawLoopScheduler> _mockLogger = Substitute.For<ILogger<ClawLoopScheduler>>();

    [Fact]
    public async Task ScheduleLoopAsync_CreatesCronTicker()
    {
        var scheduler = new ClawLoopScheduler(_mockManager, _mockLogger);
        var cron = "*/5 * * * *";

        await scheduler.ScheduleLoopAsync("s1", cron, "check status", TestContext.Current.CancellationToken);

        await _mockManager.Received(1).CreateOrUpdateAsync("LoopJob_s1", Arg.Is<CronTicker>(t =>
            t.Function == "AgentLoopExecutor" &&
            t.Expression == cron &&
            t.Retries == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelLoopAsync_RemovesCronTicker()
    {
        var scheduler = new ClawLoopScheduler(_mockManager, _mockLogger);

        await scheduler.CancelLoopAsync("s1", TestContext.Current.CancellationToken);

        await _mockManager.Received(1).RemoveAsync("LoopJob_s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignalComplete_CancelsLoop()
    {
        var scheduler = new ClawLoopScheduler(_mockManager, _mockLogger);
        var control = (ILoopControlService)scheduler;

        await control.SignalCompleteAsync("s1", TestContext.Current.CancellationToken);

        await _mockManager.Received(1).RemoveAsync("LoopJob_s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLoopStatus_NoJob_ReturnsNull()
    {
        _mockManager.GetAsync("LoopJob_s1", Arg.Any<CancellationToken>()).Returns((CronTicker?)null);
        var scheduler = new ClawLoopScheduler(_mockManager, _mockLogger);

        var status = await scheduler.GetLoopStatusAsync("s1", TestContext.Current.CancellationToken);

        Assert.Null(status);
    }

    [Theory]
    [InlineData("5m", "*/5 * * * *")]
    [InlineData("30s", "*/30 * * * * *")]
    [InlineData("120s", "*/2 * * * *")]
    [InlineData("1h", "0 */1 * * *")]
    public void IntervalToCron_ConvertsCorrectly(string interval, string expectedCron)
    {
        var cron = ClawLoopScheduler.IntervalToCron(interval);
        Assert.Equal(expectedCron, cron);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5x")]
    [InlineData("abc")]
    public void IntervalToCron_Invalid_Throws(string interval)
    {
        Assert.Throws<ArgumentException>(() => ClawLoopScheduler.IntervalToCron(interval));
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj && dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ClawLoopSchedulerTests"`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Tests/ClawLoopSchedulerTests.cs
git commit -m "test(loop): add ClawLoopScheduler unit tests"
```

---

### Task 11: Tests — LoopTerminationDetector and LoopControlTool

**Files:** Create `src/OpenClaw.Tests/LoopTerminationDetectorTests.cs`, `src/OpenClaw.Tests/LoopControlToolTests.cs`

- [ ] **Step 1: Write LoopTerminationDetectorTests.cs**

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Loops;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopTerminationDetectorTests
{
    private readonly ILoopControlService _mockControl = Substitute.For<ILoopControlService>();
    private readonly ILogger<LoopTerminationDetector> _mockLogger = Substitute.For<ILogger<LoopTerminationDetector>>();

    [Theory]
    [InlineData("LOOP_TERMINATE")]
    [InlineData("任务完成")]
    [InlineData("DONE")]
    [InlineData("WORK_COMPLETE")]
    public async Task ScanText_KeywordMatch_SignalsComplete(string text)
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", text, TestContext.Current.CancellationToken);

        Assert.True(result);
        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanText_NoKeyword_DoesNotSignal()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        var result = await detector.ScanTextAsync("s1", "just a normal response", TestContext.Current.CancellationToken);

        Assert.False(result);
        await _mockControl.DidNotReceive().SignalCompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanText_EmptyOrNull_ReturnsFalse()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        Assert.False(await detector.ScanTextAsync("s1", "", TestContext.Current.CancellationToken));
        Assert.False(await detector.ScanTextAsync("s1", null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnToolComplete_SignalsComplete()
    {
        var detector = new LoopTerminationDetector(_mockControl, _mockLogger);

        await detector.OnToolCompleteAsync("s1", TestContext.Current.CancellationToken);

        await _mockControl.Received(1).SignalCompleteAsync("s1", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Write LoopControlToolTests.cs**

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopControlToolTests
{
    private readonly LoopTerminationDetector _mockDetector;
    private readonly LoopControlTool _tool;

    public LoopControlToolTests()
    {
        var mockControl = Substitute.For<ILoopControlService>();
        var mockLogger = Substitute.For<ILogger<LoopTerminationDetector>>();
        _mockDetector = Substitute.For<LoopTerminationDetector>(mockControl, mockLogger);
        _tool = new LoopControlTool(_mockDetector);
    }

    [Fact]
    public void Tool_HasExpectedMetadata()
    {
        Assert.Equal("loop_control", _tool.Name);
        Assert.Contains("complete", _tool.Description);
        Assert.Contains("status", _tool.ParameterSchema);
    }

    [Fact]
    public async Task Execute_Complete_CallsDetector()
    {
        var context = new ToolExecutionContext
        {
            Session = new Session { Id = "s1", ChannelId = "cli", SenderId = "test" },
            TurnContext = new TurnContext { SessionId = "s1", ChannelId = "cli" }
        };

        var result = await _tool.ExecuteAsync(
            """{"status":"complete"}""",
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("stopped", result);
        await _mockDetector.Received(1).OnToolCompleteAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_InvalidStatus_ReturnsError()
    {
        var context = new ToolExecutionContext
        {
            Session = new Session { Id = "s1", ChannelId = "cli", SenderId = "test" },
            TurnContext = new TurnContext { SessionId = "s1", ChannelId = "cli" }
        };

        var result = await _tool.ExecuteAsync(
            """{"status":"paused"}""",
            context,
            TestContext.Current.CancellationToken);

        Assert.Contains("Error", result);
        await _mockDetector.DidNotReceive().OnToolCompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_NoContext_ReturnsError()
    {
        var result = await _tool.ExecuteAsync("{}", TestContext.Current.CancellationToken);
        Assert.Contains("Error", result);
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj && dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoopTerminationDetectorTests|FullyQualifiedName~LoopControlToolTests"`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/OpenClaw.Tests/LoopTerminationDetectorTests.cs src/OpenClaw.Tests/LoopControlToolTests.cs
git commit -m "test(loop): add termination detector and LoopControlTool tests"
```

---

### Task 12: Tests — AgentLoopJob

**Files:** Create `src/OpenClaw.Tests/AgentLoopJobTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Loops;
using TickerQ.Utilities.Base;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentLoopJobTests
{
    private readonly IAgentLoopDispatcher _mockDispatcher = Substitute.For<IAgentLoopDispatcher>();
    private readonly ILogger<AgentLoopJob> _mockLogger = Substitute.For<ILogger<AgentLoopJob>>();

    [Fact]
    public async Task Execute_DispatchSucceeded_NoException()
    {
        var job = new AgentLoopJob(_mockDispatcher, _mockLogger);
        _mockDispatcher.DispatchAsync("s1", "check status", Arg.Any<CancellationToken>())
            .Returns(true);

        var ctx = new TickerFunctionContext<AgentLoopRequestPayload>
        {
            Request = new AgentLoopRequestPayload("s1", "check status")
        };

        await job.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        await _mockDispatcher.Received(1).DispatchAsync("s1", "check status", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_DispatchFailed_NoException()
    {
        var job = new AgentLoopJob(_mockDispatcher, _mockLogger);
        _mockDispatcher.DispatchAsync("s1", "check status", Arg.Any<CancellationToken>())
            .Returns(false);

        var ctx = new TickerFunctionContext<AgentLoopRequestPayload>
        {
            Request = new AgentLoopRequestPayload("s1", "check status")
        };

        // Should not throw even when dispatch returns false
        await job.ExecuteAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_DispatchThrows_PropagatesToTickerQ()
    {
        var job = new AgentLoopJob(_mockDispatcher, _mockLogger);
        _mockDispatcher.DispatchAsync("s1", "check status", Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("test failure"));

        var ctx = new TickerFunctionContext<AgentLoopRequestPayload>
        {
            Request = new AgentLoopRequestPayload("s1", "check status")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ExecuteAsync(ctx, TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj && dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~AgentLoopJobTests"`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/OpenClaw.Tests/AgentLoopJobTests.cs
git commit -m "test(loop): add AgentLoopJob unit tests"
```

---

### Task 13: End-to-End Build & AOT Verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build OpenClaw.Net.slnx`
Expected: Build succeeds with zero warnings.

- [ ] **Step 2: Run all tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -v n`
Expected: All tests pass, including new loop tests and all existing tests.

- [ ] **Step 3: AOT publish validation**

Run: `dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release /p:PublishAot=true --no-restore 2>&1 | Select-String -Pattern "warning IL|error IL" | Select-Object -First 20`
Expected: No AOT trimming warnings from loop code.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore(loop): final integration — build, tests, AOT verification"
```
