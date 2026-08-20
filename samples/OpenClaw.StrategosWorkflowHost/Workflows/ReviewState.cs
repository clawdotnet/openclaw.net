using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Strategos.Abstractions;
using Strategos.Agents.Abstractions;
using Strategos.Attributes;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

// Event-sourced Marten state for the durable agent review saga.
// The Strategos source generator emits DurableAgentReviewStarted(Guid WorkflowId,
// ReviewState InitialState, DateTimeOffset Timestamp) and one {StepClassName}Completed
// event per step, plus IDurableAgentReviewEvent marker interface. We adopt the started
// event's InitialState as the Marten aggregation seed and adopt each {Step}Completed's
// UpdatedState on fold (with CurrentPhase stamping).
[WorkflowState]
public sealed record ReviewState : IEventSourcedState<ReviewState>
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public string UserRequest { get; init; } = "";
    public string Plan { get; init; } = "";
    public IReadOnlyList<ReviewVerdict> Reviews { get; init; } = [];
    public string? AggregatedSummary { get; init; }
    public double AggregateConfidence { get; init; }
    public HumanDecision? Decision { get; init; }
    public string? ExecutionResult { get; init; }
    public string? FailureReason { get; init; }
    public string CurrentPhase { get; init; } = "NotStarted";

    // Marten aggregation seed: the generator-produced Started event carries InitialState
    // (ReviewState), so we adopt it as-is. The adapter supplies the seeded state when
    // publishing StartDurableAgentReviewCommand.
    public static ReviewState Create(DurableAgentReviewStarted started) => started.InitialState;

    // Marten aggregation folds. Each {Step}Completed event carries an UpdatedState snapshot;
    // we adopt that snapshot verbatim and stamp CurrentPhase from the step that just finished.
    // Approve/Reject happens via the Resume{ApprovalPoint}ApprovalCommand and is saga-internal,
    // so it does NOT set CurrentPhase here (the adapter observes the pause from the event stream).
    public ReviewState Apply(PlanExecutorCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingPlan" };

    public ReviewState Apply(RequestHumanReviewCompleted e) =>
        e.UpdatedState with { CurrentPhase = "AwaitingReview" };

    public ReviewState Apply(SecurityReviewerCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingReview" };

    public ReviewState Apply(ArchitectureReviewerCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingReview" };

    public ReviewState Apply(CostReviewerCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingReview" };

    public ReviewState Apply(AggregateReviewsCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingAggregator" };

    public ReviewState Apply(AssessConfidenceCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingConfidence" };

    public ReviewState Apply(ExecuteApprovedActionCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingAction" };

    public ReviewState Apply(EmitAuditTraceCompleted e) =>
        e.UpdatedState with { CurrentPhase = "Completed" };

    // Strategos in-memory fold (called by the saga after each append).
    public ReviewState ApplyEvent(IProgressEvent evt) => evt switch
    {
        PlanExecutorCompleted c => c.UpdatedState,
        RequestHumanReviewCompleted c => c.UpdatedState,
        SecurityReviewerCompleted c => c.UpdatedState,
        ArchitectureReviewerCompleted c => c.UpdatedState,
        CostReviewerCompleted c => c.UpdatedState,
        AggregateReviewsCompleted c => c.UpdatedState,
        AssessConfidenceCompleted c => c.UpdatedState,
        ExecuteApprovedActionCompleted c => c.UpdatedState,
        EmitAuditTraceCompleted c => c.UpdatedState,
        DurableAgentReviewStarted => this,
        _ => this,
    };
}