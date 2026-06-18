using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Goal;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Services;
using OpenClaw.Core.Sessions;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Integration tests for the Goal continuation flow in AgentRuntime.
/// Tests the critical path: model stops → Goal active → check prompt injected → model continues.
/// </summary>
public sealed class AgentRuntimeGoalIntegrationTests
{
    private static AgentRuntimeGoalIntegration CreateIntegration()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        return new AgentRuntimeGoalIntegration(goalService);
    }

    private static Session CreateSession(string channelId = "cli", string? senderId = null)
    {
        return new Session
        {
            Id = $"test-{Guid.NewGuid():N}"[..20],
            ChannelId = channelId,
            SenderId = senderId ?? "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
        };
    }

    [Fact]
    public void BuildGoalSystemPrompt_NoGoal_ReturnsNull()
    {
        var integration = CreateIntegration();
        var result = integration.BuildGoalSystemPrompt("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void BuildGoalSystemPrompt_ActiveGoal_ReturnsPrompt()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 0);
        var result = integration.BuildGoalSystemPrompt("s1");

        Assert.NotNull(result);
        Assert.Contains("fix the bug", result);
        Assert.Contains("update_goal", result);
    }

    [Fact]
    public void BuildGoalSystemPrompt_PausedGoal_ReturnsNull()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 0, 0);
        goalService.UpdateStatus("s1", GoalStatus.Paused);
        var result = integration.BuildGoalSystemPrompt("s1");

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateGoalContinuation_ActiveGoal_ChannelInteractive_ReturnsPrompt()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 100);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
        };

        var result = integration.EvaluateGoalContinuation(session, 1, 50, "I'm still working on it");
        Assert.NotNull(result);
        Assert.Contains("fix the bug", result);
    }

    [Fact]
    public void EvaluateGoalContinuation_NoGoal_ReturnsNull()
    {
        var integration = CreateIntegration();
        var session = CreateSession();

        var result = integration.EvaluateGoalContinuation(session, 1, 50, "done");
        Assert.Null(result);
    }

    [Fact]
    public void EvaluateGoalContinuation_NonInteractiveChannel_ReturnsNull()
    {
        // From outside voice resolution: Goal should NOT auto-continue in non-interactive channels
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 100);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "gateway", // Non-interactive
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
        };

        var result = integration.EvaluateGoalContinuation(session, 1, 50, "I fixed it");
        Assert.Null(result); // Should NOT auto-continue in gateway channel
    }

    [Fact]
    public void EvaluateGoalContinuation_BudgetExceeded_ReturnsNull()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        // Budget is 1000, baseline is 0, session total is 5000 → used = 5000 > 1000
        goalService.CreateGoal("s1", "fix the bug", 1000, 0);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 3000,
            TotalOutputTokens = 2000, // Total = 5000
        };

        var result = integration.EvaluateGoalContinuation(session, 1, 50, "still working");
        Assert.Null(result); // Budget exceeded → no continuation

        // Verify the goal transitioned to BudgetLimited
        var goal = goalService.GetGoal("s1");
        Assert.Equal(GoalStatus.BudgetLimited, goal!.Status);
    }

    [Fact]
    public void EvaluateGoalContinuation_ExceedsContinuationLimit_AutoPauses()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 0);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
        };

        // 10 continuations should still work (MaxContinuationsPerTurn = 10 allows 10)
        for (int i = 0; i < 10; i++)
        {
            var result = integration.EvaluateGoalContinuation(session, i, 50, $"still working iteration {i}");
            Assert.NotNull(result);
        }

        // The 11th continuation should cause auto-pause
        var pausedResult = integration.EvaluateGoalContinuation(session, 11, 50, "still working iteration 11");
        Assert.Null(pausedResult);

        // Verify the goal is paused
        var goal = goalService.GetGoal("s1");
        Assert.Equal(GoalStatus.Paused, goal!.Status);
    }

    [Fact]
    public void EvaluateGoalContinuation_MaxIterations_ReturnsNull()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 0);
        var session = CreateSession(senderId: "s1"); // Use "s1" as SenderId mapped to session Id
        // Override the random session ID to match the goal
        var sessionWithId = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
        };

        // At max iterations, should still continue
        var result = integration.EvaluateGoalContinuation(sessionWithId, 49, 50, "done?");
        Assert.NotNull(result); // Should still continue at iteration 49 (max is 50)

        // At max iterations reached, should no longer continue
        var result2 = integration.EvaluateGoalContinuation(sessionWithId, 50, 50, "done?");
        Assert.Null(result2);
    }

    [Fact]
    public void UpdateGoalTokenUsage_UpdatesGoalTokens()
    {
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 100);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 1000,
            TotalOutputTokens = 500, // Total = 1500, baseline = 100 → used = 1400
        };

        integration.UpdateGoalTokenUsage(session);
        var goal = goalService.GetGoal("s1");
        Assert.Equal(1400, goal!.TokensUsed);
    }

    [Fact]
    public void BlockerDetection_ThreeSameBlockers_BlocksGoal()
    {
        // Integration test: the full flow from EvaluateGoalContinuation through blocker detection
        var logger = Substitute.For<ILogger<InMemoryGoalService>>();
        var goalService = new InMemoryGoalService(logger);
        var integration = new AgentRuntimeGoalIntegration(goalService);

        goalService.CreateGoal("s1", "fix the bug", 50000, 0);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
            History = new List<ChatTurn>(),
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
        };

        // First two same-blocker turns → continues
        Assert.NotNull(integration.EvaluateGoalContinuation(session, 1, 50, "I am stuck on step 1"));
        Assert.NotNull(integration.EvaluateGoalContinuation(session, 2, 50, "I am stuck on step 1"));

        // Third same-blocker turn → should block
        var blocked = integration.EvaluateGoalContinuation(session, 3, 50, "I am stuck on step 1");
        Assert.Null(blocked);

        var goal = goalService.GetGoal("s1");
        Assert.Equal(GoalStatus.Blocked, goal!.Status);
    }
}
