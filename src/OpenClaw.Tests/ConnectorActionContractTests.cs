using OpenClaw.Core.ConnectorActions;
using OpenClaw.Core.Models;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConnectorActionContractTests
{
    [Fact]
    public void ValidateForExecution_RequireApprovalMissingTicketRef_Fails()
    {
        var request = new ConnectorActionExecuteRequest
        {
            Proposal = BuildValidProposal(),
            Decision = "require_approval",
            Approval = new ConnectorApprovalPayload
            {
                Approver = "u_zhangsan",
                DecisionAt = "2026-07-15T08:30:00Z",
                DecisionReason = "ok",
                TicketRef = ""
            }
        };

        var result = ConnectorActionContractValidator.ValidateForExecution(request);

        Assert.False(result.Success);
        Assert.Equal("approval_denied", result.ErrorCode);
    }

    [Fact]
    public void ValidateForExecution_UnsupportedDecision_Fails()
    {
        var request = new ConnectorActionExecuteRequest
        {
            Proposal = BuildValidProposal(),
            Decision = "pause"
        };

        var result = ConnectorActionContractValidator.ValidateForExecution(request);

        Assert.False(result.Success);
        Assert.Equal("unsupported_decision", result.ErrorCode);
    }

    [Fact]
    public void ValidateForExecution_CaseVariantDecision_Fails()
    {
        var request = new ConnectorActionExecuteRequest
        {
            Proposal = BuildValidProposal(),
            Decision = "Require_Approval"
        };

        var result = ConnectorActionContractValidator.ValidateForExecution(request);

        Assert.False(result.Success);
        Assert.Equal("unsupported_decision", result.ErrorCode);
    }

    [Fact]
    public void ValidateForExecution_NullRequest_ReturnsInvalidRequest()
    {
        var result = ConnectorActionContractValidator.ValidateForExecution(null!);

        Assert.False(result.Success);
        Assert.Equal("invalid_request", result.ErrorCode);
    }

    [Fact]
    public void ExportV1_ReturnsJsonSchemaWithApprovalFields()
    {
        var schema = ConnectorActionSchemaExporter.ExportV1();
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());

        var required = root.GetProperty("required").EnumerateArray().Select(element => element.GetString()).ToArray();
        Assert.Contains("proposal", required);
        Assert.Contains("decision", required);
        Assert.DoesNotContain("approval", required);

        var decisionEnum = root.GetProperty("properties").GetProperty("decision").GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "proceed", "require_approval", "reject", "escalate" }, decisionEnum);

        var approvalRootSchema = root.GetProperty("properties").GetProperty("approval");
        Assert.Equal("object", approvalRootSchema.GetProperty("type").GetString());
        Assert.False(approvalRootSchema.TryGetProperty("required", out _));

        var conditional = root.GetProperty("allOf")[0];
        Assert.Equal("require_approval", conditional.GetProperty("if").GetProperty("properties").GetProperty("decision").GetProperty("const").GetString());

        var thenRequired = conditional.GetProperty("then").GetProperty("required").EnumerateArray().Select(element => element.GetString()).ToArray();
        Assert.Contains("approval", thenRequired);

        var approvalRequired = conditional.GetProperty("then").GetProperty("properties").GetProperty("approval").GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        Assert.Equal(new[] { "approver", "decisionAt", "decisionReason", "ticketRef" }, approvalRequired);
    }

    private static ActionProposal BuildValidProposal()
        => new()
        {
            ActionName = "update_customer_tier",
            Source = new ActionProposalSource
            {
                MetaSkill = "skill://connector",
                RunId = "run_001",
                StepId = "step_001"
            },
            Trigger = new ActionProposalTrigger
            {
                Condition = "customer tier change requested",
                EvidenceRefs = ["ev-1"]
            },
            Target = new ActionProposalTarget
            {
                System = "crm",
                Operation = "update_customer_tier"
            },
            Execution =
            [
                new ActionCall
                {
                    Call = "set_tier",
                    Args = new Dictionary<string, System.Text.Json.JsonElement>()
                }
            ],
            IdempotencyKey = "proposal-001"
        };
}
