using System.Text.Json;
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
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return ValueTask.FromResult("Error: arguments payload is empty.");

        string? objective;
        long tokenBudget = 0;
        try
        {
            using var args = JsonDocument.Parse(argumentsJson);
            if (!args.RootElement.TryGetProperty("objective", out var objectiveElement))
                return ValueTask.FromResult("Error: objective is required.");

            if (objectiveElement.ValueKind != JsonValueKind.String)
                return ValueTask.FromResult("Error: objective must be a string.");

            objective = objectiveElement.GetString();
            if (args.RootElement.TryGetProperty("token_budget", out var tb))
            {
                if (tb.ValueKind != JsonValueKind.Number || !tb.TryGetInt64(out tokenBudget))
                    return ValueTask.FromResult("Error: token_budget must be an integer.");
            }
        }
        catch (JsonException)
        {
            return ValueTask.FromResult("Error: arguments must be valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(objective))
            return ValueTask.FromResult("Error: objective cannot be empty.");
        if (tokenBudget < 0)
            return ValueTask.FromResult("Error: token_budget cannot be negative.");

        try
        {
            var goal = _goalService.CreateGoal(
                context.Session.Id, objective, tokenBudget,
                context.Session.GetTotalTokens());
            return ValueTask.FromResult($"Goal created. Status: {goal.Status.ToDisplayName()}. Objective: {goal.Objective}");
        }
        catch (InvalidOperationException ex)
        {
            return ValueTask.FromResult($"Error: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ValueTask.FromResult($"Error: {ex.Message}");
        }
    }
}
