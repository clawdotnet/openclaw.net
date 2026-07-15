using OpenClaw.Agent.Tools;
using Xunit;
using System.Text.Json;

namespace OpenClaw.Tests;

public sealed class ActionExecuteToolTests
{
    [Fact]
    public async Task ExecuteAsync_LowRiskDecision_ReturnsProceedExecute()
    {
        var tool = new ActionExecuteTool();
        var result = await tool.ExecuteAsync(BuildArguments(BuildProposalJson()), TestContext.Current.CancellationToken);

        Assert.Contains("\"decision\":\"proceed_execute\"", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RequireApprovalDecision_ReturnsPendingApprovalAndNoExecutionStart()
    {
        var tool = new ActionExecuteTool();
        var proposal = BuildProposalJson("crm", "updateCustomerTier", metadataFragment: "\"policyDecision\":\"require_approval\"");

        var result = await tool.ExecuteAsync(BuildArguments(proposal), TestContext.Current.CancellationToken);

        Assert.Contains("pending_approval", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execution_started", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalOnlyDecision_ReturnsProposalOnlyStatus()
    {
        var tool = new ActionExecuteTool();
        var proposal = BuildProposalJson("crm", "updateCustomerTier", metadataFragment: "\"policyDecision\":\"proposal_only\"");

        var result = await tool.ExecuteAsync(BuildArguments(proposal), TestContext.Current.CancellationToken);

        Assert.Contains("\"decision\":\"proposal_only\"", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"proposal_only\"", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownConnector_ReturnsPolicyDenied()
    {
        var tool = new ActionExecuteTool();
        var proposal = BuildProposalJson(targetSystem: "unknown_connector", targetOperation: "write");

        var result = await tool.ExecuteAsync(BuildArguments(proposal), TestContext.Current.CancellationToken);

        Assert.Contains("policy_denied", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ProducesGovernanceMappingPayload()
    {
        var tool = new ActionExecuteTool();
        var proposal = BuildProposalJson(
            metadataFragment: "\"policyDecision\":\"proposal_only\",\"harnessContractId\":\"hctr_123\",\"pevId\":\"pev_456\",\"evidenceBundleId\":\"evb_789\"");

        var result = await tool.ExecuteAsync(BuildArguments(proposal), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        var mapping = document.RootElement.GetProperty("governanceMapping");
        Assert.Equal("session_meta_run_record_pending", mapping.GetProperty("sessionMetaRunRecord").GetString());
        Assert.Equal("hctr_123", mapping.GetProperty("harnessContractId").GetString());
        Assert.Equal("pev_456", mapping.GetProperty("pevId").GetString());
        Assert.Equal("evb_789", mapping.GetProperty("evidenceBundleId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_InvalidProposalFromBuilder_ReturnsInvalidProposal()
    {
        var tool = new ActionExecuteTool();
        var invalidProposal = """
        {
          "actionName":"sync_customer_tier",
          "source":{"metaSkill":"customer-risk-assistant","runId":"run_1","stepId":"step_1"},
          "trigger":{"condition":"riskLevel == medium","evidenceRefs":["ev_001"]},
          "target":{"system":"crm","operation":"updateCustomerTier"},
          "execution":[],
          "idempotencyKey":"proposal-C123-20260715-01",
          "metadata":{"env":"prod"}
        }
        """;

        var result = await tool.ExecuteAsync(BuildArguments(invalidProposal), TestContext.Current.CancellationToken);

        Assert.Contains("invalid_proposal", result, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildArguments(string proposalJson)
        => "{\"proposal\":" + proposalJson + "}";

    private static string BuildProposalJson(
        string targetSystem = "crm",
        string targetOperation = "updateCustomerTier",
        string? metadataFragment = null)
    {
        var metadataBody = string.IsNullOrWhiteSpace(metadataFragment)
            ? "\"env\":\"prod\""
            : "\"env\":\"prod\"," + metadataFragment;

                return "{" +
                             "\"actionName\":\"sync_customer_tier\"," +
                             "\"source\":{\"metaSkill\":\"customer-risk-assistant\",\"runId\":\"run_1\",\"stepId\":\"step_1\"}," +
                             "\"trigger\":{\"condition\":\"riskLevel == medium\",\"evidenceRefs\":[\"ev_001\"]}," +
                             "\"target\":{\"system\":\"" + targetSystem + "\",\"operation\":\"" + targetOperation + "\"}," +
                             "\"preChecks\":[{\"call\":\"crm.getCustomer\",\"args\":{\"customerId\":\"C123\"}}]," +
                             "\"execution\":[{\"call\":\"crm.updateTier\",\"args\":{\"customerId\":\"C123\",\"tier\":\"B\"}}]," +
                             "\"rollback\":[{\"call\":\"crm.updateTier\",\"args\":{\"customerId\":\"C123\",\"tier\":\"A\"}}]," +
                             "\"idempotencyKey\":\"proposal-C123-20260715-01\"," +
                             "\"metadata\":{" + metadataBody + "}" +
                             "}";
    }
}