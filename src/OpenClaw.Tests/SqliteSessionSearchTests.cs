using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SqliteSessionSearchTests
{
    [Fact]
    public async Task SearchSessionsAsync_WithFts_AppliesChannelFilter()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "memory.db");
            using var store = new SqliteMemoryStore(dbPath, enableFts: true);

            await store.SaveSessionAsync(new Session
            {
                Id = "session-websocket",
                ChannelId = "websocket",
                SenderId = "alice",
                History =
                [
                    new ChatTurn
                    {
                        Role = "user",
                        Content = "invoice status"
                    }
                ]
            }, TestContext.Current.CancellationToken);

            await store.SaveSessionAsync(new Session
            {
                Id = "session-sms",
                ChannelId = "sms",
                SenderId = "bob",
                History =
                [
                    new ChatTurn
                    {
                        Role = "user",
                        Content = "invoice status"
                    }
                ]
            }, TestContext.Current.CancellationToken);

            var results = await ((ISessionSearchStore)store).SearchSessionsAsync(
                new SessionSearchQuery
                {
                    Text = "invoice",
                    ChannelId = "sms"
                },
                TestContext.Current.CancellationToken);

            var hit = Assert.Single(results.Items);
            Assert.Equal("session-sms", hit.SessionId);
            Assert.Equal("sms", hit.ChannelId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchSessionsAsync_WithFts_SearchesToolArgumentsWhenResultIsEmpty()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "memory.db");
            using var store = new SqliteMemoryStore(dbPath, enableFts: true);

            await store.SaveSessionAsync(new Session
            {
                Id = "session-tool",
                ChannelId = "websocket",
                SenderId = "alice",
                History =
                [
                    new ChatTurn
                    {
                        Role = "assistant",
                        Content = "[tool_use]",
                        ToolCalls =
                        [
                            new ToolInvocation
                            {
                                ToolName = "calendar",
                                Arguments = """{"query":"invoice review tomorrow"}""",
                                Result = "",
                                Duration = TimeSpan.FromMilliseconds(5)
                            }
                        ]
                    }
                ]
            }, TestContext.Current.CancellationToken);

            var results = await ((ISessionSearchStore)store).SearchSessionsAsync(
                new SessionSearchQuery
                {
                    Text = "invoice"
                },
                TestContext.Current.CancellationToken);

            var hit = Assert.Single(results.Items);
            Assert.Equal("session-tool", hit.SessionId);
            Assert.Equal("tool", hit.Role);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchSessionsAsync_WithFts_MalformedMatchQuery_ReturnsEmpty()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "memory.db");
            using var store = new SqliteMemoryStore(dbPath, enableFts: true);

            await store.SaveSessionAsync(new Session
            {
                Id = "session-a",
                ChannelId = "websocket",
                SenderId = "alice",
                History =
                [
                    new ChatTurn
                    {
                        Role = "user",
                        Content = "hello world"
                    }
                ]
            }, TestContext.Current.CancellationToken);

            var results = await ((ISessionSearchStore)store).SearchSessionsAsync(
                new SessionSearchQuery
                {
                    // Unclosed phrase quote is invalid FTS5 syntax.
                    Text = "\"unclosed"
                },
                TestContext.Current.CancellationToken);

            Assert.Empty(results.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSessionAsync_CorruptRow_ThrowsCorruptionException()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "memory.db");
            using (var store = new SqliteMemoryStore(dbPath, enableFts: false))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "session-corrupt",
                    ChannelId = "websocket",
                    SenderId = "alice"
                }, TestContext.Current.CancellationToken);

                await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE sessions SET json = '{not valid json' WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$id", "session-corrupt");
                    await cmd.ExecuteNonQueryAsync();
                }

                var ex = await Assert.ThrowsAsync<MemoryStoreCorruptionException>(async () =>
                    await store.GetSessionAsync("session-corrupt", TestContext.Current.CancellationToken));
                Assert.Equal("session-corrupt", ex.SessionId);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
