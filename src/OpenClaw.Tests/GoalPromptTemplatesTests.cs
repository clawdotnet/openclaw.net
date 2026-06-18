using OpenClaw.Agent.Goal;
using OpenClaw.Core.Models.Goal;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GoalPromptTemplatesTests
{
    [Fact]
    public void FormatGoalFooterLine_ActiveWithBudget_ShowsUsage()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "fix auth bug",
            TokenBudget = 50000,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 12000;

        var result = GoalPromptTemplates.FormatGoalFooterLine(goal);
        Assert.Contains("12000", result);
        Assert.Contains("50000", result);
    }

    [Fact]
    public void FormatGoalFooterLine_ActiveWithoutBudget_TruncatesObjective()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "fix the authentication bug in the login module",
            TokensAtStart = 0,
        };

        var result = GoalPromptTemplates.FormatGoalFooterLine(goal);
        Assert.Contains("fix the authentication bug in the login", result);
    }

    [Fact]
    public void FormatGoalFooterLine_Paused_ShowsResumeHint()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            Status = GoalStatus.Paused,
            TokensAtStart = 0,
        };

        var result = GoalPromptTemplates.FormatGoalFooterLine(goal);
        Assert.Contains("/goal resume", result);
    }

    [Fact]
    public void FormatGoalFooterLine_Complete_ShowsTokenCount()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            Status = GoalStatus.Complete,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 42000;

        var result = GoalPromptTemplates.FormatGoalFooterLine(goal);
        Assert.Contains("42000", result);
    }

    [Fact]
    public void FormatGoalFooterLine_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GoalPromptTemplates.FormatGoalFooterLine(null));
    }

    [Fact]
    public void FormatProgressBar_ActiveWithBudget_ReturnsBar()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 128000,
            Status = GoalStatus.Active,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 64000;

        var result = GoalPromptTemplates.FormatProgressBar(goal);
        Assert.NotNull(result);
        Assert.Contains("50%", result);
        Assert.Contains("64000", result);
        Assert.Contains("128000", result);
    }

    [Fact]
    public void FormatProgressBar_NoBudget_ReturnsNull()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "test",
            TokenBudget = 0,
            Status = GoalStatus.Active,
            TokensAtStart = 0,
        };

        Assert.Null(GoalPromptTemplates.FormatProgressBar(goal));
    }

    [Fact]
    public void FormatProgressBar_NullGoal_ReturnsNull()
    {
        Assert.Null(GoalPromptTemplates.FormatProgressBar(null));
    }

    [Fact]
    public void BuildActivationPrompt_ContainsObjective()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "fix the CI pipeline",
            TokensAtStart = 0,
        };

        var prompt = GoalPromptTemplates.BuildActivationPrompt(goal);
        Assert.Contains("fix the CI pipeline", prompt);
        Assert.Contains("update_goal", prompt);
        Assert.Contains("Completion Audit", prompt);
    }

    [Fact]
    public void BuildCheckPrompt_ContainsBudgetInfo()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "fix tests",
            TokenBudget = 50000,
            TokensAtStart = 0,
        };
        goal.TokensUsed = 10000;

        var prompt = GoalPromptTemplates.BuildCheckPrompt(goal, 3, 50);
        Assert.Contains("fix tests", prompt);
        Assert.Contains("Budget", prompt);
        Assert.Contains("10000", prompt);
        Assert.Contains("50000", prompt);
        Assert.Contains("3/50", prompt);
    }

    [Fact]
    public void BuildCheckPrompt_NoBudget_OmitsBudgetLine()
    {
        var goal = new SessionGoal
        {
            SessionId = "s1",
            Objective = "fix tests",
            TokenBudget = 0,
            TokensAtStart = 0,
        };

        var prompt = GoalPromptTemplates.BuildCheckPrompt(goal, 1, 50);
        Assert.DoesNotContain("Budget:", prompt);
    }
}
