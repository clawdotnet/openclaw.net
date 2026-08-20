using Strategos.Abstractions;
using Strategos.Agents.Abstractions;
using Strategos.Attributes;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

// Minimal event-sourced state mirroring Strategos' EventSourcedAuditState fixture, to prove
// the WebApplication + UseWolverine + Marten + source-generator host boots before building the
// real ReviewWorkflow. Event type names (SmokeStarted / NoopStepCompleted) are produced by the
// Stratego source generator from the workflow name + step class name; confirm in obj/Generated
// after the first build and correct here if the names differ.
[WorkflowState]
public sealed record SmokeState : IEventSourcedState<SmokeState>
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public bool Done { get; init; }

    // Marten single-stream aggregation seed: built from the workflow-started event.
    public static SmokeState Create(SmokeStarted started)
        => new() { Id = started.WorkflowId, WorkflowId = started.WorkflowId };

    // Marten aggregation fold for the step-completed event (carries the updated state).
    public SmokeState Apply(NoopStepCompleted evt) => evt.UpdatedState;

    // Strategos in-memory fold (saga calls this after appending the event to the Marten stream).
    public SmokeState ApplyEvent(IProgressEvent evt) => evt switch
    {
        NoopStepCompleted c => c.UpdatedState,
        _ => this,
    };
}
