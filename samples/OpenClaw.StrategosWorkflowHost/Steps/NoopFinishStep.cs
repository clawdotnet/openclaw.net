using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Distinct terminal step so the smoke workflow has two unique step types (Strategos AGWF003
// forbids reusing the same step type twice in one workflow).
public sealed class NoopFinishStep : IWorkflowStep<SmokeState>
{
    public Task<StepResult<SmokeState>> ExecuteAsync(
        SmokeState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<SmokeState>.FromState(state with { Done = true }));
}
