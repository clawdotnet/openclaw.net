using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

/// <summary>Operator-facing view of the cache: what it is allowed to hold, and what the last sweep did.</summary>
internal sealed record MediaCacheStatus(
    bool SweepEnabled,
    string RootPath,
    MediaCacheConfig Limits,
    DateTimeOffset? LastSweepAtUtc,
    MediaCacheSweepResult? LastSweep);

internal interface IMediaCacheCoordinator
{
    ValueTask<MediaCacheStatus> GetStatusAsync(CancellationToken ct);
    ValueTask<MediaCacheSweepResult> SweepNowAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Periodic retention sweep for the media cache directory.
/// </summary>
/// <remarks>
/// <para>
/// Disabled — and the host is left untouched — when
/// <see cref="MediaCacheConfig.RetentionDays"/> is zero or less. That value predates this sweep and means
/// "keep everything", so honouring it is what makes the change non-breaking for existing deployments.
/// </para>
/// <para>
/// A failed sweep is logged and retried on the next interval rather than allowed to escape. A
/// <see cref="BackgroundService"/> whose <c>ExecuteAsync</c> faults stops the host by default, and "a
/// locked file in the cache directory" is not a reason to take the gateway down.
/// </para>
/// </remarks>
internal sealed class MediaCacheSweeperService : BackgroundService, IMediaCacheCoordinator
{
    private readonly MediaCacheStore _store;
    private readonly ILogger<MediaCacheSweeperService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCacheSweepResult? _lastSweep;
    private DateTimeOffset? _lastSweepAtUtc;

    public MediaCacheSweeperService(
        MediaCacheStore store,
        ILogger<MediaCacheSweeperService> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store.Config.RetentionDays <= 0)
        {
            _logger.LogInformation(
                "Media cache retention is disabled (RetentionDays={RetentionDays}); the cache directory is never swept.",
                _store.Config.RetentionDays);
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(_store.Config.SweepIntervalMinutes, 1));
        using var timer = new PeriodicTimer(interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepNowAsync(dryRun: false, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Media cache sweep failed; retrying in {Interval}.",
                    interval);
            }
        }
    }

    public async ValueTask<MediaCacheSweepResult> SweepNowAsync(bool dryRun, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _store
                .SweepAsync(_timeProvider.GetUtcNow(), dryRun, ct)
                .ConfigureAwait(false);

            if (!dryRun)
            {
                _lastSweep = result;
                _lastSweepAtUtc = _timeProvider.GetUtcNow();
            }

            if (result.DeletedExpired + result.DeletedOverCapacity + result.DeletedIncomplete > 0)
            {
                _logger.LogInformation(
                    "Media cache sweep removed {Expired} expired, {OverCapacity} over-capacity and {Incomplete} incomplete assets, freeing {FreedBytes} bytes.",
                    result.DeletedExpired,
                    result.DeletedOverCapacity,
                    result.DeletedIncomplete,
                    result.FreedBytes);
            }

            if (result.FailedDeletions > 0)
            {
                _logger.LogWarning(
                    "Media cache sweep could not delete {Failed} assets; they stay and are retried next sweep.",
                    result.FailedDeletions);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<MediaCacheStatus> GetStatusAsync(CancellationToken ct) =>
        ValueTask.FromResult(new MediaCacheStatus(
            SweepEnabled: _store.Config.RetentionDays > 0,
            RootPath: _store.RootPath,
            Limits: _store.Config,
            LastSweepAtUtc: _lastSweepAtUtc,
            LastSweep: _lastSweep));
}
