using OpenClaw.Agent.Actions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ActionProposalBuilderTests
{
    [Fact]
    public void Normalize_ValidProposalJson_ReturnsTypedProposal()
    {
        var raw = """
        {
          "actionName":"sync_customer_tier",
          "source":{"metaSkill":"customer-risk-assistant","runId":"run_1","stepId":"step_1"},
          "trigger":{"condition":"riskLevel == medium","evidenceRefs":["ev_001"]},
          "target":{"system":"crm","operation":"updateCustomerTier"},
          "preChecks":[{"call":"crm.getCustomer","args":{"customerId":"C123"}}],
          "execution":[{"call":"crm.updateTier","args":{"customerId":"C123","tier":"B"}}],
          "rollback":[{"call":"crm.updateTier","args":{"customerId":"C123","tier":"A"}}],
          "idempotencyKey":"proposal-C123-20260715-01",
          "metadata":{"env":"prod"}
        }
        """;

        var result = ActionProposalBuilder.Normalize(raw);

        Assert.True(result.Success);
        Assert.NotNull(result.Proposal);
        Assert.Equal("sync_customer_tier", result.Proposal!.ActionName);
    }

    [Fact]
    public void Normalize_MissingExecution_ReturnsInvalidProposal()
    {
        var raw = """
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

        var result = ActionProposalBuilder.Normalize(raw);

        Assert.False(result.Success);
        Assert.Equal("invalid_proposal", result.ErrorCode);
    }

    [Fact]
    public void Normalize_ContainsSqlWriteField_ReturnsPolicyDenied()
    {
        var raw = """
        {
          "actionName":"dangerous_write",
          "source":{"metaSkill":"x","runId":"run_1","stepId":"step_1"},
          "trigger":{"condition":"true","evidenceRefs":[]},
          "target":{"system":"db","operation":"update"},
          "execution":[{"call":"sql.execute","args":{"sql":"update users set role='admin'"}}],
          "idempotencyKey":"k1",
          "metadata":{"connectionString":"Server=prod;"}
        }
        """;

        var result = ActionProposalBuilder.Normalize(raw);

        Assert.False(result.Success);
        Assert.Equal("policy_denied", result.ErrorCode);
    }
}
