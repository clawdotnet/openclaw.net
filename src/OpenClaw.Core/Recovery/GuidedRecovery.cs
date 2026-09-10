using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Actions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;

namespace OpenClaw.Core.Recovery;

public sealed class RecoveryControls
{
    public string Revision { get; init; } = "";
    public bool CanMutate { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public string Message { get; init; } = "";
    public List<RecoveryActionItem> Actions { get; init; } = [];
}
public sealed record RecoveryActionItem(string Id, string ToolName, string State, long Revision);
public sealed class RecoveryRequest
{
    public string Revision { get; set; } = "";
    public string Command { get; set; } = "";
    public string? ActionId { get; set; }
    public long ActionRevision { get; set; }
    public string? Evidence { get; set; }
    public string? Result { get; set; }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecoveryControls))]
[JsonSerializable(typeof(RecoveryRequest))]
public partial class RecoveryJsonContext : JsonSerializerContext;

public static class GuidedRecovery
{
    public static RecoveryControls Describe(Session session, SessionGoal? goal, IReadOnlyList<ActionRecord> actions,
        bool canMutate, bool running, bool pendingApproval, long sessionTokenBudget = 0)
    {
        var recorded = session.History.SelectMany(t => t.ToolCalls ?? []).Select(c => c.CallId).ToHashSet();
        var unresolved = actions.Where(a => a.State == "started" || (a.State == "completed" && !a.HistoryPersisted && !recorded.Contains(a.CallId))).ToArray();
        var totalTokens = session.TotalInputTokens + session.TotalOutputTokens;
        var budgetExceeded = session.BackgroundRun?.LastStopReason == "MaxContinuationTurnsReached" || (sessionTokenBudget > 0 && totalTokens >= sessionTokenBudget) ||
            (goal is { TokenBudget: > 0 } && Math.Max(goal.TokensUsed, totalTokens - goal.TokensAtStart) >= goal.TokenBudget) || (session.BackgroundRun is { TokenBudget: > 0 } background &&
            session.TotalInputTokens + session.TotalOutputTokens >= background.TokenBudget);
        var canResume = !running && !pendingApproval && unresolved.Length == 0 && !budgetExceeded && session.State == SessionState.Active &&
            goal is { Status: GoalStatus.Paused or GoalStatus.Blocked };
        var revisionText = $"{session.Id}|{session.State}|{session.RunState}|{session.History.Count}|{session.TotalInputTokens}|{session.TotalOutputTokens}|{goal?.UpdatedAt:O}|{goal?.Status}|{goal?.TokensUsed}|{sessionTokenBudget}|{running}|{pendingApproval}|" +
            string.Join(',', actions.Select(a => $"{a.Id}:{a.Revision}"));
        return new RecoveryControls
        {
            Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionText))), CanMutate = canMutate,
            CanPause = canMutate && !running && goal?.Status == GoalStatus.Active,
            CanResume = canMutate && canResume,
            Message = running ? "Stop active execution before changing recovery state." :
                unresolved.Length > 0 ? "Verify provider outcomes and record evidence before resuming." :
                pendingApproval ? "Resolve the pending approval on an approval-capable surface." :
                budgetExceeded ? "A token budget or continuation limit is exhausted; resume cannot increase it." :
                goal?.Status is GoalStatus.UsageLimited or GoalStatus.BudgetLimited ? "Resolve the usage or budget limit before resuming through the normal goal workflow." :
                canResume ? "Resume makes the goal eligible for your next message; it does not dispatch work." : "No eligible goal resume action.",
            Actions = unresolved.Select(a => new RecoveryActionItem(a.Id, a.ToolName, a.State, a.Revision)).ToList()
        };
    }

    public static void ValidateRequest(RecoveryRequest request)
    {
        if (request.Command is not ("pause" or "resume" or "completed" or "not_executed"))
            throw new ArgumentException("Unknown recovery command.");
        if (request.Command is "completed" or "not_executed")
        {
            if (request.ActionId is not { Length: 64 } || !request.ActionId.All(char.IsAsciiHexDigit) || request.ActionRevision < 1)
                throw new ArgumentException("Provide a valid action ID and action revision.");
        }
        else if (request.ActionId is not null || request.ActionRevision != 0 || request.Evidence is not null || request.Result is not null)
            throw new ArgumentException("Goal commands do not accept action fields.");
    }

    /// <summary>The caller must hold the runtime session lock and authorize the operator before calling.</summary>
    public static async Task ApplyAsync(Session session, IGoalService? goals, IMemoryStore memory,
        DurableActionJournal.Lease? journal, RecoveryRequest request, bool running, bool pendingApproval, CancellationToken ct, long sessionTokenBudget = 0)
    {
        ValidateRequest(request);
        if (request.Command is "completed" or "not_executed" && journal is null)
            throw new ArgumentException("Durable action journaling is disabled.");
        var controls = Describe(session, goals?.GetGoal(session.Id), journal?.Records ?? [], true, running, pendingApproval, sessionTokenBudget);
        if (controls.Revision != request.Revision) throw new InvalidOperationException("Recovery state changed. Refresh and review again.");
        if (running) throw new InvalidOperationException("Stop active execution before changing recovery state.");
        if (request.Command is "completed" or "not_executed")
        {
            var action = journal?.Records.SingleOrDefault(a => a.Id == request.ActionId)
                ?? throw new InvalidOperationException("Action not found in this session.");
            if (!controls.Actions.Any(a => a.Id == action.Id) || action.Revision != request.ActionRevision)
                throw new InvalidOperationException("Action is no longer eligible. Refresh and review again.");
            if (string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 4000 || request.Result?.Length > 65536)
                throw new ArgumentException("Provide up to 4000 characters of provider evidence and at most 65536 characters of result.");
            if (action.State == "completed" && request.Command != "completed")
                throw new InvalidOperationException("A recorded completed action cannot be declared not executed.");
            var recorded = session.History.SelectMany(t => t.ToolCalls ?? []).Where(c => c.CallId == action.CallId).ToArray();
            if (recorded.Length > 1 || recorded.Any(c => c.ToolName != action.ToolName || c.Result is null || c.ResultStatus != "completed"))
                throw new InvalidOperationException("Persisted history conflicts with this action; inspect it before resolving.");
            if (recorded.Length > 0 && request.Command == "not_executed")
                throw new InvalidOperationException("Persisted history records completion; it cannot be declared not executed.");
            var result = recorded.Length == 1 ? recorded[0].Result : action.State == "completed" ? action.Result : request.Result;
            if (request.Command == "completed")
            {
                if (result is null) throw new ArgumentException("A completed action requires its verified result.");
                // Persist recovery evidence into history first. A crash before journal resolution still blocks replay.
                if (recorded.Length == 0)
                {
                var turn = new ChatTurn { Role = "assistant", Content = "[reconciled_action]", ToolCalls =
                    [new ToolInvocation { CallId = action.CallId, ToolName = action.ToolName, Arguments = "{}", Result = result, ResultStatus = "completed" }] };
                session.History.Add(turn);
                try { await memory.SaveSessionAsync(session, ct); }
                catch { session.History.Remove(turn); throw; }
                }
            }
            journal!.Resolve(action, request.ActionRevision, request.Command, request.Evidence, result);
            if (request.Command == "completed") journal.AcknowledgeHistory(session);
            return;
        }
        if (request.Command == "pause" && controls.CanPause)
        {
            goals!.UpdateStatus(session.Id, GoalStatus.Paused, "Paused by operator recovery controls.");
            return;
        }
        if (request.Command == "resume" && controls.CanResume)
        {
            // Goal persistence is authoritative. No messages are queued and no session state is guessed.
            goals!.UpdateStatus(session.Id, GoalStatus.Active, "Resumed by operator; waiting for next message.");
            return;
        }
        throw new InvalidOperationException("This recovery action is not currently eligible.");
    }
}
