using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

// These tests use the generator-produced event types (DurableAgentReviewStarted and
// {Step}Completed) directly. Naming convention is verified against the generator's
// EventsEmitter: DurableAgentReviewStarted(Guid WorkflowId, ReviewState InitialState,
// DateTimeOffset Timestamp), {Step}Completed([SagaIdentity] Guid WorkflowId, Guid
// StepExecutionId, ReviewState UpdatedState, double? Confidence, DateTimeOffset Timestamp).
public class ReviewStateFoldTests
{
    [Fact]
    public void Create_FromStartedEvent_AdoptsInitialState()
    {
        var id = Guid.NewGuid();
        var initial = new ReviewState
        {
            Id = id,
            WorkflowId = id,
            UserRequest = "deploy v2 to production",
        };

        var started = new DurableAgentReviewStarted(id, initial, DateTimeOffset.UtcNow);

        var folded = ReviewState.Create(started);

        Assert.Equal(id, folded.WorkflowId);
        Assert.Equal("deploy v2 to production", folded.UserRequest);
    }

    [Fact]
    public void Apply_SecurityReviewerCompleted_StampsExecutingReviewPhase()
    {
        var id = Guid.NewGuid();
        var updated = new ReviewState { Id = id, WorkflowId = id };
        var evt = new SecurityReviewerCompleted(id, Guid.NewGuid(), updated, 0.8, DateTimeOffset.UtcNow);

        var folded = updated.Apply(evt);

        Assert.Equal("ExecutingReview", folded.CurrentPhase);
    }

    [Fact]
    public void Apply_AssessConfidenceCompleted_StampsExecutingConfidencePhase()
    {
        var id = Guid.NewGuid();
        var updated = new ReviewState { Id = id, WorkflowId = id, AggregateConfidence = 0.42 };
        var evt = new AssessConfidenceCompleted(id, Guid.NewGuid(), updated, 0.42, DateTimeOffset.UtcNow);

        var folded = updated.Apply(evt);

        Assert.Equal("ExecutingConfidence", folded.CurrentPhase);
        Assert.Equal(0.42, folded.AggregateConfidence);
    }

    [Fact]
    public void Apply_EmitAuditTraceCompleted_StampsCompletedPhase()
    {
        var id = Guid.NewGuid();
        var updated = new ReviewState
        {
            Id = id,
            WorkflowId = id,
            ExecutionResult = "audit: ok",
        };
        var evt = new EmitAuditTraceCompleted(id, Guid.NewGuid(), updated, null, DateTimeOffset.UtcNow);

        var folded = updated.Apply(evt);

        Assert.Equal("Completed", folded.CurrentPhase);
        Assert.Equal("audit: ok", folded.ExecutionResult);
    }

    [Fact]
    public void ApplyEvent_UnknownEvent_ReturnsStateUnchanged()
    {
        var id = Guid.NewGuid();
        var state = new ReviewState { Id = id, WorkflowId = id };
        var unknown = new UnknownProgressEvent(id, DateTimeOffset.UtcNow);

        var folded = state.ApplyEvent(unknown);

        Assert.Same(state, folded);
    }

    // Sentinel event type for the pass-through case (not generated). Implements
    // IProgressEvent so it can be fed to ApplyEvent; the fold must return state
    // unchanged for unknown event types per IEventSourcedState contract.
    private sealed record UnknownProgressEvent(Guid WorkflowId, DateTimeOffset Timestamp)
        : Strategos.Agents.Abstractions.IProgressEvent;
}