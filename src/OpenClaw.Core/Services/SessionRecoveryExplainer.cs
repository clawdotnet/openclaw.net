using OpenClaw.Core.Models;
using OpenClaw.Core.Abstractions;
using System.Text.Json;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Core.Services;

public static class SessionRecoveryExplainer
{
    public static SessionRecoveryExplanation ExplainWithGoalStore(Session session, IGoalService? goals,
        IEnumerable<ToolApprovalRequest> approvals)
    {
        try { return Explain(session, goals?.GetGoal(session.Id), approvals); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new() { Status = "unknown", Summary = "Goal state could not be loaded.",
                Evidence = ["Persisted goal data is unavailable or corrupt; session history remains available."],
                NextSteps = ["Inspect and restore the goal data before resuming. Do not treat unavailable goal state as a completed or absent goal."] };
        }
    }

    public static SessionRecoveryExplanation Explain(Session session, SessionGoal? goal,
        IEnumerable<ToolApprovalRequest> pendingApprovals)
    {
        var evidence = new List<string> { $"Recorded run state: {session.RunState}." };
        var approvals = pendingApprovals.Where(a => a.SessionId == session.Id).ToArray();
        if (goal is not null)
        {
            evidence.Add($"Goal: {goal.Status.ToDisplayName()}; tokens used: {goal.TokensUsed}; budget: {(goal.TokenBudget > 0 ? goal.TokenBudget.ToString() : "unlimited")}.");
            if (!string.IsNullOrWhiteSpace(goal.StatusNote))
                evidence.Add($"Goal status note: {goal.StatusNote}");
        }
        if (session.BackgroundRun is { } run)
        {
            evidence.Add($"Background continuations: {run.ContinuationCount}.");
            if (!string.IsNullOrWhiteSpace(run.LastStopReason))
                evidence.Add($"Last recorded stop reason: {run.LastStopReason}.");
        }
        if (session.ExecutionCheckpoint is { } checkpoint)
            evidence.Add($"Checkpoint {checkpoint.CheckpointId}: {checkpoint.State}; {checkpoint.ToolCalls.Count} tool result(s); recorded {checkpoint.CreatedAtUtc:O}. A checkpoint does not prove the outcome of actions interrupted before it was saved.");
        if (session.RunState is SessionRunState.Failed or SessionRunState.Blocked || goal?.Status == GoalStatus.Blocked)
        {
            var lastToolTurn = session.History.LastOrDefault(turn => turn.Role == "assistant");
            if (session.BackgroundRun is { } background && lastToolTurn?.Timestamp < background.LastContinuedAtUtc)
                lastToolTurn = null;
            foreach (var tool in (lastToolTurn?.ToolCalls ?? []).Where(tool =>
                         !string.IsNullOrWhiteSpace(tool.FailureCode) || !string.IsNullOrWhiteSpace(tool.FailureMessage)).Take(5))
                evidence.Add($"Last tool batch ({lastToolTurn!.Timestamp:O}): {tool.ToolName}; {tool.FailureCode ?? "failure"}; {tool.FailureMessage ?? "No failure message recorded."}");
        }
        foreach (var approval in approvals)
            evidence.Add($"Pending approval {approval.ApprovalId}: {approval.ToolName}.");

        SessionRecoveryExplanation Result(string status, string summary, params string[] nextSteps)
            => new() { Status = status, Summary = summary, Evidence = evidence.ToArray(), NextSteps = nextSteps };

        if (approvals.Length > 0)
            return Result("approval_pending", "This session has tool actions awaiting an operator decision.",
                "Open Approvals and inspect the matching approval IDs. Approve or reject each action according to its intended effect.",
                "Refresh this session after the decision; an approval can expire while this view is open.");
        if (session.State == SessionState.Expired)
            return Result("expired", "This session is expired.", "Inspect its history and start a new session if more work is needed.");
        if (session.State == SessionState.Paused)
            return Result("session_paused", "The session itself is paused.", "Review the session policy and history before resuming work through its original channel.");
        // Run-level limits still apply even if a goal remains active.
        if (session.RunState == SessionRunState.BudgetLimited || goal?.Status == GoalStatus.BudgetLimited || (goal?.IsBudgetExceeded == true && goal.Status != GoalStatus.Complete))
        {
            if (session.BackgroundRun?.LastStopReason == "MaxContinuationTurnsReached")
                return Result("budget_limited", "The background continuation limit stopped automatic progress.",
                    "Preserve the completed work and start a new session for further automatic continuation. A follow-up in this session runs one turn but does not reset its exhausted continuation limit.");
            return Result("budget_limited", "A recorded budget limit stopped automatic progress.",
                "Review token usage and the configured limits before continuing. Resuming a goal does not increase its token budget.",
                "Preserve the current objective and results before replacing an exhausted goal with a newly budgeted goal.");
        }
        if (goal?.Status == GoalStatus.UsageLimited)
            return Result("usage_limited", "The goal is stopped by a recorded usage limit.", "Check provider availability and usage limits. After resolving them, send /goal resume in this session, then a follow-up message.");
        if (session.RunState == SessionRunState.Failed)
            return Result("failed", "The last run failed; review the evidence before retrying.",
                "Inspect the session timeline and failed tool results to identify the cause.",
                "Verify whether any interrupted external action took effect before sending a follow-up. Do not blindly repeat it.");
        if (session.RunState == SessionRunState.Blocked || goal?.Status == GoalStatus.Blocked)
            return Result("blocked", "Progress is blocked. The recorded goal note and timeline contain the available explanation.",
                "Resolve the missing input, access, or external condition shown in the evidence.",
                goal?.Status == GoalStatus.Blocked ? "Send /goal resume in this session, then a follow-up describing what changed." : "Send a follow-up in this session describing what changed.");
        if (session.RunState == SessionRunState.Paused || goal?.Status == GoalStatus.Paused)
            return Result("paused", "Automatic progress is paused.",
                goal?.Status == GoalStatus.Paused ? "Review the goal status note, send /goal resume in this session, then a follow-up message." : "Review the timeline and send a follow-up when ready to continue.");
        if (session.RunState is SessionRunState.Running or SessionRunState.Continuing)
            return Result("in_progress", "The recorded state indicates work in progress; it is not proof of a live worker.", "Refresh the timeline to check for new activity before sending duplicate work.");
        if (goal?.Status == GoalStatus.Active)
            return Result("goal_active", "The goal is active, but the session has no recorded run in progress.", "Send a follow-up in this session to continue the active goal.");
        if (goal?.Status == GoalStatus.Complete || session.RunState == SessionRunState.Completed)
            return Result("completed", "The recorded run or goal is complete.", "Review the result and its evidence. Start a new goal for additional work.");
        return Result("idle", "No active run or recorded blocker is available.", "Send a message in this session to start or continue work.");
    }
}
