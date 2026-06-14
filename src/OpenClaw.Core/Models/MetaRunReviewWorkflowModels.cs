namespace OpenClaw.Core.Models;

public static class MetaRunReviewWorkflowKinds
{
    public const string DurableKind = "meta_run_review_workflow";
}

public static class MetaRunReviewWorkflowStages
{
    public const string DecisionRecorded = "decision_recorded";
    public const string RolledBack = "rolled_back";
}

public static class MetaRunReviewWorkflowActions
{
    public const string Accept = "accept";
    public const string Dismiss = "dismiss";
    public const string Rollback = "rollback";
    public const string Change = "change";
}

public static class MetaRunReviewWorkflowMetadata
{
    public const string Session = "session";
    public const string Proposal = "proposal";
    public const string Workflow = "workflow";
    public const string Stage = "stage";
    public const string LastAction = "last_action";
    public const string LastActor = "last_actor";
    public const string LastChangedAt = "last_changed_at";
    public const string TransitionCount = "transition_count";
}