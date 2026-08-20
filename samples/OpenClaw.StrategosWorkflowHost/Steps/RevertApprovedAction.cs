using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Compensation step wired via .Compensate<RevertApprovedAction>() on ExecuteApprovedAction.
public sealed class RevertApprovedAction : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            FailureReason = (state.FailureReason ?? "") + "; reverted approved action."
        }));
}