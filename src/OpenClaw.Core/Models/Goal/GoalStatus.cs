namespace OpenClaw.Core.Models.Goal;

/// <summary>
/// Defines the possible states of a session-scoped Goal.
/// Matches upstream OpenClaw's 6-state goal machine.
/// </summary>
public enum GoalStatus : byte
{
    /// <summary>Goal is active and being pursued. The runtime will auto-continue on stop.</summary>
    Active = 0,

    /// <summary>Operator has paused the goal. No auto-continuation until resumed.</summary>
    Paused = 1,

    /// <summary>Model encountered a genuine blocker repeated 3+ turns. No auto-continuation.</summary>
    Blocked = 2,

    /// <summary>Token budget was exhausted before the goal was achieved. Operator can resume with more budget.</summary>
    BudgetLimited = 3,

    /// <summary>System-level usage limit reached (reserved for future use).</summary>
    UsageLimited = 4,

    /// <summary>Goal has been achieved. Terminal state — cannot transition out without clear.</summary>
    Complete = 5,
}

/// <summary>
/// Utility methods for GoalStatus.
/// </summary>
public static class GoalStatusExtensions
{
    /// <summary>
    /// Returns true if the status allows pursuable auto-continuation.
    /// Only Active is pursuable — other states block continuation.
    /// </summary>
    public static bool IsPursuable(this GoalStatus status) => status == GoalStatus.Active;

    /// <summary>
    /// Returns true if the status is a terminal state (cannot transition out without clear).
    /// </summary>
    public static bool IsTerminal(this GoalStatus status) => status == GoalStatus.Complete;

    /// <summary>
    /// Returns a human-readable display name for the status.
    /// </summary>
    public static string ToDisplayName(this GoalStatus status) => status switch
    {
        GoalStatus.Active => "Active",
        GoalStatus.Paused => "Paused",
        GoalStatus.Blocked => "Blocked",
        GoalStatus.BudgetLimited => "Budget Limited",
        GoalStatus.UsageLimited => "Usage Limited",
        GoalStatus.Complete => "Complete",
        _ => "Unknown",
    };

    /// <summary>
    /// Formats the TUI footer line showing goal status.
    /// </summary>
    public static string FormatGoalFooterLine(SessionGoal? goal)
    {
        if (goal is null) return string.Empty;

        return goal.Status switch
        {
            GoalStatus.Active when goal.TokenBudget > 0 =>
                $"Pursuing goal ({goal.TokensUsed}/{goal.TokenBudget})",
            GoalStatus.Active =>
                $"Pursuing goal: {Truncate(goal.Objective, 40)}",
            GoalStatus.Paused =>
                "Goal paused (/goal resume)",
            GoalStatus.Blocked =>
                "Goal blocked (/goal resume)",
            GoalStatus.BudgetLimited =>
                $"Goal unmet ({goal.TokensUsed}/{goal.TokenBudget})",
            GoalStatus.UsageLimited =>
                "Goal hit usage limits (/goal resume)",
            GoalStatus.Complete =>
                $"Goal achieved ({goal.TokensUsed})",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Formats a TUI progress bar showing token usage.
    /// Returns null if no budget is set.
    /// </summary>
    public static string? FormatGoalProgressBar(SessionGoal? goal)
    {
        if (goal is null || goal.TokenBudget <= 0) return null;

        var pct = Math.Clamp((double)goal.TokensUsed / goal.TokenBudget, 0, 1);
        var barWidth = 20;
        var filled = (int)(pct * barWidth);
        var empty = barWidth - filled;

        var bar = new char[barWidth + 2];
        bar[0] = '[';
        for (int i = 0; i < filled; i++) bar[i + 1] = '=';
        if (filled < barWidth) bar[filled + 1] = '>';
        for (int i = filled + 2; i <= barWidth; i++) bar[filled + 1] = ' ';
        bar[barWidth + 1] = ']';

        return $"{new string(bar)} {pct * 100:F0}% ({goal.TokensUsed}/{goal.TokenBudget})";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
