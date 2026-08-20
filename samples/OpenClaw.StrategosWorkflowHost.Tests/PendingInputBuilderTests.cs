using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class PendingInputBuilderTests
{
    [Fact]
    public void Build_ReturnsSingleInputWithPortId()
    {
        var state = new ReviewState
        {
            WorkflowId = Guid.NewGuid(),
            AggregatedSummary = "needs human review",
            AggregateConfidence = 0.42,
        };

        var inputs = PendingInputBuilder.Build(state, "operator-approval");

        var single = Assert.Single(inputs);
        Assert.Equal("operator-approval", single.PortId);
        Assert.NotNull(single.Payload);
    }

    [Fact]
    public void Build_PayloadIncludesWorkflowIdAndConfidence()
    {
        var id = Guid.NewGuid();
        var state = new ReviewState
        {
            WorkflowId = id,
            AggregatedSummary = "needs review",
            AggregateConfidence = 0.55,
        };

        var inputs = PendingInputBuilder.Build(state, "operator-approval");
        var root = inputs[0].Payload!.Value;

        Assert.Equal(id, root.GetProperty("workflowId").GetGuid());
        Assert.Equal(0.55, root.GetProperty("confidence").GetDouble());
        Assert.Equal("needs review", root.GetProperty("summary").GetString());
    }

    [Fact]
    public void Build_PayloadReviewsArrayCarriesEachVerdict()
    {
        var state = new ReviewState
        {
            WorkflowId = Guid.NewGuid(),
            Reviews = new[]
            {
                new ReviewVerdict("security", "review-required", "ok", 0.8),
                new ReviewVerdict("architecture", "review-required", "ok", 0.7),
                new ReviewVerdict("cost", "review-required", "ok", 0.6),
            }
        };

        var inputs = PendingInputBuilder.Build(state, "operator-approval");
        var reviews = inputs[0].Payload!.Value.GetProperty("reviews");

        Assert.Equal(3, reviews.GetArrayLength());
        Assert.Equal("security", reviews[0].GetProperty("role").GetString());
        Assert.Equal(0.6, reviews[2].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public void Build_FallsBackToDefaultSummaryWhenAggregatedSummaryIsNull()
    {
        var state = new ReviewState
        {
            WorkflowId = Guid.NewGuid(),
            AggregatedSummary = null,
        };

        var inputs = PendingInputBuilder.Build(state, "operator-approval");

        var summary = inputs[0].Payload!.Value.GetProperty("summary").GetString();
        Assert.NotNull(summary);
        Assert.Contains("Approval required", summary);
    }

    [Fact]
    public void Build_MetadataMarksPortAsHumanApproval()
    {
        var state = new ReviewState { WorkflowId = Guid.NewGuid() };

        var inputs = PendingInputBuilder.Build(state, "operator-approval");

        Assert.NotNull(inputs[0].Metadata);
        Assert.Equal("HumanApproval", inputs[0].Metadata!["requestPort"]);
    }
}