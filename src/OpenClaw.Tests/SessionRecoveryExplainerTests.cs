using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SessionRecoveryExplainerTests
{
    private static Session Session(SessionRunState state = SessionRunState.Idle)
        => new() { Id = "s1", ChannelId = "test", SenderId = "user", RunState = state };
    private static SessionGoal Goal(GoalStatus state)
        => new() { SessionId = "s1", Objective = "Fix the issue", Status = state, StatusNote = "Access is missing" };

    [Fact]
    public void Approvals_AreScopedToExactSession_AndDoNotExposeArguments()
    {
        var own = new ToolApprovalRequest { ApprovalId = "a1", SessionId = "s1", ChannelId = "test", SenderId = "user", ToolName = "shell", Arguments = "secret", Summary = "private" };
        var other = own with { ApprovalId = "a2", SessionId = "s2" };
        var result = SessionRecoveryExplainer.Explain(Session(SessionRunState.Running), null, [other, own]);
        Assert.Equal("approval_pending", result.Status);
        var json = JsonSerializer.Serialize(result, CoreJsonContext.Default.SessionRecoveryExplanation);
        Assert.Contains("a1", json);
        Assert.DoesNotContain("a2", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private", json);
        Assert.Equal("idle", SessionRecoveryExplainer.Explain(Session(), null, [other]).Status);
    }

    [Theory]
    [InlineData(GoalStatus.Paused, "paused")]
    [InlineData(GoalStatus.Blocked, "blocked")]
    [InlineData(GoalStatus.BudgetLimited, "budget_limited")]
    [InlineData(GoalStatus.UsageLimited, "usage_limited")]
    [InlineData(GoalStatus.Complete, "completed")]
    public void GoalStateExplainsIdleSession(GoalStatus state, string expected)
    {
        var goal = Goal(state);
        var result = SessionRecoveryExplainer.Explain(Session(), goal, []);
        Assert.Equal(expected, result.Status);
        Assert.Contains(result.Evidence, item => item.Contains("Access is missing"));
        Assert.Equal(state, goal.Status); // Explanation must never mutate state.
    }

    [Fact]
    public void ExhaustedBudget_DoesNotSuggestResumeAsSolution()
    {
        var goal = new SessionGoal { SessionId = "s1", Objective = "Work", Status = GoalStatus.Paused, TokenBudget = 100, TokensUsed = 100 };
        var result = SessionRecoveryExplainer.Explain(Session(), goal, []);
        Assert.Equal("budget_limited", result.Status);
        Assert.DoesNotContain(result.NextSteps, step => step.Contains("/goal resume"));
        goal.Status = GoalStatus.Complete;
        Assert.Equal("completed", SessionRecoveryExplainer.Explain(Session(), goal, []).Status);
    }

    [Fact]
    public void ContinuationCap_DoesNotMisreportAsGoalBudgetExhaustion()
    {
        var session = Session(SessionRunState.BudgetLimited);
        session.BackgroundRun = new() { LastStopReason = "MaxContinuationTurnsReached", ContinuationCount = 8 };
        var result = SessionRecoveryExplainer.Explain(session, Goal(GoalStatus.Active), []);
        Assert.Equal("budget_limited", result.Status);
        Assert.Contains(result.NextSteps, step => step.Contains("continuation limit"));
    }

    [Fact]
    public void FailedCheckpoint_DoesNotAuthorizeReplay()
    {
        var session = Session(SessionRunState.Failed);
        session.ExecutionCheckpoint = new() { CheckpointId = "checkpoint1", State = SessionCheckpointStates.ReadyToResume };
        var result = SessionRecoveryExplainer.Explain(session, null, []);
        Assert.Equal("failed", result.Status);
        Assert.Contains(result.Evidence, step => step.Contains("does not prove"));
        Assert.Contains(result.NextSteps, step => step.Contains("Do not blindly repeat"));
    }

    [Fact]
    public void FailedRun_ShowsLatestToolFailureWithoutArgumentsOrResults()
    {
        var session = Session(SessionRunState.Failed);
        session.History.Add(new ChatTurn { Role = "assistant", Content = "", ToolCalls =
            [new() { ToolName = "upload", Arguments = "private-argument", Result = "private-result", FailureCode = "access_denied", FailureMessage = "Permission missing" }] });
        var result = SessionRecoveryExplainer.Explain(session, null, []);
        Assert.Contains(result.Evidence, item => item.Contains("access_denied") && item.Contains("Permission missing"));
        Assert.DoesNotContain(result.Evidence, item => item.Contains("private-"));
    }

    [Theory]
    [InlineData(SessionRunState.Running)]
    [InlineData(SessionRunState.Continuing)]
    public void PersistedRunningState_IsNotClaimedAsLiveWorker(SessionRunState state)
    {
        Assert.Contains("not proof of a live worker", SessionRecoveryExplainer.Explain(Session(state), null, []).Summary);
    }

    [Fact]
    public void NewActiveGoal_IsNotHiddenByPreviousCompletedRun()
    {
        var result = SessionRecoveryExplainer.Explain(Session(SessionRunState.Completed), Goal(GoalStatus.Active), []);
        Assert.Equal("goal_active", result.Status);
        Assert.DoesNotContain(result.NextSteps, step => step.Contains("new goal"));
    }

    [Fact]
    public void ExpiredSession_DoesNotSuggestGoalResume()
    {
        var session = Session();
        session.State = SessionState.Expired;
        var result = SessionRecoveryExplainer.Explain(session, Goal(GoalStatus.Paused), []);
        Assert.Equal("expired", result.Status);
        Assert.DoesNotContain(result.NextSteps, step => step.Contains("/goal resume"));
    }
}
