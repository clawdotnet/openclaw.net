using Microsoft.Data.Sqlite;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SqliteMemoryStoreRetentionTests
{
    [Fact]
    public async Task SweepAsync_ArchivesAndDeletesExpiredItems()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");
        var archive = Path.Combine(root, "archive");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);

        using var store = new SqliteMemoryStore(dbPath, enableFts: false);

        await store.SaveSessionAsync(new Session
        {
            Id = "session-expired",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-45)
        }, TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(new Session
        {
            Id = "session-fresh",
            ChannelId = "websocket",
            SenderId = "bob",
            LastActiveAt = now.AddDays(-1)
        }, TestContext.Current.CancellationToken);
        await store.SaveBranchAsync(new SessionBranch
        {
            BranchId = "branch-expired",
            SessionId = "session-expired",
            Name = "old",
            CreatedAt = now.AddDays(-20),
            History = []
        }, TestContext.Current.CancellationToken);
        await store.SaveBranchAsync(new SessionBranch
        {
            BranchId = "branch-fresh",
            SessionId = "session-fresh",
            Name = "new",
            CreatedAt = now.AddDays(-1),
            History = []
        }, TestContext.Current.CancellationToken);

        var staleEpoch = now.AddDays(-45).ToUnixTimeSeconds();
        await SetUpdatedAtAsync(dbPath, "sessions", "id", "session-expired", staleEpoch);
        await SetUpdatedAtAsync(dbPath, "branches", "branch_id", "branch-expired", staleEpoch);

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = true,
                ArchivePath = archive,
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeletedSessions);
        Assert.Equal(1, result.DeletedBranches);
        Assert.Equal(1, result.ArchivedSessions);
        Assert.Equal(1, result.ArchivedBranches);
        Assert.Null(await store.GetSessionAsync("session-expired", TestContext.Current.CancellationToken));
        Assert.NotNull(await store.GetSessionAsync("session-fresh", TestContext.Current.CancellationToken));
        Assert.Null(await store.LoadBranchAsync("branch-expired", TestContext.Current.CancellationToken));
        Assert.NotNull(await store.LoadBranchAsync("branch-fresh", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SweepAsync_RespectsMaxItemsAndSkipsProtectedSession()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);

        using var store = new SqliteMemoryStore(dbPath, enableFts: false);

        foreach (var id in new[] { "s1", "s2", "s3" })
        {
            await store.SaveSessionAsync(new Session
            {
                Id = id,
                ChannelId = "websocket",
                SenderId = id,
                LastActiveAt = now.AddDays(-90)
            }, TestContext.Current.CancellationToken);
            await SetUpdatedAtAsync(dbPath, "sessions", "id", id, now.AddDays(-90).ToUnixTimeSeconds());
        }

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = false,
                ArchivePath = Path.Combine(root, "archive"),
                ArchiveRetentionDays = 30,
                MaxItems = 2
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal) { "s1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SkippedProtectedSessions);
        Assert.Equal(2, result.DeletedSessions);
        Assert.True(result.MaxItemsLimitReached);
        Assert.NotNull(await store.GetSessionAsync("s1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SweepAsync_ArchiveFailurePreventsDelete()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);
        var invalidArchiveRoot = Path.Combine(root, "archive-blocker");
        await File.WriteAllTextAsync(invalidArchiveRoot, "not-a-directory");

        using var store = new SqliteMemoryStore(dbPath, enableFts: false);
        await store.SaveSessionAsync(new Session
        {
            Id = "session-expired",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-90)
        }, TestContext.Current.CancellationToken);
        await SetUpdatedAtAsync(dbPath, "sessions", "id", "session-expired", now.AddDays(-90).ToUnixTimeSeconds());

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = true,
                ArchivePath = invalidArchiveRoot,
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DeletedSessions);
        Assert.NotEmpty(result.Errors);
        Assert.NotNull(await store.GetSessionAsync("session-expired", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SweepAsync_RemovesDeletedSessionsFromFtsSearchResults()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);

        using var store = new SqliteMemoryStore(dbPath, enableFts: true);
        await store.SaveSessionAsync(new Session
        {
            Id = "session-expired",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-90),
            History =
            [
                new ChatTurn
                {
                    Role = "user",
                    Content = "legacy needle"
                }
            ]
        }, TestContext.Current.CancellationToken);
        await SetUpdatedAtAsync(dbPath, "sessions", "id", "session-expired", now.AddDays(-90).ToUnixTimeSeconds());

        var beforeSweep = await store.SearchSessionsAsync(
            new SessionSearchQuery { Text = "legacy" },
            TestContext.Current.CancellationToken);
        Assert.Single(beforeSweep.Items);

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = false,
                ArchivePath = Path.Combine(root, "archive"),
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeletedSessions);

        var afterSweep = await store.SearchSessionsAsync(
            new SessionSearchQuery { Text = "legacy" },
            TestContext.Current.CancellationToken);
        Assert.Empty(afterSweep.Items);
    }

    [Fact]
    public async Task SweepAsync_ExpiresBranchesUsingCreatedAtInsteadOfUpdatedAt()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);

        using var store = new SqliteMemoryStore(dbPath, enableFts: false);
        await store.SaveBranchAsync(new SessionBranch
        {
            BranchId = "branch-expired",
            SessionId = "session-expired",
            Name = "old",
            CreatedAt = now.AddDays(-30),
            History = []
        }, TestContext.Current.CancellationToken);
        await SetUpdatedAtAsync(dbPath, "branches", "branch_id", "branch-expired", now.ToUnixTimeSeconds());

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = false,
                ArchivePath = Path.Combine(root, "archive"),
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeletedBranches);
        Assert.Null(await store.LoadBranchAsync("branch-expired", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initialize_CreatesRetentionIndexes()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "memory.db");

        using var _ = new SqliteMemoryStore(dbPath, enableFts: false);

        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('idx_sessions_updated_at','idx_branches_updated_at') ORDER BY name;";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        Assert.Contains("idx_sessions_updated_at", names);
        Assert.Contains("idx_branches_updated_at", names);
    }

    private static async Task SetUpdatedAtAsync(string dbPath, string table, string idColumn, string id, long updatedAt)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET updated_at = $updated_at WHERE {idColumn} = $id;";
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
