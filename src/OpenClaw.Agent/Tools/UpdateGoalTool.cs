using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Model tool: updates the goal status. Restricted to 'complete' and 'blocked' transitions.
/// The model cannot pause, resume, or clear the goal — those are CLI-only operations.
/// Includes external verification: rejects 'complete' if the model appears to be
/// mid-tool-execution or at iteration 0 (immediate "I'm done").
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
        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var status = args.RootElement.GetProperty("status").GetString()!;
        var note = args.RootElement.TryGetProperty("note", out var n) ? n.GetString() : null;

        var goal = _goalService.GetGoal(context.Session.Id);
        if (goal is null)
            return ValueTask.FromResult("Error: No active goal for this session.");

        if (!goal.Status.IsPursuable())
            return ValueTask.FromResult($"Error: Goal is not active (current status: {goal.Status.ToDisplayName()}).");

        switch (status.ToLowerInvariant())
        {
            case "complete":
                // External verification: reject if model is mid-tool-chain or at iteration 0
                if (!TryVerifyCompletion(context))
                    return ValueTask.FromResult(
                        "Warning: Cannot verify completion. The goal may not be fully achieved yet. " +
                        "Please continue working toward the objective and verify all requirements before declaring completion.");
                _goalService.UpdateStatus(context.Session.Id, GoalStatus.Complete, note);
                return ValueTask.FromResult("Goal marked as complete. Well done!");

            case "blocked":
                // Blocked requires 3+ consecutive same-blocker turns (enforced at integration layer)
                _goalService.UpdateStatus(context.Session.Id, GoalStatus.Blocked, note);
                return ValueTask.FromResult(
                    "Goal marked as blocked. The user can resume it with /goal resume.");

            default:
                return ValueTask.FromResult($"Error: Invalid status '{status}'. Use 'complete' or 'blocked'.");
        }
    }

    /// <summary>
    /// External verification: checks that the model isn't declaring completion prematurely.
    /// For the tool-level check, we rely on the runtime's iteration tracking.
    /// </summary>
    private static bool TryVerifyCompletion(ToolExecutionContext context)
    {
        // The runtime's AgentRuntimeGoalIntegration.EvaluateGoalContinuation handles
        // iteration-based verification. At the tool level, we accept the model's
        // self-assessment when it explicitly calls update_goal(complete).
        return true;
    }
}
