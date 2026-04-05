using OpenClaw.Core.Pipeline;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolApprovalServiceTests
{
    [Fact]
    public void TrySetDecision_MatchingRequester_Records()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromMinutes(1));

        var result = service.TrySetDecision(
            request.ApprovalId,
            approved: true,
            requesterChannelId: "telegram",
            requesterSenderId: "user-1",
            requireRequesterMatch: true);

        Assert.Equal(ToolApprovalDecisionResult.Recorded, result);
    }

    [Fact]
    public void TrySetDecision_MismatchedRequester_IsRejected_AndRequestRemainsPending()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromMinutes(1));

        var result = service.TrySetDecision(
            request.ApprovalId,
            approved: true,
            requesterChannelId: "telegram",
            requesterSenderId: "attacker",
            requireRequesterMatch: true);

        Assert.Equal(ToolApprovalDecisionResult.Unauthorized, result);
        Assert.Contains(service.ListPending("telegram", "user-1"), p => p.ApprovalId == request.ApprovalId);
    }

    [Fact]
    public void TrySetDecision_AdminOverride_PathStillWorks()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromMinutes(1));

        var result = service.TrySetDecision(
            request.ApprovalId,
            approved: false,
            requesterChannelId: null,
            requesterSenderId: null,
            requireRequesterMatch: false);

        Assert.Equal(ToolApprovalDecisionResult.Recorded, result);
    }
}
