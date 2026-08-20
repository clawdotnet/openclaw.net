using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// OnLowConfidence handler step. Routes low-confidence AssessConfidence results to this
// no-op step so the saga continues; the top-level AwaitApproval<Operator> then pauses for
// the human decision. The step just records the path was taken (CurrentPhase stamping
// happens in ReviewState.Apply via the generator-produced Completed event).
public sealed class RequestHumanReview : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state));
}