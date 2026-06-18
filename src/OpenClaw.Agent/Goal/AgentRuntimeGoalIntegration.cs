using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Goal;

/// <summary>
/// Encapsulates the Goal integration with AgentRuntime.
/// Handles prompt injection, continuation checks, budget tracking, and blocker detection.
/// Designed to be called inline from AgentRuntime.RunAsync and RunStreamingAsync.
/// </summary>
public sealed class AgentRuntimeGoalIntegration
{
    private readonly IGoalService _goalService;
    private readonly ILogger? _logger;

    /// <summary>Session types where auto-continuation is allowed (interactive human presence).</summary>
    private static readonly HashSet<string> InteractiveChannelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "cli", "tui", "terminal", "console", "companion",
        "websocket", // WebChat UI — human interacting via browser
        "discord", "slack", "teams", "telegram", "signal", "whatsapp", "sms"
    };

    public AgentRuntimeGoalIntegration(IGoalService goalService, ILogger? logger = null)
    {
        _goalService = goalService ?? throw new ArgumentNullException(nameof(goalService));
        _logger = logger;
    }

    /// <summary>
    /// Builds the goal activation system prompt to inject at turn start.
    /// Returns null if no active goal exists.
    /// </summary>
    public string? BuildGoalSystemPrompt(string sessionId)
    {
        var goal = _goalService.GetGoal(sessionId);
        if (goal is null || !goal.Status.IsPursuable()) return null;

        return GoalPromptTemplates.BuildActivationPrompt(goal);
    }

    /// <summary>
    /// Updates token usage tracked for the current goal.
    /// </summary>
    public void UpdateGoalTokenUsage(Session session)
    {
        _goalService.UpdateTokenUsage(session.Id, session.GetTotalTokens());
    }

    /// <summary>
    /// Evaluates whether the agent should auto-continue after a stop.
    /// Returns a continuation prompt if auto-continue should fire, or null if not.
    /// </summary>
    /// <param name="session">The current session.</param>
    /// <param name="iteration">Current iteration index.</param>
    /// <param name="maxIterations">Maximum allowed iterations.</param>
    /// <param name="modelResponseText">The model's response text when it stopped.</param>
    public string? EvaluateGoalContinuation(
        Session session, int iteration, int maxIterations, string modelResponseText)
    {
        var goal = _goalService.GetGoal(session.Id);
        if (goal is null || !goal.Status.IsPursuable()) return null;

        // Check channel gating: only auto-continue in interactive channels
        if (!IsInteractiveChannel(session.ChannelId))
        {
            _logger?.LogDebug("Goal auto-continuation skipped for non-interactive channel: {ChannelId}", session.ChannelId);
            // Record goal history for the terminal state report
            return null;
        }

        // Check budget
        _goalService.UpdateTokenUsage(session.Id, session.GetTotalTokens());
        goal = _goalService.GetGoal(session.Id);
        if (goal is null || !goal.Status.IsPursuable()) return null;

        if (goal.IsBudgetExceeded)
        {
            _goalService.UpdateStatus(session.Id, GoalStatus.BudgetLimited, "Token budget exceeded");
            _logger?.LogInformation("Goal {SessionId} budget exceeded: {Used}/{Budget}",
                session.Id, goal.TokensUsed, goal.TokenBudget);
            return null;
        }

        // Check per-turn continuation limit
        var continuationCount = _goalService.IncrementContinuationCount(session.Id);
        if (continuationCount == 0) return null;
        if (continuationCount > SessionGoal.MaxContinuationsPerTurn)
        {
            _goalService.UpdateStatus(session.Id, GoalStatus.Paused,
                $"Auto-paused after {SessionGoal.MaxContinuationsPerTurn} continuations");
            _logger?.LogInformation("Goal {SessionId} auto-paused: exceeded continuation limit", session.Id);
            return null;
        }

        // Record turn hash for blocker detection
        var normalized = SessionGoal.NormalizeForComparison(modelResponseText);
        var isBlocked = _goalService.RecordTurnHash(session.Id, normalized);
        if (isBlocked)
        {
            _goalService.UpdateStatus(session.Id, GoalStatus.Blocked,
                "Same blocker repeated 3+ consecutive turns");
            _logger?.LogInformation("Goal {SessionId} blocked after 3+ same-blocker turns", session.Id);
            return null;
        }

        // Check iteration limit
        if (iteration >= maxIterations)
        {
            _logger?.LogInformation("Goal {SessionId} reached max iterations ({Max})", session.Id, maxIterations);
            return null;
        }

        // Build continuation prompt
        _logger?.LogInformation("Goal {SessionId} auto-continuing (iteration {Iter}/{Max}, continuation #{Cont})",
            session.Id, iteration, maxIterations, continuationCount);

        return GoalPromptTemplates.BuildCheckPrompt(goal, iteration, maxIterations);
    }

    /// <summary>
    /// Determines if the session channel is interactive (CLI/TUI).
    /// Auto-continuation only fires for interactive channels per the outside voice resolution.
    /// </summary>
    private static bool IsInteractiveChannel(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return true; // Default to interactive if unknown
        return InteractiveChannelIds.Contains(channelId);
    }
}
