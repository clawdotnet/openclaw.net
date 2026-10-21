using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// The media cache had no ceiling on a single asset and no removal policy at all: every writer stored
/// whatever it was handed and nothing ever deleted anything. These cover the two halves of the fix —
/// the write-time ceiling, and the sweep that enforces age and total size.
/// </summary>
/// <remarks>
/// Assets are written by hand rather than through <c>SaveAsync</c> when the test needs a specific age:
/// <c>SaveAsync</c> stamps <c>CreatedAtUtc</c> with the wall clock, and a retention rule that can only be
/// tested by waiting a week is not a rule anyone will test.
/// </remarks>
public sealed class MediaCacheLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openclaw-media-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task SaveAsync_refuses_an_asset_larger_than_the_limit()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { MaxFileBytes = 4 });

        var exception = await Assert.ThrowsAsync<MediaAssetTooLargeException>(
            () => store.SaveAsync(new byte[5], "application/octet-stream", "big.bin", CancellationToken.None));

        Assert.Equal(5, exception.SizeBytes);
        Assert.Equal(4, exception.MaxBytes);
        // Nothing is written before the check: a refused asset must not leave a partial file behind.
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task SaveAsync_accepts_an_asset_exactly_at_the_limit()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { MaxFileBytes = 5 });

        var asset = await store.SaveAsync(new byte[5], "application/octet-stream", "exact.bin", CancellationToken.None);

        Assert.Equal(5, asset.SizeBytes);
    }

    [Fact]
    public async Task SweepAsync_removes_an_asset_that_outlived_retention()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7, MaxTotalBytes = long.MaxValue });
        var now = DateTimeOffset.UtcNow;
        var expired = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-8));
        var fresh = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-1));

        var result = await store.SweepAsync(now, dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.DeletedExpired);
        Assert.Equal(10L, result.FreedBytes);
        Assert.Null(await store.GetAsync(expired, CancellationToken.None));
        Assert.NotNull(await store.GetAsync(fresh, CancellationToken.None));
        // The sidecar goes with the asset; otherwise the next sweep would see a dangling id.
        Assert.False(File.Exists(Path.Combine(_root, expired + ".json")));
    }

    [Fact]
    public async Task SweepAsync_trims_the_oldest_assets_when_the_directory_is_over_capacity()
    {
        // RetentionDays = 0 leaves only the capacity rule in play, which is the point of this test.
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 0, MaxTotalBytes = 20 });
        var now = DateTimeOffset.UtcNow;
        var oldest = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-3));
        var middle = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-2));
        var newest = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-1));

        var result = await store.SweepAsync(now, dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.DeletedOverCapacity);
        Assert.Equal(20L, result.TotalBytesAfter);
        Assert.Null(await store.GetAsync(oldest, CancellationToken.None));
        Assert.NotNull(await store.GetAsync(middle, CancellationToken.None));
        Assert.NotNull(await store.GetAsync(newest, CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_never_evicts_an_asset_written_in_the_last_minute()
    {
        // A writer mid-save holds no lock the sweep could take, so evicting a brand-new asset while it is
        // still being written would turn a successful save into a dangling reference.
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 0, MaxTotalBytes = 0 });
        var now = DateTimeOffset.UtcNow;
        var justWritten = await WriteAssetAsync(sizeBytes: 10, createdAt: now);

        var result = await store.SweepAsync(now, dryRun: false, CancellationToken.None);

        Assert.Equal(0, result.DeletedOverCapacity);
        Assert.NotNull(await store.GetAsync(justWritten, CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_removes_an_asset_file_that_has_no_sidecar()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7 });
        var orphan = Path.Combine(_root, "media_0123456789abcdef.bin");
        await File.WriteAllBytesAsync(orphan, new byte[12]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-30));

        var result = await store.SweepAsync(DateTimeOffset.UtcNow, dryRun: false, CancellationToken.None);

        // Nothing can serve it: the sidecar is what makes an id resolvable, so this is pure leak.
        Assert.Equal(1, result.DeletedIncomplete);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task SweepAsync_removes_a_sidecar_whose_asset_is_already_gone()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7 });
        var id = await WriteAssetAsync(sizeBytes: 10, createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        File.Delete(Path.Combine(_root, id + ".bin"));

        var result = await store.SweepAsync(DateTimeOffset.UtcNow, dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.DeletedIncomplete);
        Assert.Null(await store.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_dry_run_reports_without_deleting()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7 });
        var now = DateTimeOffset.UtcNow;
        var expired = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-8));

        var result = await store.SweepAsync(now, dryRun: true, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.DeletedExpired);
        Assert.Equal(result.TotalBytesBefore, result.TotalBytesAfter);
        Assert.NotNull(await store.GetAsync(expired, CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_leaves_a_sidecar_it_cannot_parse_alone()
    {
        // A half-written sidecar is not an orphan: deleting its asset on that basis would destroy a file
        // the gateway may still be in the middle of registering.
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7, MaxTotalBytes = long.MaxValue });
        var assetPath = Path.Combine(_root, "media_fedcba9876543210.bin");
        await File.WriteAllBytesAsync(assetPath, new byte[8]);
        await File.WriteAllTextAsync(Path.Combine(_root, "media_fedcba9876543210.json"), "{ not json");

        var result = await store.SweepAsync(DateTimeOffset.UtcNow, dryRun: false, CancellationToken.None);

        Assert.Equal(0, result.DeletedIncomplete);
        Assert.True(File.Exists(assetPath));
    }

    [Fact]
    public async Task A_locked_asset_is_reported_as_failed_and_still_counts_against_the_total()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("File.Delete succeeds against an open handle on Unix, so the failure cannot be simulated.");

        // Room for one 10-byte asset. The older one cannot be deleted, so the newer one has to go instead —
        // that is what "a failed deletion still occupies space" means in practice. If the sweep subtracted
        // the locked asset from its running total anyway, it would conclude there is room and stop early,
        // leaving the directory over its ceiling while reporting success.
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7, MaxTotalBytes = 10 });
        var now = DateTimeOffset.UtcNow;
        var locked = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-8));
        _ = await WriteAssetAsync(sizeBytes: 10, createdAt: now.AddDays(-2));

        // A sharing violation is the closest thing to a real "cannot delete" on Windows: another process
        // holds the file.
        await using var hold = new FileStream(
            Path.Combine(_root, locked + ".bin"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await store.SweepAsync(now, dryRun: false, CancellationToken.None);

        Assert.Equal(1, result.FailedDeletions);
        Assert.Equal(0, result.DeletedExpired);
        Assert.Equal(1, result.DeletedOverCapacity);
        Assert.Equal(10L, result.TotalBytesAfter);
    }

    /// <summary>Writes an asset and its sidecar with an explicit age, bypassing <c>SaveAsync</c>'s clock.</summary>
    private async Task<string> WriteAssetAsync(long sizeBytes, DateTimeOffset createdAt)
    {
        var id = AttachmentReferences.MediaAssetIdPrefix + Guid.NewGuid().ToString("N")[..16];
        var assetPath = Path.Combine(_root, id + ".bin");
        await File.WriteAllBytesAsync(assetPath, new byte[sizeBytes]);

        var asset = new StoredMediaAsset
        {
            Id = id,
            MediaType = "application/octet-stream",
            FileName = id + ".bin",
            Path = assetPath,
            SizeBytes = sizeBytes,
            CreatedAtUtc = createdAt
        };

        await File.WriteAllTextAsync(
            Path.Combine(_root, id + ".json"),
            JsonSerializer.Serialize(asset, CoreJsonContext.Default.StoredMediaAsset));
        return id;
    }
}
