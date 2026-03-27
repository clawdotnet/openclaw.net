using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SessionManagerTests
{
    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCalls_ReturnCanonicalSessionInstance()
    {
        var store = new DelayedMemoryStore();
        var manager = new SessionManager(store, new GatewayConfig
        {
            MaxConcurrentSessions = 8,
            SessionTimeoutMinutes = 30
        });

        var t1 = Task.Run(() => manager.GetOrCreateAsync("websocket", "alice", CancellationToken.None).AsTask());
        var t2 = Task.Run(() => manager.GetOrCreateAsync("websocket", "alice", CancellationToken.None).AsTask());

        var sessions = await Task.WhenAll(t1, t2);
        Assert.Same(sessions[0], sessions[1]);

        var next = await manager.GetOrCreateAsync("websocket", "alice", CancellationToken.None);
        Assert.Same(sessions[0], next);
    }

    [Fact]
    public async Task IsActive_ReturnsTrueForActiveSessions()
    {
        var store = new InMemoryStore();
        var manager = new SessionManager(store, new GatewayConfig
        {
            MaxConcurrentSessions = 8,
            SessionTimeoutMinutes = 30
        });

        await manager.GetOrCreateAsync("websocket", "bob", CancellationToken.None);
        Assert.True(manager.IsActive("websocket:bob"));
        Assert.False(manager.IsActive("websocket:nobody"));
    }

    [Fact]
    public async Task GetOrCreateByIdAsync_UsesExplicitSessionId()
    {
        var store = new InMemoryStore();
        var manager = new SessionManager(store, new GatewayConfig
        {
            MaxConcurrentSessions = 8,
            SessionTimeoutMinutes = 30
        });

        var s1 = await manager.GetOrCreateByIdAsync("cron:daily-news", "cron", "system", CancellationToken.None);
        var s2 = await manager.GetOrCreateByIdAsync("cron:daily-news", "cron", "system", CancellationToken.None);

        Assert.Same(s1, s2);
        Assert.True(manager.IsActive("cron:daily-news"));
        Assert.Equal("cron:daily-news", s1.Id);
    }

    [Fact]
    public async Task SweepExpiredActiveSessions_EvictsExpiredWithoutCapacityPressure()
    {
        var store = new InMemoryStore();
        var manager = new SessionManager(store, new GatewayConfig
        {
            MaxConcurrentSessions = 100,
            SessionTimeoutMinutes = 1
        });

        var session = await manager.GetOrCreateAsync("websocket", "charlie", CancellationToken.None);
        session.LastActiveAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var evicted = manager.SweepExpiredActiveSessions();

        Assert.Equal(1, evicted);
        Assert.False(manager.IsActive("websocket:charlie"));
    }

    [Fact]
    public async Task GetOrCreateByIdAsync_ConcurrentDistinctSessions_RespectsStrictCapacityCap()
    {
        var store = new InMemoryStore();
        var manager = new SessionManager(store, new GatewayConfig
        {
            MaxConcurrentSessions = 2,
            SessionTimeoutMinutes = 30
        });

        var tasks = new[]
        {
            CaptureAsync("session-a"),
            CaptureAsync("session-b"),
            CaptureAsync("session-c")
        };

        var results = await Task.WhenAll(tasks);

        Assert.Equal(3, results.Count(static result => result.Session is not null));
        Assert.Equal(0, results.Count(static result => result.Error is not null));
        Assert.True(manager.ActiveCount <= 2);

        async Task<(Session? Session, Exception? Error)> CaptureAsync(string sessionId)
        {
            try
            {
                var session = await manager.GetOrCreateByIdAsync(sessionId, "websocket", sessionId, CancellationToken.None);
                return (session, null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }
    }

    private sealed class DelayedMemoryStore : IMemoryStore
    {
        public async ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            return null;
        }

        public ValueTask SaveSessionAsync(Session session, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct) => ValueTask.FromResult<string?>(null);
        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DeleteNoteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct) => ValueTask.FromResult<SessionBranch?>(null);
        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<SessionBranch>>([]);
        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class InMemoryStore : IMemoryStore
    {
        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<Session?>(null);
        public ValueTask SaveSessionAsync(Session session, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct) => ValueTask.FromResult<string?>(null);
        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DeleteNoteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct) => ValueTask.FromResult<SessionBranch?>(null);
        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<SessionBranch>>([]);
        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
