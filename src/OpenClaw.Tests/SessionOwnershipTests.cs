using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SessionOwnershipTests : IDisposable
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "openclaw-session-ownership-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_storagePath, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task GetOrCreateById_WhenCreating_ShouldRecordOwner()
    {
        var manager = CreateManager();

        var session = await manager.GetOrCreateByIdAsync("owned", "api", "sender", CancellationToken.None, ownerAccountId: "acct-a");

        Assert.Equal("acct-a", session.OwnerAccountId);
    }

    [Fact]
    public async Task GetOrCreateById_WhenSessionExists_ShouldNotReassignOwner()
    {
        var manager = CreateManager();
        await manager.GetOrCreateByIdAsync("owned", "api", "sender", CancellationToken.None, ownerAccountId: "acct-a");

        var again = await manager.GetOrCreateByIdAsync("owned", "api", "sender", CancellationToken.None, ownerAccountId: "acct-b");

        Assert.Equal("acct-a", again.OwnerAccountId);
    }

    [Theory]
    [InlineData(null, "acct-b", false, true)]      // unowned sessions stay open; writing never claims them
    [InlineData("acct-a", "acct-a", false, true)]  // owner
    [InlineData("acct-a", "acct-b", false, false)] // another account
    [InlineData("acct-a", "acct-b", true, true)]   // admin
    [InlineData("acct-a", null, false, true)]      // no account: bootstrap, open loopback, channel and system turns
    public void CanWrite_ShouldAllowOwnerOrAdminOnOwnedSessions(string? owner, string? accountId, bool isAdmin, bool expected)
    {
        var session = new Session { Id = "s", ChannelId = "api", SenderId = "sender", OwnerAccountId = owner };

        Assert.Equal(expected, SessionAccess.CanWrite(session, accountId, isAdmin));
    }

    [Fact]
    public async Task FileStore_ListSessions_WhenOwnerFilter_ShouldReturnOnlyOwned()
    {
        var store = new FileMemoryStore(_storagePath, 4);
        await SeedAsync(store);

        var page = await store.ListSessionsAsync(1, 50, new SessionListQuery { OwnerAccountId = "acct-a" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("mine", item.Id);
        Assert.Equal("acct-a", item.OwnerAccountId);
    }

    [Fact]
    public async Task SqliteStore_ListSessions_WhenOwnerFilter_ShouldReturnOnlyOwned()
    {
        Directory.CreateDirectory(_storagePath);
        using var store = new SqliteMemoryStore(Path.Combine(_storagePath, "memory.db"), enableFts: false);
        await SeedAsync(store);

        var page = await store.ListSessionsAsync(1, 50, new SessionListQuery { OwnerAccountId = "acct-a" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("mine", item.Id);
        Assert.Equal("acct-a", item.OwnerAccountId);
    }

    private SessionManager CreateManager()
        => new(new FileMemoryStore(_storagePath, 4), new GatewayConfig { Memory = new MemoryConfig { StoragePath = _storagePath } }, NullLogger.Instance);

    private static async Task SeedAsync(IMemoryStore store)
    {
        await store.SaveSessionAsync(new Session { Id = "mine", ChannelId = "api", SenderId = "a", OwnerAccountId = "acct-a" }, CancellationToken.None);
        await store.SaveSessionAsync(new Session { Id = "theirs", ChannelId = "api", SenderId = "b", OwnerAccountId = "acct-b" }, CancellationToken.None);
        await store.SaveSessionAsync(new Session { Id = "unowned", ChannelId = "telegram", SenderId = "c" }, CancellationToken.None);
    }
}
