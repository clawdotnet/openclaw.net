using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Workflows;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Pure function: ReviewState + port id -> OpenClaw AgentWorkflowPendingInput describing
// the human-approval port the gateway should expose. Payload carries workflow context
// (workflowId, summary, confidence, reviews) so the host UI can render the gate.
public static class PendingInputBuilder
{
    public static IReadOnlyList<AgentWorkflowPendingInput> Build(ReviewState state, string portId)
    {
        var payload = new JsonObject
        {
            ["workflowId"] = state.WorkflowId,
            ["summary"] = state.AggregatedSummary ?? "Approval required before executing the approved action.",
            ["confidence"] = state.AggregateConfidence,
            ["reviews"] = new JsonArray(state.Reviews.Select(v => (JsonNode)new JsonObject
            {
                ["role"] = v.Role,
                ["verdict"] = v.Verdict,
                ["summary"] = v.Summary,
                ["confidence"] = v.Confidence,
            }).ToArray()),
        };
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return [new AgentWorkflowPendingInput
        {
            PortId = portId,
            Summary = "Approve or reject the aggregated agent review.",
            Payload = doc.RootElement.Clone(),
            Metadata = new Dictionary<string, string> { ["requestPort"] = "HumanApproval" }
        }];
    }
}