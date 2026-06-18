using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Core.Services;

/// <summary>
/// Thread-safe in-memory implementation of IGoalService.
/// Stores goals in a ConcurrentDictionary keyed by session ID.
/// Single-goal-per-session constraint enforced at the service level.
/// </summary>
public sealed class InMemoryGoalService : IGoalService
{
    private readonly ConcurrentDictionary<string, SessionGoal> _goals = new();
    private readonly ConcurrentDictionary<string, object> _sessionLocks = new();
    private readonly object _historyWriteLock = new();
    private readonly ILogger<InMemoryGoalService>? _logger;
    private readonly string? _historyFilePath;

    public InMemoryGoalService(ILogger<InMemoryGoalService>? logger = null, string? historyFilePath = null)
    {
        _logger = logger;
        _historyFilePath = historyFilePath;
    }

    public SessionGoal CreateGoal(string sessionId, string objective, long tokenBudget, long tokensAtStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        if (objective.Length > SessionGoal.MaxObjectiveLength)
            throw new ArgumentException($"Objective exceeds max length of {SessionGoal.MaxObjectiveLength} characters.");
        if (tokenBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), "Token budget cannot be negative.");

        var goal = new SessionGoal
        {
            SessionId = sessionId,
            Objective = objective,
            TokenBudget = tokenBudget,
            TokensAtStart = tokensAtStart,
        };

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (!_goals.TryAdd(sessionId, goal))
            {
                _logger?.LogWarning("Goal already exists for session {SessionId}", sessionId);
                throw new InvalidOperationException($"A goal already exists for session '{sessionId}'. Clear it first.");
            }
        }

        _logger?.LogInformation("Goal created for session {SessionId} with budget {TokenBudget}", sessionId, tokenBudget);
        return goal;
    }

    public SessionGoal? GetGoal(string sessionId)
    {
        _goals.TryGetValue(sessionId, out var goal);
        return goal;
    }

    public void UpdateStatus(string sessionId, GoalStatus newStatus, string? note = null)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (!_goals.TryGetValue(sessionId, out var goal))
                throw new InvalidOperationException($"No goal found for session '{sessionId}'.");

            if (goal.Status.IsTerminal())
                throw new InvalidOperationException($"Cannot transition from terminal state '{goal.Status.ToDisplayName()}'.");

            if (!IsValidTransition(goal.Status, newStatus))
                throw new InvalidOperationException($"Invalid transition: {goal.Status.ToDisplayName()} -> {newStatus.ToDisplayName()}.");

            goal.Status = newStatus;
            goal.UpdatedAt = DateTime.UtcNow;
            goal.StatusNote = note;

            _logger?.LogInformation("Goal {SessionId} status: {Status}", sessionId, newStatus.ToDisplayName());

            if (newStatus.IsTerminal() || newStatus is GoalStatus.Blocked or GoalStatus.BudgetLimited)
            {
                RecordGoalHistory(goal);
            }
        }
    }

    public void UpdateTokenUsage(string sessionId, long sessionTotalTokens)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (!_goals.TryGetValue(sessionId, out var goal)) return;

            // Usage = session total at check time - baseline at goal creation
            goal.TokensUsed = Math.Max(0, sessionTotalTokens - goal.TokensAtStart);
            goal.UpdatedAt = DateTime.UtcNow;
        }
    }

    public int IncrementContinuationCount(string sessionId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (!_goals.TryGetValue(sessionId, out var goal)) return 0;

            goal.ContinuationCount++;
            goal.UpdatedAt = DateTime.UtcNow;
            return goal.ContinuationCount;
        }
    }

    public bool RecordTurnHash(string sessionId, string normalizedText)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (!_goals.TryGetValue(sessionId, out var goal)) return false;

            var hash = SessionGoal.ComputeTurnHash(normalizedText);
            if (string.IsNullOrEmpty(hash))
            {
                goal.LastBlockerHash = null;
                goal.ConsecutiveBlockerCount = 0;
                return false;
            }

            if (hash == goal.LastBlockerHash)
            {
                goal.ConsecutiveBlockerCount++;
                _logger?.LogDebug("Blocker hash repeated: {Count}/3 for session {SessionId}",
                    goal.ConsecutiveBlockerCount, sessionId);
                return goal.ConsecutiveBlockerCount >= 3;
            }

            // Blocker changed or first recorded turn
            goal.LastBlockerHash = hash;
            goal.ConsecutiveBlockerCount = 1;
            return false;
        }
    }

    public void ClearGoal(string sessionId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (_goals.TryRemove(sessionId, out var goal))
            {
                _logger?.LogInformation("Goal cleared for session {SessionId}", sessionId);
                // Record history for non-terminal goals that are being cleared
                if (!goal.Status.IsTerminal())
                {
                    RecordGoalHistory(goal);
                }
            }
        }
    }

    public bool HasActiveGoal(string sessionId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            return _goals.TryGetValue(sessionId, out var goal) && goal.Status.IsPursuable();
        }
    }

    public void RecordGoalHistory(SessionGoal goal)
    {
        if (_historyFilePath is null) return;

        try
        {
            var dir = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var record = new GoalHistoryRecord
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                SessionId = goal.SessionId,
                Objective = goal.Objective,
                Status = goal.Status.ToDisplayName(),
                TokenBudget = goal.TokenBudget,
                TokensUsed = goal.TokensUsed,
                ContinuationCount = goal.ContinuationCount,
                CreatedAt = goal.CreatedAt.ToString("O"),
            };

            lock (_historyWriteLock)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(record, GoalJsonContext.Default.GoalHistoryRecord);
                File.AppendAllText(_historyFilePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record goal history for session {SessionId}", goal.SessionId);
        }
    }

    /// <summary>
    /// Validates state transitions per the 6-state state machine.
    /// Transitions not listed here are invalid.
    /// </summary>
    private static bool IsValidTransition(GoalStatus current, GoalStatus next)
    {
        if (current == next) return true; // No-op is always valid

        return (current, next) switch
        {
            (GoalStatus.Active, GoalStatus.Paused) => true,
            (GoalStatus.Active, GoalStatus.Blocked) => true,
            (GoalStatus.Active, GoalStatus.BudgetLimited) => true,
            (GoalStatus.Active, GoalStatus.UsageLimited) => true,
            (GoalStatus.Active, GoalStatus.Complete) => true,
            (GoalStatus.Paused, GoalStatus.Active) => true,
            (GoalStatus.Blocked, GoalStatus.Active) => true,
            (GoalStatus.BudgetLimited, GoalStatus.Active) => true,
            (GoalStatus.UsageLimited, GoalStatus.Active) => true,
            _ => false,
        };
    }
}
