namespace OpenClaw.StrategosWorkflowHost.Workflows;

// Strategos has no built-in Operator/Admin marker (verified: TApprover is user-defined).
// These marker classes tag AwaitApproval<T> / EscalateTo<T> so the source generator can route
// the approval commands to the right resume handler.
public sealed class Operator { }
public sealed class Admin { }