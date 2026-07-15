using OpenClaw.Agent.Actions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ActionApprovalRecordTests
{
    [Fact]
    public void TryValidate_MissingRequiredField_ReturnsApprovalDenied()
    {
        var record = new ActionApprovalRecord
        {
            Approver = "u_zhangsan",
            DecisionAt = "",
            DecisionReason = "approved by operator",
            TicketRef = "ITSM-1",
            DecisionType = "approve"
        };

        var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

        Assert.False(valid);
        Assert.Equal("approval_denied", errorCode);
    }

    [Fact]
    public void TryValidate_Iso8601UtcDecisionAt_ReturnsValid()
    {
        var record = new ActionApprovalRecord
        {
            Approver = "u_zhangsan",
            DecisionAt = "2026-07-15T09:12:33Z",
            DecisionReason = "risk accepted",
            TicketRef = "ITSM-2"
        };

        var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

        Assert.True(valid);
        Assert.Equal(string.Empty, errorCode);
    }

    [Fact]
    public void TryValidate_DecisionTypeInvalid_ReturnsApprovalDenied()
    {
        var record = new ActionApprovalRecord
        {
            Approver = "u_lisi",
            DecisionAt = "2026-07-15T09:12:33Z",
            DecisionReason = "manual review",
            TicketRef = "ITSM-3",
            DecisionType = "hold"
        };

        var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

        Assert.False(valid);
        Assert.Equal("approval_denied", errorCode);
    }

    [Fact]
    public void TryValidate_DecisionTypeOptional_ReturnsValid()
    {
        var record = new ActionApprovalRecord
        {
            Approver = "u_wangwu",
            DecisionAt = "2026-07-15T09:12:33.0000000Z",
            DecisionReason = "approved",
            TicketRef = "ITSM-4",
            DecisionType = null
        };

        var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

        Assert.True(valid);
        Assert.Equal(string.Empty, errorCode);
    }

    [Fact]
    public void TryValidate_DecisionAtNotUtc_ReturnsApprovalDenied()
    {
        var record = new ActionApprovalRecord
        {
            Approver = "u_wangwu",
            DecisionAt = "2026-07-15T17:12:33+08:00",
            DecisionReason = "approved",
            TicketRef = "ITSM-5",
            DecisionType = "approve"
        };

        var valid = ActionApprovalRecordValidator.TryValidate(record, out var errorCode);

        Assert.False(valid);
        Assert.Equal("approval_denied", errorCode);
    }
}
