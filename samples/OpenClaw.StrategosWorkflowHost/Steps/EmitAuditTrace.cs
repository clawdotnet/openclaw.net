using System.Text.Json.Nodes;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Finally step: append an audit trace summary to ExecutionResult. OutputPayload is assembled
// by the adapter from the event stream; this step only seeds the readable summary.
public sealed class EmitAuditTrace : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var audit = new JsonObject
        {
            ["plan"] = state.Plan,
            ["reviews"] = state.Reviews.Count,
            ["approved"] = state.Decision?.Approved
        };
        return Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            ExecutionResult = (state.ExecutionResult ?? "") + $"\nAuditTrace:{audit.ToJsonString()}"
        }));
    }
}