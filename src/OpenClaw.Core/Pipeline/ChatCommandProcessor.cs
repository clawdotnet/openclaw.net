using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Loops;

namespace OpenClaw.Core.Pipeline;

public enum DynamicCommandRegistrationResult
{
    Registered,
    ReservedBuiltIn,
    Duplicate
}

public sealed class ChatCommandProcessor
{
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
        "/stop",
        "/cancel",
        "/abort",
        "/help"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SessionManager _sessionManager;
    private readonly ProviderUsageTracker? _providerUsage;
    private readonly IGoalService? _goalService;
    private readonly ConcurrentDictionary<string, Func<string, CancellationToken, Task<string>>> _dynamicCommands = new(StringComparer.OrdinalIgnoreCase);
    private Func<Session, CancellationToken, Task<int>>? _compactCallback;
    private Func<Session, string, CancellationToken, Task<string?>>? _loopCallback;

    public ChatCommandProcessor(SessionManager sessionManager, ProviderUsageTracker? providerUsage = null, IGoalService? goalService = null)
    {
        _sessionManager = sessionManager;
        _providerUsage = providerUsage;
        _goalService = goalService;
    }

    /// <summary>
    /// Sets the callback for LLM-powered history compaction (injected from gateway setup).
    /// </summary>
    public void SetCompactCallback(Func<Session, CancellationToken, Task<int>> callback)
        => _compactCallback = callback;

    /// <summary>
    /// Sets the callback for /loop command handling (injected from gateway setup).
    /// The callback receives (session, fullText, ct) and returns the response text.
    /// </summary>
    public void SetLoopCallback(Func<Session, string, CancellationToken, Task<string?>> callback)
        => _loopCallback = callback;

    /// <summary>
    /// Registers a dynamic command handler (e.g. from a plugin).
    /// </summary>
    public DynamicCommandRegistrationResult RegisterDynamic(string command, Func<string, CancellationToken, Task<string>> handler)
    {
        var key = command.StartsWith('/') ? command : "/" + command;
        if (BuiltInCommands.Contains(key))
            return DynamicCommandRegistrationResult.ReservedBuiltIn;

        return _dynamicCommands.TryAdd(key, handler)
            ? DynamicCommandRegistrationResult.Registered
            : DynamicCommandRegistrationResult.Duplicate;
    }

    /// <summary>
    /// Processes chat commands (starting with /).
    /// Returns true if a command was handled (and thus the pipeline should short-circuit the LLM).
    /// </summary>
    public async Task<(bool Handled, string? Response)> TryProcessCommandAsync(
        Session session, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/'))
            return (false, null);

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case "/status":
                var activeModel = session.ModelOverride ?? "default";
                var (statusCacheRead, statusCacheWrite) = GetCacheTotals(session);
                return (true, $"Session info:\n- Active Model: {activeModel}\n- Turn Count: {session.History.Count}\n- Token Usage: {session.TotalInputTokens} in / {session.TotalOutputTokens} out\n- Prompt Cache: {statusCacheRead} read / {statusCacheWrite} write");

            case "/new":
            case "/reset":
                session.History.Clear();
                session.TotalInputTokens = 0;
                session.TotalOutputTokens = 0;
                _goalService?.ClearGoal(session.Id);
                await _sessionManager.PersistAsync(session, ct);
                return (true, "Session history has been reset. Starting fresh!");

            case "/model":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Current model override: {session.ModelOverride ?? "none (using default)"}\nUsage: /model <model-name> or /model reset");

                if (args.Equals("reset", StringComparison.OrdinalIgnoreCase) || args.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    session.ModelOverride = null;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Model override cleared. Back to default.");
                }

                session.ModelOverride = args;
                await _sessionManager.PersistAsync(session, ct);
                return (true, $"Model override set to: {args}");

            case "/usage":
                var (usageCacheRead, usageCacheWrite) = GetCacheTotals(session);
                return (true, $"Total Token Usage in this session:\n- Input: {session.TotalInputTokens}\n- Output: {session.TotalOutputTokens}\n- Sum: {session.GetTotalTokens()}\n- Prompt Cache Read: {usageCacheRead}\n- Prompt Cache Write: {usageCacheWrite}");

            case "/think":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Current reasoning effort: {session.ReasoningEffort ?? "default"}\nUsage: /think off|low|medium|high");

                var level = args.ToLowerInvariant();
                if (level is "off" or "low" or "medium" or "high")
                {
                    session.ReasoningEffort = level == "off" ? null : level;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, level == "off"
                        ? "Extended thinking disabled."
                        : $"Reasoning effort set to: {level}");
                }
                return (true, "Invalid level. Use: /think off|low|medium|high");

