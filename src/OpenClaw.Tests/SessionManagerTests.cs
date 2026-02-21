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
        var store = new BarrierMemoryStore(participants: 2);
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

    private sealed class BarrierMemoryStore : IMemoryStore
    {
        private readonly Barrier _barrier;

        public BarrierMemoryStore(int participants)
        {
            _barrier = new Barrier(participants);
        }

        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
        {
            if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("Barrier timeout in test memory store.");

            return ValueTask.FromResult<Session?>(null);
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
