using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Deterministic no-op step: returns state with Done = true. Used by the smoke workflow to
// exercise the source-generator -> Wolverine saga -> Marten stream path without an LLM.
public sealed class NoopStep : IWorkflowStep<SmokeState>
{
    public Task<StepResult<SmokeState>> ExecuteAsync(
        SmokeState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<SmokeState>.FromState(state with { Done = true }));
}
