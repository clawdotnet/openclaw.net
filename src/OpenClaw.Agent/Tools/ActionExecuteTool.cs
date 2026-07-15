using System.Text.Json;
using OpenClaw.Agent.Actions;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class ActionExecuteTool : ITool
{
    private readonly IActionPolicyEngine _policyEngine;

    public ActionExecuteTool()
        : this(new ActionPolicyEngine())
    {
    }

    internal ActionExecuteTool(IActionPolicyEngine policyEngine)
    {
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
    }

    public string Name => "action_execute";

    public string Description => "Evaluate a normalized action proposal and route by policy decision.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "proposal": {
              "description": "Action proposal payload as object or JSON string"
            }
          },
          "required": ["proposal"]
        }
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var proposalJson = ExtractProposal(argumentsJson);
        if (proposalJson is null)
            return ValueTask.FromResult(BuildFailure("invalid_proposal", "Proposal is required."));

        var normalized = ActionProposalBuilder.Normalize(proposalJson);
        if (!normalized.Success || normalized.Proposal is null)
            return ValueTask.FromResult(BuildFailure(normalized.ErrorCode ?? "invalid_proposal", normalized.ErrorMessage));

        var governanceMapping = BuildGovernanceMapping(normalized.Proposal);
        var decision = _policyEngine.Evaluate(normalized.Proposal);

        if (decision.Decision.Equals("policy_denied", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildFailure("policy_denied", "Policy denied execution.", decision, governanceMapping));

        if (decision.Decision.Equals("require_approval", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildDecisionResult("pending_approval", decision, governanceMapping));

        if (decision.Decision.Equals("proposal_only", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildDecisionResult("proposal_only", decision, governanceMapping));

        return ValueTask.FromResult(BuildDecisionResult("execution_started", decision, governanceMapping));
    }

    private static string? ExtractProposal(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("proposal", out var proposalElement))
                return null;

            return proposalElement.ValueKind switch
            {
                JsonValueKind.String => proposalElement.GetString(),
                JsonValueKind.Object => proposalElement.GetRawText(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildFailure(
        string failureCode,
        string? message,
        ActionPolicyDecision? decision = null,
        ActionGovernanceMapping? governanceMapping = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("status", "failed");
        writer.WriteString("failureCode", failureCode);
        if (!string.IsNullOrWhiteSpace(message))
            writer.WriteString("message", message);

        if (decision is not null)
            WriteDecision(writer, decision);

        if (governanceMapping is not null)
            WriteGovernanceMapping(writer, governanceMapping);

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildDecisionResult(string status, ActionPolicyDecision decision, ActionGovernanceMapping governanceMapping)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("status", status);
        WriteDecision(writer, decision);
        WriteGovernanceMapping(writer, governanceMapping);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ActionGovernanceMapping BuildGovernanceMapping(OpenClaw.Core.Models.ActionProposal proposal)
    {
        var metadata = proposal.Metadata;
        var key = proposal.IdempotencyKey;

        return new ActionGovernanceMapping(
            SessionMetaRunRecord: "session_meta_run_record_pending",
            HarnessContractId: GetMetadataOrDefault(metadata, "harnessContractId", $"hctr_{key}"),
            PevId: GetMetadataOrDefault(metadata, "pevId", $"pev_{key}"),
            EvidenceBundleId: GetMetadataOrDefault(metadata, "evidenceBundleId", $"evb_{key}"));
    }

    private static string GetMetadataOrDefault(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static void WriteGovernanceMapping(Utf8JsonWriter writer, ActionGovernanceMapping governanceMapping)
    {
        writer.WritePropertyName("governanceMapping");
        writer.WriteStartObject();
        writer.WriteString("sessionMetaRunRecord", governanceMapping.SessionMetaRunRecord);
        writer.WriteString("harnessContractId", governanceMapping.HarnessContractId);
        writer.WriteString("pevId", governanceMapping.PevId);
        writer.WriteString("evidenceBundleId", governanceMapping.EvidenceBundleId);
        writer.WriteEndObject();
    }

    private static void WriteDecision(Utf8JsonWriter writer, ActionPolicyDecision decision)
    {
        writer.WriteString("decision", decision.Decision);
        writer.WriteString("riskLevel", decision.RiskLevel);

        writer.WritePropertyName("reasonCodes");
        writer.WriteStartArray();
        foreach (var reasonCode in decision.ReasonCodes)
            writer.WriteStringValue(reasonCode);
        writer.WriteEndArray();

        writer.WritePropertyName("requiredApprovals");
        writer.WriteStartArray();
        foreach (var requiredApproval in decision.RequiredApprovals)
            writer.WriteStringValue(requiredApproval);
        writer.WriteEndArray();

        writer.WritePropertyName("constraints");
        writer.WriteStartArray();
        foreach (var constraint in decision.Constraints)
            writer.WriteStringValue(constraint);
        writer.WriteEndArray();
    }

    private sealed record ActionGovernanceMapping(
        string SessionMetaRunRecord,
        string HarnessContractId,
        string PevId,
        string EvidenceBundleId);
}