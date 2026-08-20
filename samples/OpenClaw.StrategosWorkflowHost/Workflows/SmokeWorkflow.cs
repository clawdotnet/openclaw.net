using Strategos.Attributes;
using Strategos.Builders;
using Strategos.Definitions;
using OpenClaw.StrategosWorkflowHost.Steps;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

// Smoke workflow: proves the Strategos event-sourced source generator emits a saga +
// AddSmokeWorkflow() registration that boots under WebApplication + UseWolverine + Marten.
[Workflow("smoke", Persistence = PersistenceMode.EventSourced)]
public static partial class SmokeWorkflowDefinition
{
    public static WorkflowDefinition<SmokeState> Definition =>
        Workflow<SmokeState>
            .Create("smoke")
            .StartWith<NoopStep>()
            .Finally<NoopFinishStep>();
}
