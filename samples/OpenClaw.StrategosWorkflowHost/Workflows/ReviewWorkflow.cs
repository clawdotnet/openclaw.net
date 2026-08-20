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
// IMPORTANT: OnLowConfidence handler lambda body may only contain .Then<THandler>() calls —
// AwaitApproval is not recognized by the analyzer inside an OnLowConfidence handler (StepExtractor
// only scans for Then<T>() invocations, see StepExtractor.ExtractLowConfidenceHandlerChain). So
// we route low-confidence flow through a no-op RequestHumanReview step, then the top-level
// .AwaitApproval<Operator>() pauses for the human decision regardless of confidence path.
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
            .Then<AssessConfidence>(step => step
                .RequireConfidence(0.85)
                .OnLowConfidence(alt => alt.Then<RequestHumanReview>()))
            .AwaitApproval<Operator>(approval => approval
                .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                .WithTimeout(TimeSpan.FromHours(4))
                .OnTimeout(esc => esc.EscalateTo<Admin>(a => a
                    .WithContextFrom(s => "Escalated after approval timeout."))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())
            .OnFailure(flow => flow.Then<NotifyFailure>())
            .Finally<EmitAuditTrace>();
}