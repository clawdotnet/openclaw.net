using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BackgroundSessionStoreTests
{
    [Fact]
    public async Task FileStore_ListsOnlyRunnableBackgroundSessions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-bg-file-" + Guid.NewGuid().ToString("N"));
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
        var db = Path.Combine(Path.GetTempPath(), "openclaw-bg-" + Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteMemoryStore(db, enableFts: false);
        await SeedSessionsAsync(store, TestContext.Current.CancellationToken);

        var backgroundStore = Assert.IsAssignableFrom<IBackgroundSessionStore>(store);
        var sessions = await backgroundStore.ListBackgroundRunnableSessionsAsync(10, TestContext.Current.CancellationToken);

        Assert.Single(sessions);
        Assert.Equal("websocket:runnable", sessions[0].Id);
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
