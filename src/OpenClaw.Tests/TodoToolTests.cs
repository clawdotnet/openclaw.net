using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TodoToolTests
{
    [Fact]
    public async Task ExecuteAsync_AddAndComplete_PersistsTodoState()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var tool = new TodoTool(metadataStore);
        var context = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_todo",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_todo",
                ChannelId = "websocket"
            }
        };

        var addResult = await tool.ExecuteAsync("""{"action":"add","text":"Review deployment notes"}""", context, TestContext.Current.CancellationToken);
        var todoId = addResult.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("Review deployment notes", addResult, StringComparison.Ordinal);

        var completeResult = await tool.ExecuteAsync($$"""{"action":"complete","id":"{{todoId}}"}""", context, TestContext.Current.CancellationToken);
        Assert.Contains("[done]", completeResult, StringComparison.Ordinal);

        var metadata = metadataStore.Get("sess_todo");
        var todo = Assert.Single(metadata.TodoItems);
        Assert.Equal(todoId, todo.Id);
        Assert.True(todo.Completed);
    }
}
