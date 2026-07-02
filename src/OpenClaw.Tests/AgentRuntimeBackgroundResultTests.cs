using OpenClaw.Agent;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class AgentRuntimeBackgroundResultTests
{
    [Fact]
    public void AgentTurnResult_CanRepresentContinuationStopReason()
    {
        var result = new AgentTurnResult
        {
            Text = "working",
            ShouldContinue = true,
            StopReason = AgentTurnStopReason.GoalContinuationRequired,
            ContinuePrompt = "continue checking the goal"
        };

        Assert.True(result.ShouldContinue);
        Assert.Equal(AgentTurnStopReason.GoalContinuationRequired, result.StopReason);
        Assert.Equal("continue checking the goal", result.ContinuePrompt);
    }

    [Fact]
    public void AgentTurnResult_Completed_ReturnsNonContinuingResult()
    {
        var result = AgentTurnResult.Completed("done");

        Assert.Equal("done", result.Text);
        Assert.False(result.ShouldContinue);
        Assert.Equal(AgentTurnStopReason.Completed, result.StopReason);
        Assert.Null(result.ContinuePrompt);
    }

    [Fact]
    public void AgentTurnStopReason_AllValues_AreDistinct()
    {
        var values = Enum.GetValues<AgentTurnStopReason>();
        var distinct = new HashSet<AgentTurnStopReason>(values);
        Assert.Equal(values.Length, distinct.Count);
    }
}
