using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Builds a placeholder plan from the user request. No LLM call; deterministic.
public sealed class PlanExecutor : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            Plan = $"Plan for: {state.UserRequest}"
        }));
}