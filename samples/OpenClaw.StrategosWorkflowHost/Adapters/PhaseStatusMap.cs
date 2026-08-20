using OpenClaw.Core.Models;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Pure function: maps a Strategos phase string to one of OpenClaw's six workflow status literals.
// Unknown phases fail safe to "running" (logged by the adapter), matching MafDurableHttpWorkflowRunner's
// NormalizeStatus which substitutes "running" for blank statuses.
public static class PhaseStatusMap
{
    public static string ToOpenClawStatus(string phase) => phase switch
    {
        "NotStarted" => AgentWorkflowStatuses.Queued,
        "AwaitingApproval" => AgentWorkflowStatuses.WaitingForInput,
        "Completed" => AgentWorkflowStatuses.Completed,
        "Failed" => AgentWorkflowStatuses.Failed,
        "Compensated" => AgentWorkflowStatuses.Failed,
        "Cancelled" => AgentWorkflowStatuses.Cancelled,
        _ when phase.StartsWith("Executing", StringComparison.Ordinal) => AgentWorkflowStatuses.Running,
        _ => AgentWorkflowStatuses.Running,
    };
}
