using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Core.Services;

/// <summary>
/// Thread-safe goal service with optional atomic local persistence.
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
    private readonly string? _stateDirectory;

    public InMemoryGoalService(ILogger<InMemoryGoalService>? logger = null, string? historyFilePath = null, string? stateDirectory = null)
    {
        _logger = logger;
        _historyFilePath = historyFilePath;
        _stateDirectory = stateDirectory is null ? null : Path.GetFullPath(stateDirectory);
    }

    public SessionGoal CreateGoal(string sessionId, string objective, long tokenBudget, long tokensAtStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        if (objective.Length > SessionGoal.MaxObjectiveLength)
            throw new ArgumentException($"Objective exceeds max length of {SessionGoal.MaxObjectiveLength} characters.");
        if (tokenBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), "Token budget cannot be negative.");
        if (tokensAtStart < 0)
            throw new ArgumentOutOfRangeException(nameof(tokensAtStart), "Token baseline cannot be negative.");

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
            if (GetGoal(sessionId) is not null)
            {
                _logger?.LogWarning("Goal already exists for session {SessionId}", sessionId);
                throw new InvalidOperationException($"A goal already exists for session '{sessionId}'. Clear it first.");
            }
            Persist(goal);
            _goals[sessionId] = goal;
        }

        _logger?.LogInformation("Goal created for session {SessionId} with budget {TokenBudget}", sessionId, tokenBudget);
        return goal;
    }

    public SessionGoal? GetGoal(string sessionId)
    {
        lock (_sessionLocks.GetOrAdd(sessionId, static _ => new object()))
        {
            if (_goals.TryGetValue(sessionId, out var goal)) return goal;
            var path = StatePath(sessionId);
            if (path is null || !File.Exists(path)) return null;
            goal = JsonSerializer.Deserialize(File.ReadAllText(path), GoalJsonContext.Default.SessionGoal)
                ?? throw new InvalidDataException("Goal state is empty.");
            if (goal.SessionId != sessionId || !Enum.IsDefined(goal.Status) || goal.TokenBudget < 0 || goal.TokensUsed < 0)
                throw new InvalidDataException("Invalid persisted goal state.");
            _goals[sessionId] = goal;
            return goal;
        }
    }

    public void UpdateStatus(string sessionId, GoalStatus newStatus, string? note = null)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (GetGoal(sessionId) is not { } goal)
                throw new InvalidOperationException($"No goal found for session '{sessionId}'.");

            if (goal.Status.IsTerminal())
                throw new InvalidOperationException($"Cannot transition from terminal state '{goal.Status.ToDisplayName()}'.");

            if (!IsValidTransition(goal.Status, newStatus))
                throw new InvalidOperationException($"Invalid transition: {goal.Status.ToDisplayName()} -> {newStatus.ToDisplayName()}.");

            if (newStatus == GoalStatus.Active && goal.Status != GoalStatus.Active)
            {
                goal.ContinuationCount = 0;
                goal.ConsecutiveBlockerCount = 0;
                goal.LastBlockerHash = null;
            }
            goal.Status = newStatus;
            goal.UpdatedAt = DateTime.UtcNow;
            goal.StatusNote = note;
            Persist(goal);

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
            if (GetGoal(sessionId) is not { } goal) return;

            // Usage = session total at check time - baseline at goal creation
            goal.TokensUsed = Math.Max(goal.TokensUsed, Math.Max(0, sessionTotalTokens - goal.TokensAtStart));
            goal.UpdatedAt = DateTime.UtcNow;
            Persist(goal);
        }
    }

    public int IncrementContinuationCount(string sessionId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (GetGoal(sessionId) is not { } goal) return 0;

            goal.ContinuationCount++;
            goal.UpdatedAt = DateTime.UtcNow;
            Persist(goal);
            return goal.ContinuationCount;
        }
    }

    public bool RecordTurnHash(string sessionId, string normalizedText)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            if (GetGoal(sessionId) is not { } goal) return false;

            var hash = SessionGoal.ComputeTurnHash(normalizedText);
            if (string.IsNullOrEmpty(hash))
            {
                goal.LastBlockerHash = null;
                goal.ConsecutiveBlockerCount = 0;
                Persist(goal);
                return false;
            }

            if (hash == goal.LastBlockerHash)
            {
                goal.ConsecutiveBlockerCount++;
                _logger?.LogDebug("Blocker hash repeated: {Count}/3 for session {SessionId}",
                    goal.ConsecutiveBlockerCount, sessionId);
                Persist(goal);
                return goal.ConsecutiveBlockerCount >= 3;
            }

            // Blocker changed or first recorded turn
            goal.LastBlockerHash = hash;
            goal.ConsecutiveBlockerCount = 1;
            Persist(goal);
            return false;
        }
    }

    public void ClearGoal(string sessionId)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new object());
        lock (sessionLock)
        {
            _ = GetGoal(sessionId);
            var path = StatePath(sessionId);
            if (path is not null) File.Delete(path);
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
            return GetGoal(sessionId)?.Status.IsPursuable() == true;
        }
    }

    public void BeginTurn(string sessionId)
    {
        lock (_sessionLocks.GetOrAdd(sessionId, static _ => new object()))
        {
            if (GetGoal(sessionId) is not { } goal) return;
            goal.ContinuationCount = 0;
            Persist(goal);
        }
    }

    public void UpdateModelStatus(string sessionId, GoalStatus newStatus, string? note = null)
    {
        lock (_sessionLocks.GetOrAdd(sessionId, static _ => new object()))
        {
            var goal = GetGoal(sessionId) ?? throw new InvalidOperationException("No goal found.");
            if (newStatus is not (GoalStatus.Complete or GoalStatus.Blocked))
                throw new InvalidOperationException("The model can only complete or block goals.");
            if (newStatus == GoalStatus.Blocked && goal.ConsecutiveBlockerCount < 3)
                throw new InvalidOperationException("Blocking requires three consecutive observations of the same blocker. Continue working or report the blocker for this turn.");
            UpdateStatus(sessionId, newStatus, note);
        }
    }

    private string? StatePath(string sessionId) => _stateDirectory is null ? null :
        Path.Combine(_stateDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))) + ".json");

    private void Persist(SessionGoal goal)
    {
        var path = StatePath(goal.SessionId);
        if (path is null) return;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_stateDirectory!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, goal, GoalJsonContext.Default.SessionGoal);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Reload the last durable state on the next access; never acknowledge an unpersisted transition.
            _goals.TryRemove(goal.SessionId, out _);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
