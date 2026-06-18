using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Model tool: reads the current goal state for the session.
/// Read-only — no side effects. Always available when a goal service is registered.
/// </summary>
public sealed class GetGoalTool : IToolWithContext
{
    private readonly IGoalService _goalService;

    public GetGoalTool(IGoalService goalService)
    {
        _goalService = goalService ?? throw new ArgumentNullException(nameof(goalService));
    }

    public string Name => "get_goal";
    public string Description => "Read the current session goal: status, objective, token usage, and budget.";
    public string ParameterSchema => """{"type":"object","properties":{},"required":[]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        return ValueTask.FromResult("Error: get_goal requires session context. Use the default parameterless call.");
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        var sessionId = context.Session.Id;
        var goal = _goalService.GetGoal(sessionId);

        if (goal is null)
            return ValueTask.FromResult("No active goal for this session.");

        var result = $"""
            Status: {goal.Status.ToDisplayName()}
            Objective: {goal.Objective}
            Tokens Used: {goal.TokensUsed}
            """;

        if (goal.TokenBudget > 0)
            result += $"\nToken Budget: {goal.TokenBudget}\nRemaining: {goal.RemainingBudget}";

        if (!string.IsNullOrEmpty(goal.StatusNote))
            result += $"\nNote: {goal.StatusNote}";

        return ValueTask.FromResult(result);
    }
}
