using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Goal;

/// <summary>
/// Builds system prompts for Goal activation (turn start) and check (on stop).
/// </summary>
public static class GoalPromptTemplates
{
    /// <summary>
    /// Builds the activation prompt injected once at the start of each turn
    /// when a goal is active.
    /// </summary>
    public static string BuildActivationPrompt(SessionGoal goal)
    {
        return $"""
            **Active Goal**
            A session-scoped goal is now active with the following objective:
            <objective>{goal.Objective}</objective>

            **Your Behavior**
            - Treat the objective itself as your directive. Do NOT pause to ask the user what to do.
            - The system will automatically continue you if you stop before the goal is achieved.
            - When the goal is fully achieved, use the update_goal tool with status='complete'.
            - If you're genuinely blocked after repeated attempts, use update_goal with status='blocked'.

            **Completion Audit**
            Before declaring the goal complete, derive concrete requirements from the objective.
            For each requirement, identify authoritative evidence. Uncertain evidence means NOT achieved.
            """;
    }

    /// <summary>
    /// Builds the check prompt injected when the model stops and a goal is active.
    /// This prompt tells the model to review progress and continue working.
    /// </summary>
    public static string BuildCheckPrompt(SessionGoal goal, int iteration, int maxIterations)
    {
        var budgetLine = goal.TokenBudget > 0
            ? $"**Budget**: Used {goal.TokensUsed} / Budget {goal.TokenBudget} / Remaining {goal.RemainingBudget}"
            : "**Budget**: No limit set.";

        return $"""
            **Goal Check — Continue Working**
            You were working toward this objective: <objective>{goal.Objective}</objective>

            1. REVIEW all work done so far
            2. DETERMINE whether the objective has been FULLY achieved
            3. If ACHIEVED → use update_goal tool with status='complete'
            4. If NOT ACHIEVED → CONTINUE working without asking the user

            {budgetLine}
            **Fidelity**: Optimize for movement toward the requested end state. Do NOT substitute easier solutions.
            **Blocked Audit**: Only mark blocked after 3+ consecutive turns with the same blocker.
            Iteration: {iteration}/{maxIterations}
            """;
    }

    /// <summary>
    /// Formats the TUI footer line showing goal status.
    /// Delegates to GoalStatusExtensions in Core.
    /// </summary>
    public static string FormatGoalFooterLine(SessionGoal? goal)
        => GoalStatusExtensions.FormatGoalFooterLine(goal);

    /// <summary>
    /// Formats a TUI progress bar showing token usage.
    /// Delegates to GoalStatusExtensions in Core.
    /// </summary>
    public static string? FormatProgressBar(SessionGoal? goal)
        => GoalStatusExtensions.FormatGoalProgressBar(goal);
}
