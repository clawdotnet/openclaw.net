using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Reports the aggregated confidence before the explicit operator approval point.
// The gate lives in the step-config on ReviewWorkflow; this step only echoes the value.
public sealed class AssessConfidence : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.WithConfidence(state, state.AggregateConfidence));
}