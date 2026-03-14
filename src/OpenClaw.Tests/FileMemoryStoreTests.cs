using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FileMemoryStoreTests
{
    [Fact]
    public async Task GetSessionAsync_RoundTripsToolCallHistory()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var writerStore = new FileMemoryStore(storagePath, 4);
            var session = new Session
            {
                Id = "tool-history-session",
                ChannelId = "test",
                SenderId = "user"
            };
            session.History.Add(new ChatTurn
            {
                Role = "user",
                Content = "save a note"
            });
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls =
                [
                    new ToolInvocation
                    {
                        ToolName = "memory",
                        Arguments = """{"action":"write","key":"note","content":"hello"}""",
                        Result = "Saved note: note",
                        Duration = TimeSpan.FromMilliseconds(12)
                    }
                ]
            });
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "Saved note: note"
            });

            await writerStore.SaveSessionAsync(session, CancellationToken.None);

            var readerStore = new FileMemoryStore(storagePath, 4);
            var loaded = await readerStore.GetSessionAsync(session.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.History.Count);
            var toolCall = Assert.Single(loaded!.History[1].ToolCalls!);
            Assert.Equal("memory", toolCall.ToolName);
            Assert.Equal("Saved note: note", toolCall.Result);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }
}
