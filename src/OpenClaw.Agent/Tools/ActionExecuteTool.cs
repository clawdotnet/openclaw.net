using System.Text.Json;
using OpenClaw.Agent.Actions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

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
        var contractOverride = ContractExecutionOverride.Parse(argumentsJson);
        var proposalJson = contractOverride.ProposalJson;
        if (proposalJson is null)
            return ValueTask.FromResult(BuildFailure("invalid_proposal", "Proposal is required."));

        var normalized = ActionProposalBuilder.Normalize(proposalJson);
        if (!normalized.Success || normalized.Proposal is null)
            return ValueTask.FromResult(BuildFailure(normalized.ErrorCode ?? "invalid_proposal", normalized.ErrorMessage));

        var governanceMapping = BuildGovernanceMapping(normalized.Proposal);
        var decision = _policyEngine.Evaluate(normalized.Proposal);

        if (decision.Decision.Equals("policy_denied", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildFailure("policy_denied", "Policy denied execution.", decision, governanceMapping));

        if (contractOverride.Decision is not null)
        {
            var validation = ConnectorActionContractValidator.ValidateForExecution(new ConnectorActionExecuteRequest
            {
                Proposal = normalized.Proposal,
                Decision = contractOverride.Decision,
                RiskLevel = contractOverride.RiskLevel,
                Approval = contractOverride.Approval
            });
            if (!validation.Success)
                return ValueTask.FromResult(BuildFailure(validation.ErrorCode ?? "invalid_request", validation.ErrorMessage, governanceMapping: governanceMapping));

            var callerDecision = BuildCallerDecision(contractOverride.Decision, contractOverride.RiskLevel);
            return contractOverride.Decision switch
            {
                "proceed" => ValueTask.FromResult(BuildDecisionResult("execution_started", callerDecision, governanceMapping)),
                "require_approval" => ValueTask.FromResult(BuildDecisionResult("execution_started", callerDecision, governanceMapping)),
                "reject" => ValueTask.FromResult(BuildFailure("policy_denied", "Execution rejected by caller contract.", callerDecision, governanceMapping)),
                "escalate" => ValueTask.FromResult(BuildDecisionResult("proposal_only", callerDecision, governanceMapping)),
                _ => ValueTask.FromResult(BuildFailure("unsupported_decision", $"Unsupported decision '{contractOverride.Decision}'.", governanceMapping: governanceMapping))
            };
        }

        if (decision.Decision.Equals("require_approval", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildDecisionResult("pending_approval", decision, governanceMapping));

        if (decision.Decision.Equals("proposal_only", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(BuildDecisionResult("proposal_only", decision, governanceMapping));

        return ValueTask.FromResult(BuildDecisionResult("execution_started", decision, governanceMapping));
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

    private static ActionPolicyDecision BuildCallerDecision(string decision, string? riskLevel)
        => decision switch
        {
            "proceed" => new ActionPolicyDecision
            {
                Decision = "proceed",
                RiskLevel = riskLevel ?? "low",
                ReasonCodes = ["caller_decision"],
                RequiredApprovals = [],
                Constraints = []
            },
            "require_approval" => new ActionPolicyDecision
            {
                Decision = "require_approval",
                RiskLevel = riskLevel ?? "medium",
                ReasonCodes = ["approval_required", "approval_validated"],
                RequiredApprovals = ["operator"],
                Constraints = []
            },
            "reject" => new ActionPolicyDecision
            {
                Decision = "reject",
                RiskLevel = riskLevel ?? "high",
                ReasonCodes = ["caller_rejected"],
                RequiredApprovals = [],
                Constraints = ["no_execution"]
            },
            "escalate" => new ActionPolicyDecision
            {
                Decision = "escalate",
                RiskLevel = riskLevel ?? "high",
                ReasonCodes = ["caller_escalated"],
                RequiredApprovals = ["operator"],
                Constraints = ["manual_review"]
            },
            _ => ActionPolicyDecision.ForDecision("proceed_execute")
        };

    private sealed class ContractExecutionOverride
    {
        public string? ProposalJson { get; private init; }
        public string? Decision { get; private init; }
        public string? RiskLevel { get; private init; }
        public ConnectorApprovalPayload? Approval { get; private init; }

        public static ContractExecutionOverride Parse(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return new ContractExecutionOverride();

            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                var root = document.RootElement;
                return new ContractExecutionOverride
                {
                    ProposalJson = TryReadProposal(root),
                    Decision = TryReadString(root, "decision"),
                    RiskLevel = TryReadString(root, "riskLevel"),
                    Approval = TryReadApproval(root)
                };
            }
            catch (JsonException)
            {
                return new ContractExecutionOverride();
            }
        }

        private static string? TryReadProposal(JsonElement root)
        {
            if (!root.TryGetProperty("proposal", out var proposalElement))
                return null;

            return proposalElement.ValueKind switch
            {
                JsonValueKind.String => proposalElement.GetString(),
                JsonValueKind.Object => proposalElement.GetRawText(),
                _ => null
            };
        }

        private static string? TryReadString(JsonElement root, string propertyName)
            => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static ConnectorApprovalPayload? TryReadApproval(JsonElement root)
        {
            if (!root.TryGetProperty("approval", out var approvalElement) || approvalElement.ValueKind != JsonValueKind.Object)
                return null;

            return JsonSerializer.Deserialize(approvalElement.GetRawText(), CoreJsonContext.Default.ConnectorApprovalPayload);
        }
    }

    private sealed record ActionGovernanceMapping(
        string SessionMetaRunRecord,
        string HarnessContractId,
        string PevId,
        string EvidenceBundleId);
}