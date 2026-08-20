using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// OnFailure branch: stamps the failure reason with the current phase so callers can debug.
public sealed class NotifyFailure : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            FailureReason = $"Workflow failed at phase {state.CurrentPhase}."
        }));
}