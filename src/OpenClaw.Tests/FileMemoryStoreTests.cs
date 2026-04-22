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
                        Duration = TimeSpan.FromMilliseconds(12),
                        ResultStatus = ToolResultStatuses.Blocked,
                        FailureCode = ToolFailureCodes.ApprovalRequired,
                        FailureMessage = "Approval required.",
                        NextStep = "Approve the request and retry."
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
            Assert.Equal(ToolResultStatuses.Blocked, toolCall.ResultStatus);
            Assert.Equal(ToolFailureCodes.ApprovalRequired, toolCall.FailureCode);
            Assert.Equal("Approval required.", toolCall.FailureMessage);
            Assert.Equal("Approve the request and retry.", toolCall.NextStep);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetSessionAsync_ConcurrentLoads_ReturnCanonicalCachedInstance()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var writerStore = new FileMemoryStore(storagePath, 4);
            var session = new Session
            {
                Id = "canonical-session",
                ChannelId = "test",
                SenderId = "user"
            };

            for (var i = 0; i < 256; i++)
            {
                session.History.Add(new ChatTurn
                {
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Content = new string((char)('a' + (i % 26)), 512)
                });
            }

            await writerStore.SaveSessionAsync(session, CancellationToken.None);

            var readerStore = new FileMemoryStore(storagePath, 4);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 16)
                .Select(async _ =>
                {
                    await gate.Task;
                    return await readerStore.GetSessionAsync(session.Id, CancellationToken.None);
                })
                .ToArray();

            gate.SetResult(true);
            var loadedSessions = await Task.WhenAll(tasks);

            var canonical = Assert.IsType<Session>(loadedSessions[0]);
            Assert.All(loadedSessions, item => Assert.Same(canonical, Assert.IsType<Session>(item)));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetSessionAsync_CorruptFileThrowsAndQuarantines()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var sessionId = "corrupt-session";
            var sessionFile = Path.Combine(storagePath, "sessions", "Y29ycnVwdC1zZXNzaW9u.json");
            Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
            await File.WriteAllTextAsync(sessionFile, "{ this is not valid json", CancellationToken.None);

            var store = new FileMemoryStore(storagePath, 4);
            var ex = await Assert.ThrowsAsync<MemoryStoreCorruptionException>(async () =>
                await store.GetSessionAsync(sessionId, CancellationToken.None));

            Assert.Equal(sessionId, ex.SessionId);
            Assert.Contains(".corrupt-", ex.FilePath, StringComparison.Ordinal);
            Assert.DoesNotContain(sessionFile, Directory.GetFiles(Path.Combine(storagePath, "sessions")), StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task ListNotesWithPrefixAsync_LongKeys_ReturnsOriginalKey()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = new FileMemoryStore(storagePath, 4);
            var longKey = "project:myapp:" + new string('k', 240);

            await store.SaveNoteAsync(longKey, "remember this", CancellationToken.None);

            var keys = await store.ListNotesWithPrefixAsync("project:myapp:", CancellationToken.None);

            Assert.Contains(longKey, keys);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task SearchNotesAsync_LongKeys_RespectPrefixFilter()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = new FileMemoryStore(storagePath, 4);
            var longKey = "project:myapp:" + new string('p', 240);

            await store.SaveNoteAsync(longKey, "architecture conventions", CancellationToken.None);

            var results = await store.SearchNotesAsync("conventions", "project:myapp:", 5, CancellationToken.None);

            var hit = Assert.Single(results);
            Assert.Equal(longKey, hit.Key);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task SearchNotesAsync_PrefersHigherScoringAndMoreRecentNotes()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-file-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = new FileMemoryStore(storagePath, 4);
            await store.SaveNoteAsync("project:demo:legacy", "architecture notes about migration", CancellationToken.None);
            await Task.Delay(20);
            await store.SaveNoteAsync("project:demo:architecture", "architecture migration checklist", CancellationToken.None);

            var hits = await store.SearchNotesAsync("architecture migration", "project:demo:", 2, CancellationToken.None);

            Assert.Equal(2, hits.Count);
            Assert.Equal("project:demo:architecture", hits[0].Key);
            Assert.True(hits[0].Score >= hits[1].Score);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }
}
