using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// The sweeper's contract with the rest of the gateway: it must stay out of the way when retention is
/// off, and it must be able to run on demand for an operator without the background loop being involved.
/// </summary>
/// <remarks>
/// The loop itself is not driven here — that needs a controllable <c>TimeProvider</c> and this project has
/// no fake clock — but everything the loop calls is: the disabled check it returns on, and the sweep it
/// delegates to.
/// </remarks>
public sealed class MediaCacheSweeperServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openclaw-media-sweeper-{Guid.NewGuid():N}");

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
    public async Task With_retention_disabled_it_reports_so_and_stops_immediately()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 0 });
        var sweeper = new MediaCacheSweeperService(store, NullLogger<MediaCacheSweeperService>.Instance);

        await sweeper.StartAsync(CancellationToken.None);
        // A background service that ignored the disabled setting would keep running here and never return.
        await sweeper.StopAsync(CancellationToken.None);

        var status = await sweeper.GetStatusAsync(CancellationToken.None);
        Assert.False(status.SweepEnabled);
        Assert.Equal(_root, status.RootPath, ignoreCase: true);
    }

    [Fact]
    public async Task An_on_demand_sweep_is_recorded_and_returned()
    {
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7 });
        await WriteExpiredAssetAsync();
        var sweeper = new MediaCacheSweeperService(store, NullLogger<MediaCacheSweeperService>.Instance);

        var result = await sweeper.SweepNowAsync(dryRun: false, CancellationToken.None);
        var status = await sweeper.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, result.DeletedExpired);
        Assert.NotNull(status.LastSweepAtUtc);
        Assert.Equal(1, status.LastSweep!.DeletedExpired);
        Assert.True(status.SweepEnabled);
    }

    [Fact]
    public async Task A_dry_run_is_not_recorded_as_the_last_sweep()
    {
        // The operator's "what would happen" probe must not overwrite the record of what did.
        var store = new MediaCacheStore(_root, new MediaCacheConfig { RetentionDays = 7 });
        await WriteExpiredAssetAsync();
        var sweeper = new MediaCacheSweeperService(store, NullLogger<MediaCacheSweeperService>.Instance);

        var dryRun = await sweeper.SweepNowAsync(dryRun: true, CancellationToken.None);
        var status = await sweeper.GetStatusAsync(CancellationToken.None);

        Assert.True(dryRun.DryRun);
        Assert.Equal(1, dryRun.DeletedExpired);
        Assert.Null(status.LastSweep);
        Assert.Null(status.LastSweepAtUtc);
    }

    private async Task WriteExpiredAssetAsync()
    {
        var id = AttachmentReferences.MediaAssetIdPrefix + Guid.NewGuid().ToString("N")[..16];
        var assetPath = Path.Combine(_root, id + ".bin");
        await File.WriteAllBytesAsync(assetPath, new byte[8]);

        var asset = new StoredMediaAsset
        {
            Id = id,
            MediaType = "application/octet-stream",
            FileName = id + ".bin",
            Path = assetPath,
            SizeBytes = 8,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-30)
        };

        await File.WriteAllTextAsync(
            Path.Combine(_root, id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(asset, CoreJsonContext.Default.StoredMediaAsset));
    }
}
