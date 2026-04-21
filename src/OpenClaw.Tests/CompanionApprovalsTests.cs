using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Core.Pipeline;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionApprovalsTests
{
    [Fact]
    public void FromRequest_CopiesCoreFields()
    {
        var req = new ToolApprovalRequest
        {
            ApprovalId = "apr_1",
            SessionId = "sess_1",
            ChannelId = "websocket",
            SenderId = "user@example.com",
            ToolName = "shell",
            Arguments = "{}",
            Action = "run",
            IsMutation = true,
            Summary = "Run a shell command."
        };

        var item = PendingApprovalItem.FromRequest(req);

        Assert.Equal("apr_1", item.ApprovalId);
        Assert.Equal("sess_1", item.SessionId);
        Assert.Equal("websocket", item.ChannelId);
        Assert.Equal("user@example.com", item.SenderId);
        Assert.Equal("shell", item.ToolName);
        Assert.Equal("run", item.Action);
        Assert.True(item.IsMutation);
        Assert.Equal("Run a shell command.", item.Summary);
    }

    [Fact]
    public void FromRequest_EmptyArguments_ShowsPlaceholder()
    {
        var req = NewRequest(arguments: "");
        var item = PendingApprovalItem.FromRequest(req);

        Assert.Equal("(no arguments)", item.ArgumentsPreview);
    }

    [Fact]
    public void FromRequest_ValidJson_PrettyPrintsWithNewlines()
    {
        var req = NewRequest(arguments: "{\"path\":\"/tmp/a.txt\",\"mode\":\"read\"}");
        var item = PendingApprovalItem.FromRequest(req);

        Assert.Contains('\n', item.ArgumentsPreview);
        Assert.Contains("\"path\"", item.ArgumentsPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void FromRequest_InvalidJson_FallsBackToRaw()
    {
        var req = NewRequest(arguments: "not-json-at-all");
        var item = PendingApprovalItem.FromRequest(req);

        Assert.Equal("not-json-at-all", item.ArgumentsPreview);
    }

    [Fact]
    public void FromRequest_VeryLongArguments_AreTruncated()
    {
        var raw = new string('x', 900);
        var req = NewRequest(arguments: raw);
        var item = PendingApprovalItem.FromRequest(req);

        Assert.EndsWith("…", item.ArgumentsPreview, StringComparison.Ordinal);
        Assert.True(item.ArgumentsPreview.Length < raw.Length);
    }

    [Fact]
    public void Origin_OmitsSender_WhenBlank()
    {
        var req = NewRequest(senderId: "");
        var item = PendingApprovalItem.FromRequest(req);

        Assert.Equal(req.ChannelId, item.Origin);
    }

    [Fact]
    public void Origin_IncludesSender_WhenPresent()
    {
        var req = NewRequest(senderId: "user@example.com");
        var item = PendingApprovalItem.FromRequest(req);

        Assert.Contains(req.ChannelId, item.Origin);
        Assert.Contains("user@example.com", item.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void MergePendingApprovals_AddsNewItems()
    {
        var viewModel = CreateViewModel();
        Assert.Empty(viewModel.PendingApprovals);

        viewModel.MergePendingApprovals([NewItem("apr_1"), NewItem("apr_2")]);

        Assert.Equal(2, viewModel.PendingApprovals.Count);
        Assert.Equal(2, viewModel.PendingApprovalsCount);
    }

    [Fact]
    public void MergePendingApprovals_RemovesMissingItems()
    {
        var viewModel = CreateViewModel();
        viewModel.MergePendingApprovals([NewItem("apr_1"), NewItem("apr_2")]);

        viewModel.MergePendingApprovals([NewItem("apr_2")]);

        Assert.Single(viewModel.PendingApprovals);
        Assert.Equal("apr_2", viewModel.PendingApprovals[0].ApprovalId);
    }

    [Fact]
    public void MergePendingApprovals_PreservesExistingItemReferences()
    {
        var viewModel = CreateViewModel();
        var original = NewItem("apr_1");
        viewModel.MergePendingApprovals([original]);

        viewModel.MergePendingApprovals([NewItem("apr_1"), NewItem("apr_2")]);

        Assert.Equal(2, viewModel.PendingApprovals.Count);
        Assert.Same(original, viewModel.PendingApprovals.Single(i => i.ApprovalId == "apr_1"));
    }

    [Fact]
    public void MergePendingApprovals_WithEmptyLatest_ClearsQueue()
    {
        var viewModel = CreateViewModel();
        viewModel.MergePendingApprovals([NewItem("apr_1"), NewItem("apr_2")]);

        viewModel.MergePendingApprovals([]);

        Assert.Empty(viewModel.PendingApprovals);
        Assert.Equal(0, viewModel.PendingApprovalsCount);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsStore(dir);
        return new MainWindowViewModel(settings, new GatewayWebSocketClient());
    }

    private static ToolApprovalRequest NewRequest(string arguments = "{}", string senderId = "sender")
        => new()
        {
            ApprovalId = Guid.NewGuid().ToString("N"),
            SessionId = "sess",
            ChannelId = "websocket",
            SenderId = senderId,
            ToolName = "shell",
            Arguments = arguments
        };

    private static PendingApprovalItem NewItem(string approvalId)
        => PendingApprovalItem.FromRequest(new ToolApprovalRequest
        {
            ApprovalId = approvalId,
            SessionId = "sess",
            ChannelId = "websocket",
            SenderId = "sender",
            ToolName = "shell",
            Arguments = "{}"
        });
}
