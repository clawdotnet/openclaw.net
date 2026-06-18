using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Model tool: creates a new goal for the session.
/// Restricted — only works when explicitly directed by the user/system.
/// Fails if a goal already exists for the session.
/// </summary>
public sealed class CreateGoalTool : IToolWithContext
{
    private readonly IGoalService _goalService;

    public CreateGoalTool(IGoalService goalService)
    {
        _goalService = goalService ?? throw new ArgumentNullException(nameof(goalService));
    }

    public string Name => "create_goal";
    public string Description => "Create a new session goal with an objective and optional token budget. Fails if a goal already exists.";
    public string ParameterSchema => """
        {"type":"object","properties":{"objective":{"type":"string","description":"The goal objective — what to achieve."},"token_budget":{"type":"integer","description":"Optional token budget (e.g., 500000 for 500k). 0 or omitted means unlimited."}},"required":["objective"]}
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        return ValueTask.FromResult("Error: create_goal requires session context.");
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var objective = args.RootElement.GetProperty("objective").GetString()!;
        var tokenBudget = args.RootElement.TryGetProperty("token_budget", out var tb) ? tb.GetInt64() : 0L;

        if (string.IsNullOrWhiteSpace(objective))
            return ValueTask.FromResult("Error: objective cannot be empty.");

        try
        {
            var goal = _goalService.CreateGoal(
                context.Session.Id, objective, Math.Max(0, tokenBudget),
                context.Session.GetTotalTokens());
            return ValueTask.FromResult($"Goal created. Status: {goal.Status.ToDisplayName()}. Objective: {goal.Objective}");
        }
        catch (InvalidOperationException ex)
        {
            return ValueTask.FromResult($"Error: {ex.Message}");
        }
    }
}