            case "/compact":
                if (session.History.Count <= 2)
                    return (true, "Nothing to compact — session has 2 or fewer turns.");

                var turnsBefore = session.History.Count;
                if (_compactCallback is not null)
                {
                    var remainingTurns = await _compactCallback(session, ct);
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, $"Compacted: {turnsBefore} turns → {remainingTurns} turns remaining.");
                }

                // Fallback: simple trim keeping last 10 turns
                var keepRecent = Math.Min(10, session.History.Count);
                var removeCount = session.History.Count - keepRecent;
                if (removeCount > 0)
                    session.History.RemoveRange(0, removeCount);
                await _sessionManager.PersistAsync(session, ct);
                return (true, $"Trimmed: {turnsBefore} turns → {session.History.Count} turns (kept last {keepRecent}).");

            case "/verbose":
                if (string.IsNullOrWhiteSpace(args))
                    return (true, $"Verbose mode: {(session.VerboseMode ? "on" : "off")}\nUsage: /verbose on|off");

                if (args.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    session.VerboseMode = true;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Verbose mode enabled. Tool calls and token counts will be shown.");
                }
                if (args.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    session.VerboseMode = false;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Verbose mode disabled.");
                }
                return (true, "Usage: /verbose on|off");

            case "/concise":
                if (string.IsNullOrWhiteSpace(args))
                {
                    var currentMode = session.ResponseMode switch
                    {
                        SessionResponseModes.ConciseOps => "on",
                        SessionResponseModes.Full => "off",
                        _ => "auto"
                    };
                    return (true, $"Concise mode: {currentMode}\nUsage: /concise on|off|auto");
                }

                if (args.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.ConciseOps;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise operational mode enabled.");
                }
                if (args.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.Full;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise operational mode disabled for this session.");
                }
                if (args.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    session.ResponseMode = SessionResponseModes.Default;
                    await _sessionManager.PersistAsync(session, ct);
                    return (true, "Concise mode reset to automatic behavior.");
                }
                return (true, "Usage: /concise on|off|auto");

            case "/help":
                return (true, "Available commands:\n/status - Show session details\n/new (or /reset) - Clear conversation history\n/model <name> - Override the LLM model for this session\n/model reset - Clear model override\n/usage - Show token counts\n/think <level> - Set reasoning effort (off/low/medium/high)\n/compact - Compact conversation history\n/concise on|off|auto - Control concise operational responses\n/verbose on|off - Toggle verbose output\n/goal <action> - Manage session goals (start/set/create/pause/resume/complete/done/block/blocked/clear/status)\n/goal start <objective> +500k - Start a goal with a token budget\n/goal start <objective> spend 1.5m tokens - Start a goal with a budget phrase\n/help - Show this message");

            case "/loop":
                if (_loopCallback is null)
                    return (true, "Loop scheduling is not available in this configuration.");
                var loopResult = await _loopCallback(session, text, ct);
                return (true, loopResult);

            case "/goal":
                return (true, await HandleGoalCommandAsync(session, args, ct));

            case "/stop":
            case "/cancel":
            case "/abort":
                return (true, "There is no active execution to stop.");

            default:
                if (_dynamicCommands.TryGetValue(command, out var dynamicHandler))
                {
                    var dynamicResult = await dynamicHandler(args, ct);
                    return (true, dynamicResult);
                }

                // Not a recognized command — assume it might be normal user text that just starts with a slash
                return (false, null);
        }
    }

    private (long CacheReadTokens, long CacheWriteTokens) GetCacheTotals(Session session)
    {
        if (session.TotalCacheReadTokens > 0 || session.TotalCacheWriteTokens > 0)
            return (session.TotalCacheReadTokens, session.TotalCacheWriteTokens);

        return _providerUsage?.GetLatestSessionCacheTotals(session.Id) ?? (0, 0);
    }

    /// <summary>
    /// Handles the /goal command — full goal lifecycle management.
    /// CLI commands support start, pause, resume, complete, clear, and status.
    /// </summary>
    private async Task<string> HandleGoalCommandAsync(Session session, string args, CancellationToken ct)
    {
        if (_goalService is null)
            return "Goal system is not available. Start the gateway with goal support enabled.";

        var parts = string.IsNullOrWhiteSpace(args) ? [] : args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        var subargs = parts.Length > 1 ? parts[1] : "";

        switch (subcommand)
        {
            case "start":
            case "set":
            case "create":
            {
                // Parse objective and optional budget from the arguments
                // Format: /goal start <objective> or /goal start <objective> +<budget>
                var remaining = subargs;
                long budget = 0;

                // Check for +N token budget suffix
                var budgetMatch = Regex.Match(remaining, @"\+(?<budget>\d+(?:\.\d+)?)(?<suffix>[kKmM])?\s*$");
                if (budgetMatch.Success)
                {
                    var budgetVal = double.Parse(budgetMatch.Groups["budget"].Value, CultureInfo.InvariantCulture);
                    var multiplier = budgetMatch.Groups["suffix"].Value.ToLowerInvariant() switch
                    {
                        "k" => 1_000,
                        "m" => 1_000_000,
                        _ => 1,
                    };
                    budget = (long)(budgetVal * multiplier);
                    remaining = remaining[..budgetMatch.Index].TrimEnd();
                }

                // Check for "spend N tokens" syntax
                var spendMatch = Regex.Match(remaining, @"spend\s+(?<budget>\d+(?:\.\d+)?)\s*(?<suffix>[kKmM])?\s*tokens?\s*$", RegexOptions.IgnoreCase);
                if (spendMatch.Success)
                {
                    var budgetVal = double.Parse(spendMatch.Groups["budget"].Value, CultureInfo.InvariantCulture);
                    var multiplier = spendMatch.Groups["suffix"].Value.ToLowerInvariant() switch
                    {
                        "k" => 1_000,
                        "m" => 1_000_000,
                        _ => 1,
                    };
                    budget = (long)(budgetVal * multiplier);
                    remaining = remaining[..spendMatch.Index].TrimEnd();
                }

                var objective = remaining.Trim();
                if (string.IsNullOrWhiteSpace(objective))
                    return "Usage: /goal start <objective> [+<budget>]\nExample: /goal start fix auth bug +500k";

                // /new and /reset clear goals — enforce single-goal constraint
                var existing = _goalService.GetGoal(session.Id);
                if (existing is not null)
                    return $"A goal already exists: \"{existing.Objective}\"\nClear it with /goal clear first.";

                try
                {
                    var goal = _goalService.CreateGoal(session.Id, objective, budget, session.GetTotalTokens());
                    var budgetInfo = budget > 0 ? $" with budget {budget}" : " (no budget limit)";
                    return $"Goal created: \"{goal.Objective}\"{budgetInfo}";
                }
                catch (ArgumentException ex)
                {
                    return $"Error: {ex.Message}";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            case "pause":
            {
                var goal = _goalService.GetGoal(session.Id);
                if (goal is null) return "No active goal to pause.";
                try
                {
                    _goalService.UpdateStatus(session.Id, GoalStatus.Paused, subargs);
                    return "Goal paused. Resume with /goal resume.";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            case "resume":
            {
                var goal = _goalService.GetGoal(session.Id);
                if (goal is null) return "No goal to resume.";
                if (goal.Status.IsPursuable()) return "Goal is already active.";
                if (goal.Status.IsTerminal())
                    return "Cannot resume a completed goal. Start a new one with /goal start.";

                try
                {
                    _goalService.UpdateStatus(session.Id, GoalStatus.Active, subargs);
                    return "Goal resumed.";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            case "complete":
            case "done":
            {
                var goal = _goalService.GetGoal(session.Id);
                if (goal is null) return "No active goal.";
                try
                {
                    _goalService.UpdateStatus(session.Id, GoalStatus.Complete, subargs);
                    return "Goal marked as complete!";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            case "block":
            case "blocked":
            {
                var goal = _goalService.GetGoal(session.Id);
                if (goal is null) return "No active goal.";
                try
                {
                    _goalService.UpdateStatus(session.Id, GoalStatus.Blocked, subargs);
                    return "Goal marked as blocked. Resume with /goal resume.";
                }
                catch (InvalidOperationException ex)
                {
                    return $"Error: {ex.Message}";
                }
            }

            case "clear":
            {
                _goalService.ClearGoal(session.Id);
                return "Goal cleared.";
            }

            case "status":
            default:
            {
                var statusGoal = _goalService.GetGoal(session.Id);
                if (statusGoal is null)
                    return "No active goal. Use /goal start <objective> to create one.";

                var result = $"Goal Status: {statusGoal.Status.ToDisplayName()}\n" +
                             $"Objective: {statusGoal.Objective}\n" +
                             $"Tokens Used: {statusGoal.TokensUsed}";

                if (statusGoal.TokenBudget > 0)
                    result += $"\nBudget: {statusGoal.TokenBudget} (Remaining: {statusGoal.RemainingBudget})";

                if (!string.IsNullOrEmpty(statusGoal.StatusNote))
                    result += $"\nNote: {statusGoal.StatusNote}";

                result += $"\nContinuations: {statusGoal.ContinuationCount}/{SessionGoal.MaxContinuationsPerTurn}";

                return result;
            }
        }
    }
}
