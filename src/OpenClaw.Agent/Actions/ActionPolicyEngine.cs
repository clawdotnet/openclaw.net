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
            return ActionPolicyDecision.ForDecision(normalizedDecision);
        }

        return ActionPolicyDecision.ForDecision("proceed_execute");
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
}

internal sealed class ActionPolicyDecision
{
    public required string Decision { get; init; }
    public required string RiskLevel { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
    public IReadOnlyList<string> RequiredApprovals { get; init; } = [];
    public IReadOnlyList<string> Constraints { get; init; } = [];

    public static ActionPolicyDecision ForDecision(string decision)
        => decision switch
        {
            "require_approval" => new ActionPolicyDecision
            {
                Decision = "require_approval",
                RiskLevel = "medium",
                ReasonCodes = ["approval_required"],
                RequiredApprovals = ["operator"],
                Constraints = []
            },
            "proposal_only" => new ActionPolicyDecision
            {
                Decision = "proposal_only",
                RiskLevel = "low",
                ReasonCodes = ["proposal_only_mode"],
                RequiredApprovals = [],
                Constraints = ["no_execution"]
            },
            _ => new ActionPolicyDecision
            {
                Decision = "proceed_execute",
                RiskLevel = "low",
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