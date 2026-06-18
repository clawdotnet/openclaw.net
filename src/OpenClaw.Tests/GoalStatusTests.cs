using OpenClaw.Core.Models.Goal;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GoalStatusTests
{
    [Fact]
    public void IsPursuable_Active_ReturnsTrue()
    {
        Assert.True(GoalStatus.Active.IsPursuable());
    }

    [Fact]
    public void IsPursuable_NonActive_ReturnsFalse()
    {
        Assert.False(GoalStatus.Paused.IsPursuable());
        Assert.False(GoalStatus.Blocked.IsPursuable());
        Assert.False(GoalStatus.BudgetLimited.IsPursuable());
        Assert.False(GoalStatus.UsageLimited.IsPursuable());
        Assert.False(GoalStatus.Complete.IsPursuable());
    }

    [Fact]
    public void IsTerminal_Complete_ReturnsTrue()
    {
        Assert.True(GoalStatus.Complete.IsTerminal());
    }

    [Fact]
    public void IsTerminal_NonComplete_ReturnsFalse()
    {
        Assert.False(GoalStatus.Active.IsTerminal());
        Assert.False(GoalStatus.Paused.IsTerminal());
        Assert.False(GoalStatus.Blocked.IsTerminal());
        Assert.False(GoalStatus.BudgetLimited.IsTerminal());
        Assert.False(GoalStatus.UsageLimited.IsTerminal());
    }

    [Theory]
    [InlineData(GoalStatus.Active, "Active")]
    [InlineData(GoalStatus.Paused, "Paused")]
    [InlineData(GoalStatus.Blocked, "Blocked")]
    [InlineData(GoalStatus.BudgetLimited, "Budget Limited")]
    [InlineData(GoalStatus.UsageLimited, "Usage Limited")]
    [InlineData(GoalStatus.Complete, "Complete")]
    public void ToDisplayName_ReturnsExpected(GoalStatus status, string expected)
    {
        Assert.Equal(expected, status.ToDisplayName());
    }

    [Fact]
    public void FormatGoalProgressBar_FillsRemainingSlots()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 100,
            TokensAtStart = 0,
            TokensUsed = 50,
        };

        Assert.Equal("[==========>         ] 50% (50/100)", GoalStatusExtensions.FormatGoalProgressBar(goal));
    }
}
