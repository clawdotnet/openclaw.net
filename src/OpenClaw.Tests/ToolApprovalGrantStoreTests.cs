using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolApprovalGrantStoreTests
{
    [Fact]
    public void TryConsume_DecrementsRemainingUses_AndRemovesGrantAtZero()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-approval-grants", Guid.NewGuid().ToString("N"));
        var store = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        store.AddOrUpdate(new ToolApprovalGrant
        {
            Id = "grant_1",
            Scope = "sender_tool_window",
            ChannelId = "telegram",
            SenderId = "owner",
            ToolName = "shell",
            GrantedBy = "tester",
            GrantSource = "test",
            RemainingUses = 2
        });

        var first = store.TryConsume(sessionId: "sess1", channelId: "telegram", senderId: "owner", toolName: "shell");
        var second = store.TryConsume(sessionId: "sess1", channelId: "telegram", senderId: "owner", toolName: "shell");
        var third = store.TryConsume(sessionId: "sess1", channelId: "telegram", senderId: "owner", toolName: "shell");

        Assert.NotNull(first);
        Assert.Equal(1, first!.RemainingUses);
        Assert.NotNull(second);
        Assert.Equal("grant_1", second!.Id);
        Assert.Null(third);
        Assert.Empty(store.List());
    }
}
