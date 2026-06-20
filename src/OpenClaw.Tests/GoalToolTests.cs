using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GoalToolTests
{
    private static ToolExecutionContext CreateContext(Session? session = null)
    {
        session ??= new Session
        {
            Id = "s1",
            ChannelId = "cli",
            SenderId = "test-sender",
        };

        return new ToolExecutionContext
        {
            Session = session,
            TurnContext = new TurnContext
            {
                SessionId = session.Id,
                ChannelId = session.ChannelId,
            }
        };
    }

    private static InMemoryGoalService CreateGoalService()
        => new(Substitute.For<ILogger<InMemoryGoalService>>());

    [Fact]
    public async Task CreateGoalTool_MalformedJson_ReturnsToolError()
    {
        var tool = new CreateGoalTool(CreateGoalService());

        var result = await tool.ExecuteAsync("{", CreateContext(), TestContext.Current.CancellationToken);

        Assert.Equal("Error: arguments must be valid JSON.", result);
    }

    [Fact]
    public async Task CreateGoalTool_NegativeBudget_ReturnsToolError()
    {
        var tool = new CreateGoalTool(CreateGoalService());

        var result = await tool.ExecuteAsync(
            """{"objective":"fix bug","token_budget":-1}""",
            CreateContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: token_budget cannot be negative.", result);
    }

    [Fact]
    public async Task UpdateGoalTool_MissingStatus_ReturnsToolError()
    {
        var goalService = CreateGoalService();
        goalService.CreateGoal("s1", "fix bug", 0, 0);
        var tool = new UpdateGoalTool(goalService);

        var result = await tool.ExecuteAsync("{}", CreateContext(), TestContext.Current.CancellationToken);

        Assert.Equal("Error: status is required.", result);
    }

    [Fact]
    public async Task UpdateGoalTool_CompleteWithoutAssistantEvidence_ReturnsWarning()
    {
        var goalService = CreateGoalService();
        goalService.CreateGoal("s1", "fix bug", 0, 0);
        var tool = new UpdateGoalTool(goalService);

        var result = await tool.ExecuteAsync(
            """{"status":"complete"}""",
            CreateContext(),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Warning: Cannot verify completion.", result);
        Assert.Equal(GoalStatus.Active, goalService.GetGoal("s1")!.Status);
    }
}
