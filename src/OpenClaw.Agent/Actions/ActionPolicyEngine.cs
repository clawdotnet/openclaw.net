using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Actions;

internal interface IActionPolicyEngine
{
    ActionPolicyDecision Evaluate(ActionProposal proposal);
}

internal sealed class ActionPolicyEngine : IActionPolicyEngine
{
    private static readonly HashSet<string> KnownConnectorSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "crm",
        "salesforce",
        "hubspot",
        "zendesk",
        "stripe",
        "slack",
        "notion"
    };

    public ActionPolicyDecision Evaluate(ActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (!KnownConnectorSystems.Contains(proposal.Target.System))
        {
            return ActionPolicyDecision.PolicyDenied(
                riskLevel: "high",
                reasonCodes: ["unknown_connector"]);
        }

        if (proposal.Metadata.TryGetValue("policyDecision", out var configuredDecision)
            && TryNormalizeDecision(configuredDecision, out var normalizedDecision))
        {
            return ActionPolicyDecision.ForDecision(
                normalizedDecision,
                proposal.Metadata.TryGetValue("riskLevel", out var metadataRiskLevel)
                    && TryNormalizeRiskLevel(metadataRiskLevel, out var normalizedMetadataRiskLevel)
                    ? normalizedMetadataRiskLevel
                    : null);
        }

        if (proposal.Metadata.TryGetValue("riskLevel", out var explicitRiskLevel)
            && !TryNormalizeRiskLevel(explicitRiskLevel, out _))
        {
            return new ActionPolicyDecision
            {
                Decision = "proposal_only",
                RiskLevel = "high",
                ReasonCodes = ["unknown_risk"],
                RequiredApprovals = [],
                Constraints = ["no_execution"]
            };
        }

        var riskLevel = ClassifyRiskLevel(proposal);
        return riskLevel switch
        {
            "low" => ActionPolicyDecision.ForDecision("proceed_execute", riskLevel),
            "medium" => ActionPolicyDecision.ForDecision("require_approval", riskLevel),
            "high" or "critical" => ActionPolicyDecision.ForDecision("proposal_only", riskLevel),
            _ => new ActionPolicyDecision
            {
                Decision = "proposal_only",
                RiskLevel = "high",
                ReasonCodes = ["unknown_risk"],
                RequiredApprovals = [],
                Constraints = ["no_execution"]
            }
        };
    }

    private static string ClassifyRiskLevel(ActionProposal proposal)
    {
        if (proposal.Metadata.TryGetValue("riskLevel", out var riskLevel)
            && TryNormalizeRiskLevel(riskLevel, out var normalizedRiskLevel))
        {
            return normalizedRiskLevel;
        }

        var environment = proposal.Metadata.TryGetValue("env", out var envValue) ? envValue : "dev";
        var isProd = string.Equals(environment, "prod", StringComparison.OrdinalIgnoreCase);
        var hasDestructiveOperation = proposal.Execution.Any(step =>
            step.Call.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || step.Call.Contains("drop", StringComparison.OrdinalIgnoreCase)
            || step.Call.Contains("remove", StringComparison.OrdinalIgnoreCase));

        return isProd && hasDestructiveOperation ? "high" : "low";
    }

    private static bool TryNormalizeDecision(string? decision, out string normalizedDecision)
    {
        normalizedDecision = string.Empty;
        if (string.IsNullOrWhiteSpace(decision))
            return false;

        if (decision.Equals("proceed_execute", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("require_approval", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("proposal_only", StringComparison.OrdinalIgnoreCase))
        {
            normalizedDecision = decision.ToLowerInvariant();
            return true;
        }

        return false;
    }

    private static bool TryNormalizeRiskLevel(string? riskLevel, out string normalizedRiskLevel)
    {
        normalizedRiskLevel = string.Empty;
        if (string.IsNullOrWhiteSpace(riskLevel))
            return false;

        var normalized = riskLevel.Trim().ToLowerInvariant();
        if (normalized is "low" or "medium" or "high" or "critical")
        {
            normalizedRiskLevel = normalized;
            return true;
        }

        return false;
    }
}

internal sealed class ActionPolicyDecision
{
    public required string Decision { get; init; }
    public required string RiskLevel { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
    public IReadOnlyList<string> RequiredApprovals { get; init; } = [];
    public IReadOnlyList<string> Constraints { get; init; } = [];

    public static ActionPolicyDecision ForDecision(string decision, string? riskLevel = null)
        => decision switch
        {
            "require_approval" => new ActionPolicyDecision
            {
                Decision = "require_approval",
                RiskLevel = riskLevel ?? "medium",
                ReasonCodes = ["approval_required"],
                RequiredApprovals = ["operator"],
                Constraints = []
            },
            "proposal_only" => new ActionPolicyDecision
            {
                Decision = "proposal_only",
                RiskLevel = riskLevel ?? "high",
                ReasonCodes = ["proposal_only_mode"],
                RequiredApprovals = [],
                Constraints = ["no_execution"]
            },
            _ => new ActionPolicyDecision
            {
                Decision = "proceed_execute",
                RiskLevel = riskLevel ?? "low",
                ReasonCodes = ["policy_passed"],
                RequiredApprovals = [],
                Constraints = []
            }
        };

    public static ActionPolicyDecision PolicyDenied(string riskLevel, IReadOnlyList<string> reasonCodes)
        => new()
        {
            Decision = "policy_denied",
            RiskLevel = riskLevel,
            ReasonCodes = reasonCodes,
            RequiredApprovals = [],
            Constraints = []
        };
}