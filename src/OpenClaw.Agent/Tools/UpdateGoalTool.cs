using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Model tool: updates the goal status. Restricted to 'complete' and 'blocked' transitions.
/// The model cannot pause, resume, or clear the goal — those are CLI-only operations.
/// Requires completed assistant work before accepting a model completion.
/// The executor rejects status updates mixed into a batch with other tools.
/// </summary>
public sealed class UpdateGoalTool : IToolWithContext
{
    private readonly IGoalService _goalService;

    public UpdateGoalTool(IGoalService goalService)
    {
        _goalService = goalService ?? throw new ArgumentNullException(nameof(goalService));
    }

    public string Name => "update_goal";
    public string Description => "Update the goal status. Only 'complete' (goal achieved) or 'blocked' (genuinely stuck) are allowed. Cannot pause, resume, or clear the goal.";
    public string ParameterSchema => """
        {"type":"object","properties":{"status":{"type":"string","enum":["complete","blocked"],"description":"New status: 'complete' when fully achieved, 'blocked' when genuinely stuck after 3+ attempts."},"note":{"type":"string","description":"Optional note explaining the status change."}},"required":["status"]}
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        return ValueTask.FromResult("Error: update_goal requires session context.");
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return ValueTask.FromResult("Error: arguments payload is empty.");

        string? status;
        string? note = null;
        try
        {
            using var args = JsonDocument.Parse(argumentsJson);
            if (!args.RootElement.TryGetProperty("status", out var statusElement))
                return ValueTask.FromResult("Error: status is required.");

            if (statusElement.ValueKind != JsonValueKind.String)
                return ValueTask.FromResult("Error: status must be a string.");

            status = statusElement.GetString();
            if (args.RootElement.TryGetProperty("note", out var n))
            {
                if (n.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                    return ValueTask.FromResult("Error: note must be a string.");
                note = n.GetString();
            }
        }
        catch (JsonException)
        {
            return ValueTask.FromResult("Error: arguments must be valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(status))
            return ValueTask.FromResult("Error: status is required.");

        var goal = _goalService.GetGoal(context.Session.Id);
        if (goal is null)
            return ValueTask.FromResult("Error: No active goal for this session.");

        if (!goal.Status.IsPursuable())
            return ValueTask.FromResult($"Error: Goal is not active (current status: {goal.Status.ToDisplayName()}).");

        try
        {
            _goalService.UpdateTokenUsage(context.Session.Id, context.Session.GetTotalTokens());
            switch (status.ToLowerInvariant())
            {
                case "complete":
                    // Require completed work evidence before a model-initiated completion.
                    if (!TryVerifyCompletion(context))
                        return ValueTask.FromResult(
                            "Warning: Cannot verify completion. The goal may not be fully achieved yet. " +
                            "Please continue working toward the objective and verify all requirements before declaring completion.");
                    _goalService.UpdateModelStatus(context.Session.Id, GoalStatus.Complete, note);
                    return ValueTask.FromResult("Goal marked as complete. Well done!");

                case "blocked":
                    _goalService.UpdateModelStatus(context.Session.Id, GoalStatus.Blocked, note);
                    return ValueTask.FromResult(
                        "Goal marked as blocked. The user can resume it with /goal resume.");

                default:
                    return ValueTask.FromResult($"Error: Invalid status '{status}'. Use 'complete' or 'blocked'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            return ValueTask.FromResult($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// External verification: checks that the model isn't declaring completion prematurely.
    /// Accepts completed tool batches as well as assistant text.
    /// </summary>
    private static bool TryVerifyCompletion(ToolExecutionContext context)
    {
        var latestAssistantTurn = context.Session.History.LastOrDefault(static turn =>
            string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (latestAssistantTurn is null)
            return false;

        var content = latestAssistantTurn.Content?.Trim();
        if (string.IsNullOrEmpty(content))
            return false;

        if (!string.Equals(content, "[tool_use]", StringComparison.Ordinal))
            return true;

        // Completed work is evidence, not a marker of in-flight execution.
        return latestAssistantTurn.ToolCalls is { Count: > 0 } calls
            && calls.All(call => call.ResultStatus is null or ToolResultStatuses.Completed)
            && calls.Any(call => call.ToolName is not ("create_goal" or "get_goal" or "update_goal"));
    }
}
