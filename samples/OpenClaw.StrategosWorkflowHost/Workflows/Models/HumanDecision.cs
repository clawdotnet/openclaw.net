namespace OpenClaw.StrategosWorkflowHost.Workflows.Models;

// Plain POCO (not part of the Strategos source-gen cycle). A human approval decision captured
// at the AwaitApproval gate and replayed by the saga.
public sealed record HumanDecision(bool Approved, string? ActorId, string? Comment);
