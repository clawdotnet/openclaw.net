using System.Text.Json;
using OpenClaw.Agent.Actions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ActionPolicyEngineTests
{
    [Fact]
    public void Evaluate_NoPolicyMetadataKnownSystem_ReturnsProceedExecute()
    {
        var engine = new ActionPolicyEngine();
        var proposal = BuildProposal("crm", metadata: null);

        var decision = engine.Evaluate(proposal);

        Assert.Equal("proceed_execute", decision.Decision);
        Assert.Equal("low", decision.RiskLevel);
        Assert.Contains("policy_passed", decision.ReasonCodes);
    }

    [Fact]
    public void Evaluate_PolicyDecisionRequireApproval_ReturnsRequireApproval()
    {
        var engine = new ActionPolicyEngine();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policyDecision"] = "require_approval"
        };
        var proposal = BuildProposal("crm", metadata);

        var decision = engine.Evaluate(proposal);

        Assert.Equal("require_approval", decision.Decision);
        Assert.Equal("medium", decision.RiskLevel);
        Assert.Contains("approval_required", decision.ReasonCodes);
    }

    [Fact]
    public void Evaluate_PolicyDecisionProposalOnly_ReturnsProposalOnly()
    {
        var engine = new ActionPolicyEngine();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policyDecision"] = "proposal_only"
        };
        var proposal = BuildProposal("crm", metadata);

        var decision = engine.Evaluate(proposal);

        Assert.Equal("proposal_only", decision.Decision);
        Assert.Contains("proposal_only_mode", decision.ReasonCodes);
        Assert.Contains("no_execution", decision.Constraints);
    }

    [Fact]
    public void Evaluate_UnknownSystem_ReturnsPolicyDenied()
    {
        var engine = new ActionPolicyEngine();
        var proposal = BuildProposal("unknown_db_system");

        var decision = engine.Evaluate(proposal);

        Assert.Equal("policy_denied", decision.Decision);
        Assert.Equal("high", decision.RiskLevel);
        Assert.Contains("unknown_connector", decision.ReasonCodes);
    }

    [Fact]
    public void Evaluate_KnownSystems_AllAccepted()
    {
        var engine = new ActionPolicyEngine();
        var knownSystems = new[] { "crm", "salesforce", "hubspot", "zendesk", "stripe", "slack", "notion" };

        foreach (var system in knownSystems)
        {
            var proposal = BuildProposal(system);
            var decision = engine.Evaluate(proposal);
            Assert.NotEqual("policy_denied", decision.Decision);
        }
    }

    private static ActionProposal BuildProposal(
        string targetSystem,
        IReadOnlyDictionary<string, string>? metadata = null)
        => new()
        {
            ActionName = "test_action",
            Source = new ActionProposalSource
            {
                MetaSkill = "test-skill",
                RunId = "run_1",
                StepId = "step_1"
            },
            Trigger = new ActionProposalTrigger
            {
                Condition = "true",
                EvidenceRefs = ["ev_001"]
            },
            Target = new ActionProposalTarget
            {
                System = targetSystem,
                Operation = "testOp"
            },
            Execution =
            [
                new ActionCall { Call = $"{targetSystem}.testOp", Args = new Dictionary<string, JsonElement>() }
            ],
            IdempotencyKey = $"test-{targetSystem}-001",
            Metadata = metadata ?? new Dictionary<string, string>()
        };
}