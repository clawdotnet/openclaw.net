using OpenClaw.Gateway;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MetaInvocationStoreTests
{
    [Fact]
    public async Task BeginOrGetAsync_WhenKeyAndRequestMatch_ShouldReturnExistingRecord()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var started = await store.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);
            var existing = await store.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationClaimKind.Started, started.Kind);
            Assert.Equal(MetaInvocationClaimKind.Existing, existing.Kind);
            Assert.Equal(started.Record.InvocationId, existing.Record.InvocationId);
            Assert.Equal(MetaInvocationStatus.Running, existing.Record.Status);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task BeginOrGetAsync_WhenKeyMatchesButRequestDiffers_ShouldReturnConflict()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            await store.BeginOrGetAsync("caller", "key", "first-hash", TestContext.Current.CancellationToken);

            var conflict = await store.BeginOrGetAsync("caller", "key", "second-hash", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationClaimKind.Conflict, conflict.Kind);
            Assert.Equal("first-hash", conflict.Record.RequestHash);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task BeginOrGetAsync_WhenCalledConcurrently_ShouldCreateOnlyOneInvocation()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
                store.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken)));

            Assert.Single(claims, claim => claim.Kind == MetaInvocationClaimKind.Started);
            Assert.Single(claims.Select(claim => claim.Record.InvocationId).Distinct());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MarkInterruptedInvocationsUncertainAsync_WhenStoreIsReopened_ShouldNotMakeRunningInvocationRunnable()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var originalStore = new MetaInvocationStore(storagePath, retentionDays: 30);
            var started = await originalStore.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);
            originalStore.Dispose();

            using var reopenedStore = new MetaInvocationStore(storagePath, retentionDays: 30);
            await reopenedStore.MarkInterruptedInvocationsUncertainAsync(TestContext.Current.CancellationToken);
            var existing = await reopenedStore.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationClaimKind.Existing, existing.Kind);
            Assert.Equal(started.Record.InvocationId, existing.Record.InvocationId);
            Assert.Equal(MetaInvocationStatus.Uncertain, existing.Record.Status);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MarkInterruptedInvocationsUncertainAsync_WhenAnotherProcessOwnsLease_ShouldKeepInvocationRunning()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var activeStore = new MetaInvocationStore(storagePath, retentionDays: 30);
            var started = await activeStore.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);
            using var startupStore = new MetaInvocationStore(storagePath, retentionDays: 30);

            await startupStore.MarkInterruptedInvocationsUncertainAsync(TestContext.Current.CancellationToken);
            var existing = await startupStore.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationClaimKind.Existing, existing.Kind);
            Assert.Equal(started.Record.InvocationId, existing.Record.InvocationId);
            Assert.Equal(MetaInvocationStatus.Running, existing.Record.Status);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteAsync_WhenStoreIsReopened_ShouldReplayPersistedResult()
    {
        var storagePath = CreateStoragePath();

        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var started = await store.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);
            await store.CompleteAsync("caller", "key", "persisted result", TestContext.Current.CancellationToken);

            using var reopenedStore = new MetaInvocationStore(storagePath, retentionDays: 30);
            var existing = await reopenedStore.BeginOrGetAsync("caller", "key", "hash", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationClaimKind.Existing, existing.Kind);
            Assert.Equal(started.Record.InvocationId, existing.Record.InvocationId);
            Assert.Equal(MetaInvocationStatus.Completed, existing.Record.Status);
            Assert.Equal("persisted result", existing.Record.Result);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static string CreateStoragePath()
    {
        var path = Path.Join(Path.GetTempPath(), "openclaw-meta-invocation-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}