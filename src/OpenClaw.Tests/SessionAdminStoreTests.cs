using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SessionAdminStoreTests
{
    [Fact]
    public async Task FileMemoryStore_ListSessionsAsync_ReturnsPagedResults()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new FileMemoryStore(root);
            await SeedSessionsAsync(store);

            var page = await ((ISessionAdminStore)store).ListSessionsAsync(page: 1, pageSize: 1, new SessionListQuery(), CancellationToken.None);

            Assert.Single(page.Items);
            Assert.True(page.HasMore);
            Assert.Equal("session-2", page.Items[0].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteMemoryStore_ListSessionsAsync_ReturnsPagedResults()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "openclaw.db");
            using (var store = new SqliteMemoryStore(dbPath, enableFts: false))
            {
                await SeedSessionsAsync(store);

                var page = await ((ISessionAdminStore)store).ListSessionsAsync(page: 1, pageSize: 1, new SessionListQuery(), CancellationToken.None);

                Assert.Single(page.Items);
                Assert.True(page.HasMore);
                Assert.Equal("session-2", page.Items[0].Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileMemoryStore_ListSessionsAsync_FiltersByChannel()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new FileMemoryStore(root);
            await SeedSessionsAsync(store);

            var page = await ((ISessionAdminStore)store).ListSessionsAsync(
                page: 1,
                pageSize: 10,
                new SessionListQuery { ChannelId = "sms" },
                CancellationToken.None);

            Assert.Single(page.Items);
            Assert.Equal("session-3", page.Items[0].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteMemoryStore_ListSessionsAsync_FiltersBySearchAcrossPages()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "openclaw.db");
            using (var store = new SqliteMemoryStore(dbPath, enableFts: false))
            {
                await SeedSessionsAsync(store);

                var page = await ((ISessionAdminStore)store).ListSessionsAsync(
                    page: 1,
                    pageSize: 1,
                    new SessionListQuery { Search = "sms" },
                    CancellationToken.None);

                Assert.Single(page.Items);
                Assert.False(page.HasMore);
                Assert.Equal("session-3", page.Items[0].Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteMemoryStore_ListSessionsAsync_FiltersByState()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, "openclaw.db");
            using (var store = new SqliteMemoryStore(dbPath, enableFts: false))
            {
                await SeedSessionsAsync(store);

                var page = await ((ISessionAdminStore)store).ListSessionsAsync(
                    page: 1,
                    pageSize: 10,
                    new SessionListQuery { State = SessionState.Paused },
                    CancellationToken.None);

                Assert.Single(page.Items);
                Assert.Equal("session-3", page.Items[0].Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedSessionsAsync(IMemoryStore store)
    {
        await store.SaveSessionAsync(new Session
        {
            Id = "session-1",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        }, CancellationToken.None);

        await store.SaveSessionAsync(new Session
        {
            Id = "session-2",
            ChannelId = "websocket",
            SenderId = "bob",
            LastActiveAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await store.SaveSessionAsync(new Session
        {
            Id = "session-3",
            ChannelId = "sms",
            SenderId = "carol",
            LastActiveAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            State = SessionState.Paused
        }, CancellationToken.None);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
