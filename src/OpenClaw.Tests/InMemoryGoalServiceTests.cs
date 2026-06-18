using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class InMemoryGoalServiceTests
{
    private static IGoalService CreateService()
    {
        return new InMemoryGoalService(Substitute.For<ILogger<InMemoryGoalService>>());
    }

    [Fact]
    public void CreateGoal_ValidInput_CreatesActiveGoal()
    {
        var svc = CreateService();
        var goal = svc.CreateGoal("s1", "fix the bug", 10000, 500);

        Assert.Equal("s1", goal.SessionId);
        Assert.Equal("fix the bug", goal.Objective);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(10000, goal.TokenBudget);
        Assert.Equal(500, goal.TokensAtStart);
    }

    [Fact]
    public void CreateGoal_DuplicateSession_Throws()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "fix the bug", 0, 0);

        Assert.Throws<InvalidOperationException>(() =>
            svc.CreateGoal("s1", "another goal", 0, 0));
    }

    [Fact]
    public void CreateGoal_NegativeBudget_Throws()
    {
        var svc = CreateService();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            svc.CreateGoal("s1", "fix the bug", -1, 0));
    }

    [Fact]
    public void CreateGoal_NegativeTokenBaseline_Throws()
    {
        var svc = CreateService();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            svc.CreateGoal("s1", "fix the bug", 0, -1));
    }

    [Fact]
    public void CreateGoal_ObjectiveTooLong_Throws()
    {
        var svc = CreateService();
        var longObjective = new string('x', SessionGoal.MaxObjectiveLength + 1);

        Assert.Throws<ArgumentException>(() =>
            svc.CreateGoal("s1", longObjective, 0, 0));
    }

    [Fact]
    public void GetGoal_Existing_ReturnsGoal()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        var goal = svc.GetGoal("s1");
        Assert.NotNull(goal);
        Assert.Equal("test", goal.Objective);
    }

    [Fact]
    public void GetGoal_NonExisting_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.GetGoal("nonexistent"));
    }

    [Fact]
    public void UpdateStatus_ValidTransition_Succeeds()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        svc.UpdateStatus("s1", GoalStatus.Paused);
        Assert.Equal(GoalStatus.Paused, svc.GetGoal("s1")!.Status);

        svc.UpdateStatus("s1", GoalStatus.Active);
        Assert.Equal(GoalStatus.Active, svc.GetGoal("s1")!.Status);

        svc.UpdateStatus("s1", GoalStatus.Complete);
        Assert.Equal(GoalStatus.Complete, svc.GetGoal("s1")!.Status);
    }

    [Fact]
    public void UpdateStatus_InvalidTransition_ActiveToBlocked_ThenActiveAgain()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        svc.UpdateStatus("s1", GoalStatus.Blocked);
        Assert.Equal(GoalStatus.Blocked, svc.GetGoal("s1")!.Status);

        svc.UpdateStatus("s1", GoalStatus.Active);
        Assert.Equal(GoalStatus.Active, svc.GetGoal("s1")!.Status);
    }

    [Fact]
    public void UpdateStatus_CompleteToActive_Throws()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        svc.UpdateStatus("s1", GoalStatus.Complete);

        Assert.Throws<InvalidOperationException>(() =>
            svc.UpdateStatus("s1", GoalStatus.Active));
    }

    [Fact]
    public void UpdateStatus_FromTerminal_Throws()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        svc.UpdateStatus("s1", GoalStatus.Complete);

        Assert.Throws<InvalidOperationException>(() =>
            svc.UpdateStatus("s1", GoalStatus.Paused));
    }

    [Fact]
    public void UpdateTokenUsage_ComputesFromBaseline()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 10000, 1000);

        svc.UpdateTokenUsage("s1", 3000); // session total = 3000, baseline = 1000 → used = 2000
        Assert.Equal(2000, svc.GetGoal("s1")!.TokensUsed);
    }

    [Fact]
    public void UpdateTokenUsage_NoGoal_NoOp()
    {
        var svc = CreateService();
        svc.UpdateTokenUsage("nonexistent", 5000); // Should not throw
    }

    [Fact]
    public void RecordTurnHash_ThreeSameHashes_ReturnsTrue()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        Assert.False(svc.RecordTurnHash("s1", "I am stuck on step 1"));
        Assert.False(svc.RecordTurnHash("s1", "I am stuck on step 1"));
        Assert.True(svc.RecordTurnHash("s1", "I am stuck on step 1"));
    }

    [Fact]
    public void RecordTurnHash_DifferentHashes_ResetsCounter()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        Assert.False(svc.RecordTurnHash("s1", "I am stuck on step 1"));
        Assert.False(svc.RecordTurnHash("s1", "I am stuck on step 1"));
        Assert.False(svc.RecordTurnHash("s1", "Now I am stuck on step 2")); // different hash, resets
        // Would need 3 more of the NEW hash to trigger blocked
        Assert.False(svc.RecordTurnHash("s1", "Now I am stuck on step 2"));
        Assert.True(svc.RecordTurnHash("s1", "Now I am stuck on step 2"));
    }

    [Fact]
    public void RecordTurnHash_EmptyText_DoesNotCountAsBlocker()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);

        Assert.False(svc.RecordTurnHash("s1", ""));
        Assert.False(svc.RecordTurnHash("s1", " "));
        Assert.False(svc.RecordTurnHash("s1", ""));

        var goal = svc.GetGoal("s1")!;
        Assert.Equal(0, goal.ConsecutiveBlockerCount);
        Assert.Null(goal.LastBlockerHash);
    }

    [Fact]
    public void IncrementContinuationCount_IncrementsPerSession()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        svc.CreateGoal("s2", "test 2", 0, 0);

        Assert.Equal(1, svc.IncrementContinuationCount("s1"));
        Assert.Equal(1, svc.IncrementContinuationCount("s2"));
        Assert.Equal(2, svc.IncrementContinuationCount("s1"));
        Assert.Equal(2, svc.GetGoal("s1")!.ContinuationCount);
        Assert.Equal(1, svc.GetGoal("s2")!.ContinuationCount);
    }

    [Fact]
    public void ClearGoal_RemovesGoal()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        svc.ClearGoal("s1");

        Assert.Null(svc.GetGoal("s1"));
    }

    [Fact]
    public void HasActiveGoal_ActiveGoal_ReturnsTrue()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        Assert.True(svc.HasActiveGoal("s1"));
    }

    [Fact]
    public void HasActiveGoal_PausedGoal_ReturnsFalse()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 0, 0);
        svc.UpdateStatus("s1", GoalStatus.Paused);
        Assert.False(svc.HasActiveGoal("s1"));
    }

    [Fact]
    public void HasActiveGoal_NoGoal_ReturnsFalse()
    {
        var svc = CreateService();
        Assert.False(svc.HasActiveGoal("nonexistent"));
    }

    [Fact]
    public void BudgetExceeded_LeadsToBudgetLimited()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "test", 1000, 0);

        // Simulate exceeding budget
        svc.UpdateTokenUsage("s1", 1000); // used = 1000 - 0 = 1000 >= budget 1000
        var goal = svc.GetGoal("s1")!;
        Assert.True(goal.IsBudgetExceeded);

        // UpdateStatus should transition to BudgetLimited
        svc.UpdateStatus("s1", GoalStatus.BudgetLimited);
        Assert.Equal(GoalStatus.BudgetLimited, svc.GetGoal("s1")!.Status);
    }

    [Fact]
    public void MultipleSessions_IndependentGoals()
    {
        var svc = CreateService();
        svc.CreateGoal("s1", "goal 1", 1000, 0);
        svc.CreateGoal("s2", "goal 2", 2000, 100);

        Assert.Equal("goal 1", svc.GetGoal("s1")!.Objective);
        Assert.Equal("goal 2", svc.GetGoal("s2")!.Objective);
        Assert.Equal(1000, svc.GetGoal("s1")!.TokenBudget);
        Assert.Equal(2000, svc.GetGoal("s2")!.TokenBudget);
    }
}
