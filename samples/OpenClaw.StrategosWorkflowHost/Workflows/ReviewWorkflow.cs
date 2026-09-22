using OpenClaw.StrategosWorkflowHost.Steps;
using Strategos.Attributes;
using Strategos.Builders;
using Strategos.Definitions;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

// Durable agent review workflow (event-sourced). Source generator emits:
//   - DurableAgentReviewStarted (seed event, carries InitialState)
//   - DurableAgentReviewSaga (Wolverine saga)
//   - StartDurableAgentReviewCommand (initial command, carries InitialState)
//   - ResumeOperatorApprovalCommand (approval-resume command from AwaitApproval<Operator>)
//   - {StepClassName}Completed for each step referenced below
//
// The top-level .AwaitApproval<Operator>() pauses for the human decision after confidence
// assessment. Strategos 3.x rejects confidence gating on the step immediately preceding an
// approval point, so the approval remains explicit rather than being conditionally configured.
[Workflow("durable-agent-review", Persistence = PersistenceMode.EventSourced)]
public static partial class DurableAgentReviewWorkflowDefinition
{
    public static WorkflowDefinition<ReviewState> Definition =>
        Workflow<ReviewState>
            .Create("durable-agent-review")
            .StartWith<PlanExecutor>()
            .Fork(
                path => path.Then<SecurityReviewer>(),
                path => path.Then<ArchitectureReviewer>(),
                path => path.Then<CostReviewer>())
            .Join<AggregateReviews>()
            .Then<AssessConfidence>()
            .AwaitApproval<Operator>(approval => approval
                .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                .WithTimeout(TimeSpan.FromHours(4))
                .OnTimeout(esc => esc.EscalateTo<Admin>(a => a
                    .WithContextFrom(s => "Escalated after approval timeout."))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())
            .OnFailure(flow => flow.Then<NotifyFailure>())
            .Finally<EmitAuditTrace>();
}