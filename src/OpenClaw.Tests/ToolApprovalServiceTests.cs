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

    [Fact]
    public void TrySetDecision_DoubleApprove_SecondReturnsNotFound()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromMinutes(1));

        var first = service.TrySetDecision(
            request.ApprovalId, approved: true,
            requesterChannelId: "telegram", requesterSenderId: "user-1");

        var second = service.TrySetDecision(
            request.ApprovalId, approved: true,
            requesterChannelId: "telegram", requesterSenderId: "user-1");

        Assert.Equal(ToolApprovalDecisionResult.Recorded, first);
        Assert.Equal(ToolApprovalDecisionResult.NotFound, second);
    }

    [Fact]
    public async Task ListPending_ExpiredEntry_IsCleaned()
    {
        var service = new ToolApprovalService();
        service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromMilliseconds(1));

        await Task.Delay(50);

        var pending = service.ListPending("telegram", "user-1");
        Assert.Empty(pending);
    }

    [Fact]
    public async Task WaitForDecisionOutcomeAsync_ApprovedBeforeTimeout_ReturnsApproved()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromSeconds(2));

        var waitTask = service.WaitForDecisionOutcomeAsync(request.ApprovalId, TimeSpan.FromSeconds(2), CancellationToken.None);

        // Approve after a short delay
        await Task.Delay(50);
        service.TrySetDecision(request.ApprovalId, approved: true,
            requesterChannelId: "telegram", requesterSenderId: "user-1");

        var outcome = await waitTask;
        Assert.Equal(ToolApprovalWaitResult.Approved, outcome.Result);
    }

    [Fact]
    public async Task WaitForDecisionOutcomeAsync_Timeout_ReturnsTimedOut()
    {
        var service = new ToolApprovalService();
        var request = service.Create("sess-1", "telegram", "user-1", "shell", "{}", TimeSpan.FromSeconds(5));

        var outcome = await service.WaitForDecisionOutcomeAsync(
            request.ApprovalId, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Equal(ToolApprovalWaitResult.TimedOut, outcome.Result);
    }
}
