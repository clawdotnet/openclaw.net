using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Service interface for managing session-scoped Goals.
/// Provides CRUD operations, state transitions, and token tracking.
/// Thread-safe for single-session CLI usage.
/// </summary>
public interface IGoalService
{
    /// <summary>Creates a new goal for the given session. Throws if a goal already exists.</summary>
    SessionGoal CreateGoal(string sessionId, string objective, long tokenBudget, long tokensAtStart);

    /// <summary>Gets the current goal for a session, or null if none exists.</summary>
    SessionGoal? GetGoal(string sessionId);

    /// <summary>
    /// Transitions the goal to a new status. Throws InvalidOperationException for invalid transitions.
    /// Valid transitions: Active→Paused, Active→Complete, Active→Blocked, Active→BudgetLimited,
    /// Active→UsageLimited, Paused→Active, Blocked→Active, BudgetLimited→Active, UsageLimited→Active.
    /// Terminal states (Complete) cannot transition.
    /// </summary>
    void UpdateStatus(string sessionId, GoalStatus newStatus, string? note = null);

    /// <summary>Updates token usage for the goal. Computes usage from session baseline.</summary>
    void UpdateTokenUsage(string sessionId, long sessionTotalTokens);

    /// <summary>
    /// Increments the per-turn auto-continuation count for a goal.
    /// Returns the updated count, or 0 if no goal exists.
    /// </summary>
    int IncrementContinuationCount(string sessionId);

    /// <summary>
    /// Records a turn hash for blocker detection.
    /// Returns true if the blocker threshold (3 consecutive same-hash) has been reached.
    /// </summary>
    bool RecordTurnHash(string sessionId, string normalizedText);

    /// <summary>Clears the goal for a session. No-op if no goal exists.</summary>
    void ClearGoal(string sessionId);

    /// <summary>Returns true if a goal exists and is in a pursuable state (Active).</summary>
    bool HasActiveGoal(string sessionId);

    /// <summary>
    /// Appends a completed/terminal goal record to the history file.
    /// No-op if GoalHistoryPersistence is not configured.
    /// </summary>
    void RecordGoalHistory(SessionGoal goal);
}
