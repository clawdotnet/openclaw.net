using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

public sealed class ExecuteApprovedAction : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            ExecutionResult = $"Executed approved action for: {state.UserRequest}"
        }));
}