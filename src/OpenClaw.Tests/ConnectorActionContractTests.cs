using OpenClaw.Core.ConnectorActions;
using OpenClaw.Core.Models;
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
    public void ExportV1_ReturnsJsonSchemaWithApprovalFields()
    {
        var schema = ConnectorActionSchemaExporter.ExportV1();

        Assert.Contains("\"decision\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"approval\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"ticketRef\"", schema, StringComparison.Ordinal);
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
