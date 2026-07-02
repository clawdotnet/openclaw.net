using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BackgroundSessionStoreTests : IAsyncDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

    [Fact]
    public async Task FileStore_ListsOnlyRunnableBackgroundSessions()
    {
        var dir = NewTempDir("bg-file-basic");
        await using var store = new FileMemoryStore(dir);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    [Fact]
    public async Task SqliteStore_ListsOnlyRunnableBackgroundSessions()
    {
        var db = NewTempFile("bg-sqlite-basic", ".db");
        using var store = new SqliteMemoryStore(db, enableFts: false);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    [Fact]
    public async Task FileStore_ReturnsRecoveryOrder_WhenMoreThanLimit()
    {
        var dir = NewTempDir("bg-file-recovery-order");
        await using var store = new FileMemoryStore(dir);
        var ct = TestContext.Current.CancellationToken;

        // Create 5 runnable sessions with different LastContinuedAtUtc timestamps.
        for (var i = 0; i < 5; i++)
        {
            var session = NewSession($"websocket:bg{i}", SessionRunState.Continuing);
            session.BackgroundRun!.LastContinuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-(5 - i));
            await store.SaveSessionAsync(session, ct);
        }

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(2, ct);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("websocket:bg0", sessions[0].Id);
        Assert.Equal("websocket:bg1", sessions[1].Id);
    }

    [Fact]
    public async Task FileStore_ToleratesCorruptFile_ReturnsValidSessions()
    {
        var dir = NewTempDir("bg-file-corrupt");
        var corruptPath = Path.Join(dir, "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{ this is not valid json !! }", TestContext.Current.CancellationToken);

        await using var store = new FileMemoryStore(dir);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        // Should still find the valid runnable session despite the corrupt file
        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var dir in _tempDirs)
            TryDeleteDirectory(dir);

        foreach (var file in _tempFiles)
        {
            TryDeleteFile(file);
            TryDeleteFile(file + "-wal");
            TryDeleteFile(file + "-shm");
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;

        static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }

        static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task SqliteStore_ReturnsRecoveryOrder_WhenMoreThanLimit()
    {
        var db = NewTempFile("bg-sqlite-order", ".db");
        using var store = new SqliteMemoryStore(db, enableFts: false);
        var ct = TestContext.Current.CancellationToken;

        // Save newer recovery candidates first, then older candidates last. This
        // guards against limiting by updated_at before sorting by recovery time.
        for (var i = 0; i < 10; i++)
        {
            var session = NewSession($"websocket:bg{i}", SessionRunState.Continuing);
            session.BackgroundRun!.LastContinuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-i);
            await store.SaveSessionAsync(session, ct);
        }

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(2, ct);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("websocket:bg9", sessions[0].Id);
        Assert.Equal("websocket:bg8", sessions[1].Id);
    }

    [Fact]
    public async Task SqliteStore_ToleratesCorruptRow_ReturnsValidSessions()
    {
        var db = NewTempFile("bg-sqlite-corrupt", ".db");
        var ct = TestContext.Current.CancellationToken;

        // Seed valid sessions first
        using (var store = new SqliteMemoryStore(db, enableFts: false))
        {
            await SeedSessionsAsync(store, ct);
        }

        // Inject a corrupt JSON row directly into the sessions table
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, json, updated_at) VALUES ($id, $json, $updated_at);";
            cmd.Parameters.AddWithValue("$id", "corrupt-session");
            cmd.Parameters.AddWithValue("$json", "{ this is not valid json !! }");
            cmd.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var store2 = new SqliteMemoryStore(db, enableFts: false);
        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store2);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, ct);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
    }

    private string NewTempDir(string suffix)
    {
        var dir = Path.Join(Path.GetTempPath(), $"openclaw-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string NewTempFile(string suffix, string extension)
    {
        var file = Path.Join(Path.GetTempPath(), $"openclaw-{suffix}-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(file);
        return file;
    }

    private static async Task SeedSessionsAsync(IMemoryStore store, CancellationToken ct)
    {
        await store.SaveSessionAsync(NewSession("websocket:runnable", SessionRunState.Continuing), ct);
        await store.SaveSessionAsync(NewSession("websocket:paused", SessionRunState.Paused), ct);
        await store.SaveSessionAsync(NewSession("websocket:done", SessionRunState.Completed), ct);
        await store.SaveSessionAsync(NewSession("websocket:idle", SessionRunState.Idle), ct);
    }

    private static Session NewSession(string id, SessionRunState state)
        => new()
        {
            Id = id,
            ChannelId = "websocket",
            SenderId = id.Split(':')[1],
            RunState = state,
            BackgroundRun = state is SessionRunState.Running or SessionRunState.Continuing
                ? new BackgroundRunMetadata
                {
                    RunId = "run_" + id.Split(':')[1],
                    Objective = "Fix tests",
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    LastContinuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    TokenBudget = 128_000,
                    MaxContinuationTurns = 200
                }
                : null
        };
}
