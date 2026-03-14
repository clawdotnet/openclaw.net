using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ApprovalAuditStoreTests
{
    [Fact]
    public void RecordCreated_AndDecision_AreQueryable()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-approval-audit", Guid.NewGuid().ToString("N"));
        var store = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var request = new ToolApprovalRequest
        {
            ApprovalId = "apr_123",
            SessionId = "sess1",
            ChannelId = "telegram",
            SenderId = "user1",
            ToolName = "shell",
            Arguments = """{"cmd":"ls -la"}"""
        };

        store.RecordCreated(request);
        store.RecordDecision(request, approved: true, decisionSource: "chat", actorChannelId: "telegram", actorSenderId: "user1");

        var all = store.Query(new ApprovalHistoryQuery { Limit = 10 });
        Assert.Equal(2, all.Count);
        Assert.Equal("decision", all[0].EventType);
        Assert.Equal("created", all[1].EventType);

        var filtered = store.Query(new ApprovalHistoryQuery { Limit = 10, ToolName = "shell" });
        Assert.Equal(2, filtered.Count);
    }
}
