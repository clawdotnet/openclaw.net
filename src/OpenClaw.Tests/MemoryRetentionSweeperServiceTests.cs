using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MemoryRetentionSweeperServiceTests
{
    [Fact]
    public async Task SweepNowAsync_WhenRetentionDisabled_Throws()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Retention = new MemoryRetentionConfig
                {
                    Enabled = false
                }
            }
        };
        var store = new StubRetentionStore();
        var manager = new SessionManager(store, config);
        var service = new MemoryRetentionSweeperService(
            config,
            manager,
            store,
            new RuntimeMetrics(),
            NullLogger<MemoryRetentionSweeperService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SweepNowAsync(dryRun: true, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SweepNowAsync_UpdatesStatusAndMetrics()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Retention = new MemoryRetentionConfig
                {
                    Enabled = true,
                    ArchiveEnabled = false,
                    MaxItemsPerSweep = 1000
                }
            }
        };
        var store = new StubRetentionStore
        {
            NextResultFactory = req => Task.FromResult(new RetentionSweepResult
            {
                StartedAtUtc = req.NowUtc,
                CompletedAtUtc = req.NowUtc.AddSeconds(2),
                DeletedSessions = 2,
                DeletedBranches = 1,
                ArchivedSessions = 0,
                ArchivedBranches = 0,
                DryRun = req.DryRun
            })
        };
        var manager = new SessionManager(store, config);
        var metrics = new RuntimeMetrics();
        var service = new MemoryRetentionSweeperService(
            config,
            manager,
            store,
            metrics,
            NullLogger<MemoryRetentionSweeperService>.Instance);

        var result = await service.SweepNowAsync(dryRun: false, CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(3, result.TotalDeleted);
        Assert.True(status.LastRunSucceeded);
        Assert.Equal(1, status.TotalRuns);
        Assert.Equal(3, status.TotalDeletedItems);
        Assert.Equal(1, metrics.RetentionSweepRuns);
        Assert.Equal(3, metrics.RetentionDeletedItems);
    }

    [Fact]
    public async Task SweepNowAsync_RejectsOverlappingRuns()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Retention = new MemoryRetentionConfig
                {
                    Enabled = true,
                    ArchiveEnabled = false
                }
            }
        };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StubRetentionStore
        {
            NextResultFactory = async req =>
            {
                gate.TrySetResult();
                await release.Task;
                return new RetentionSweepResult
                {
                    StartedAtUtc = req.NowUtc,
                    CompletedAtUtc = req.NowUtc,
                    DryRun = req.DryRun
                };
            }
        };
        var manager = new SessionManager(store, config);
        var service = new MemoryRetentionSweeperService(
            config,
            manager,
            store,
            new RuntimeMetrics(),
            NullLogger<MemoryRetentionSweeperService>.Instance);

        var first = service.SweepNowAsync(dryRun: false, CancellationToken.None).AsTask();
        await gate.Task;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SweepNowAsync(dryRun: false, CancellationToken.None).AsTask());
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);

        release.TrySetResult();
        await first;
    }

    [Fact]
    public async Task SweepNowAsync_ProtectsStarredSessionsFromMetadataStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = root,
                    Retention = new MemoryRetentionConfig
                    {
                        Enabled = true,
                        ArchiveEnabled = false
                    }
                }
            };

            var metadataStore = new SessionMetadataStore(root, NullLogger<SessionMetadataStore>.Instance);
            metadataStore.Set("session-starred", new SessionMetadataUpdateRequest { Starred = true });

            var store = new StubRetentionStore();
            var manager = new SessionManager(store, config);
            var service = new MemoryRetentionSweeperService(
                config,
                manager,
                store,
                new RuntimeMetrics(),
                NullLogger<MemoryRetentionSweeperService>.Instance,
                metadataStore.GetAll);

            _ = await service.SweepNowAsync(dryRun: true, CancellationToken.None);

            Assert.Contains("session-starred", store.LastProtectedSessionIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubRetentionStore : IMemoryStore, IMemoryRetentionStore
    {
        public Func<RetentionSweepRequest, Task<RetentionSweepResult>>? NextResultFactory { get; set; }
        public IReadOnlySet<string> LastProtectedSessionIds { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

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

        public async ValueTask<RetentionSweepResult> SweepAsync(
            RetentionSweepRequest request,
            IReadOnlySet<string> protectedSessionIds,
            CancellationToken ct)
        {
            LastProtectedSessionIds = new HashSet<string>(protectedSessionIds, StringComparer.Ordinal);
            if (NextResultFactory is null)
            {
                return new RetentionSweepResult
                {
                    StartedAtUtc = request.NowUtc,
                    CompletedAtUtc = request.NowUtc,
                    DryRun = request.DryRun
                };
            }

            return await NextResultFactory(request);
        }

        public ValueTask<RetentionStoreStats> GetRetentionStatsAsync(CancellationToken ct)
        {
            return ValueTask.FromResult(new RetentionStoreStats
            {
                Backend = "stub",
                PersistedSessions = 0,
                PersistedBranches = 0
            });
        }
    }
}
