using OpenClaw.Core.Models.Goal;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SessionGoalTests
{
    [Fact]
    public void NormalizeForComparison_TrimsAndCollapsesWhitespace()
    {
        var result = SessionGoal.NormalizeForComparison("  hello   world  ");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void NormalizeForComparison_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SessionGoal.NormalizeForComparison(string.Empty));
        Assert.Equal(string.Empty, SessionGoal.NormalizeForComparison(null!));
        Assert.Equal(string.Empty, SessionGoal.NormalizeForComparison("   "));
    }

    [Fact]
    public void ComputeTurnHash_Deterministic()
    {
        var hash1 = SessionGoal.ComputeTurnHash("hello world");
        var hash2 = SessionGoal.ComputeTurnHash("hello world");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeTurnHash_DifferentInput_DifferentHash()
    {
        var hash1 = SessionGoal.ComputeTurnHash("I fixed the bug");
        var hash2 = SessionGoal.ComputeTurnHash("I am still working on the bug");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeTurnHash_NormalizedMatch_ProducesSameHash()
    {
        var hash1 = SessionGoal.ComputeTurnHash(SessionGoal.NormalizeForComparison("I fixed  the bug"));
        var hash2 = SessionGoal.ComputeTurnHash(SessionGoal.NormalizeForComparison("I fixed the bug"));
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void IsBudgetExceeded_NoBudget_ReturnsFalse()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 0,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 1_000_000;
        Assert.False(goal.IsBudgetExceeded);
    }

    [Fact]
    public void IsBudgetExceeded_AtLimit_ReturnsTrue()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 1000,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 1000;
        Assert.True(goal.IsBudgetExceeded);
    }

    [Fact]
    public void IsBudgetExceeded_BelowLimit_ReturnsFalse()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 1000,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 500;
        Assert.False(goal.IsBudgetExceeded);
    }

}
