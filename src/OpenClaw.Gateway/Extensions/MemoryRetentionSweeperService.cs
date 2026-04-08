using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

public interface IMemoryRetentionCoordinator
{
    ValueTask<RetentionRunStatus> GetStatusAsync(CancellationToken ct);
    ValueTask<RetentionSweepResult> SweepNowAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Optional background retention sweeper for persisted sessions and branches.
/// Runs only when Memory.Retention.Enabled=true and the configured store supports retention.
/// </summary>
public sealed class MemoryRetentionSweeperService : BackgroundService, IMemoryRetentionCoordinator
{
    private static readonly HashSet<string> ProtectedRetentionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "keep",
        "pinned",
        "retain",
        "retention-exempt"
    };

    private readonly GatewayConfig _config;
    private readonly SessionManager _sessionManager;
    private readonly RuntimeMetrics _metrics;
    private readonly ILogger<MemoryRetentionSweeperService> _logger;
    private readonly IMemoryRetentionStore? _retentionStore;
    private readonly Func<IReadOnlyDictionary<string, SessionMetadataSnapshot>>? _metadataSnapshotProvider;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusLock = new();
    private RetentionRunStatus _status;

    public MemoryRetentionSweeperService(
        GatewayConfig config,
        SessionManager sessionManager,
        IMemoryStore memoryStore,
        RuntimeMetrics metrics,
        ILogger<MemoryRetentionSweeperService> logger,
        Func<IReadOnlyDictionary<string, SessionMetadataSnapshot>>? metadataSnapshotProvider = null)
    {
        _config = config;
        _sessionManager = sessionManager;
        _metrics = metrics;
        _logger = logger;
        _retentionStore = memoryStore as IMemoryRetentionStore;
        _metadataSnapshotProvider = metadataSnapshotProvider;

        _status = new RetentionRunStatus
        {
            Enabled = _config.Memory.Retention.Enabled,
            StoreSupportsRetention = _retentionStore is not null,
            IsRunning = false
        };
    }

    public async ValueTask<RetentionRunStatus> GetStatusAsync(CancellationToken ct)
    {
        RetentionRunStatus snapshot;
        lock (_statusLock)
        {
            snapshot = CloneStatus(_status);
        }

        if (_retentionStore is null)
            return snapshot;

        try
        {
            snapshot.StoreStats = await _retentionStore.GetRetentionStatsAsync(ct);
        }
        catch (Exception ex)
        {
            snapshot.LastError ??= $"Failed to load retention stats: {ex.Message}";
        }

        return snapshot;
    }

    public async ValueTask<RetentionSweepResult> SweepNowAsync(bool dryRun, CancellationToken ct)
    {
        if (!_config.Memory.Retention.Enabled)
            throw new InvalidOperationException("Memory retention is disabled.");
        if (_retentionStore is null)
            throw new InvalidOperationException("Current memory store does not support retention sweeps.");

        return await RunSweepCoreAsync(dryRun, trigger: "manual", ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Memory.Retention.Enabled)
        {
            _logger.LogInformation("Memory retention sweeper is disabled.");
            return;
        }

        if (_retentionStore is null)
        {
            _logger.LogWarning("Memory retention sweeper enabled but current memory store does not implement IMemoryRetentionStore.");
            return;
        }

        if (_config.Memory.Retention.RunOnStartup)
            await TryRunSweepAsync(dryRun: false, trigger: "startup", stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(5, _config.Memory.Retention.SweepIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRunSweepAsync(dryRun: false, trigger: "timer", stoppingToken);
        }
    }

    private async Task TryRunSweepAsync(bool dryRun, string trigger, CancellationToken ct)
    {
        try
        {
            await RunSweepCoreAsync(dryRun, trigger, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping retention sweep ({Trigger}) because another run is active.", trigger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful stop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention sweep failed for trigger '{Trigger}'", trigger);
        }
    }

    private async ValueTask<RetentionSweepResult> RunSweepCoreAsync(bool dryRun, string trigger, CancellationToken ct)
    {
        if (_retentionStore is null)
            throw new InvalidOperationException("Retention-capable store is not available.");

        if (!await _runGate.WaitAsync(0, ct))
            throw new InvalidOperationException("A retention sweep is already running.");

        var startedAt = DateTimeOffset.UtcNow;
        SetRunningState(isRunning: true, startedAt);

        try
        {
            var request = BuildSweepRequest(startedAt, dryRun);
            var protectedIds = await BuildProtectedSetAsync(ct);
            var result = await _retentionStore.SweepAsync(request, protectedIds, ct);

            _metrics.IncrementRetentionSweepRuns();
            _metrics.AddRetentionArchivedItems(result.TotalArchived);
            _metrics.AddRetentionDeletedItems(result.TotalDeleted);
            _metrics.AddRetentionSkippedProtectedSessions(result.SkippedProtectedSessions);
            _metrics.SetRetentionLastRun(result.CompletedAtUtc, result.DurationMs, succeeded: result.Errors.Count == 0);

            SetCompletedState(result, succeeded: result.Errors.Count == 0, error: null);
            _logger.LogInformation(
                "Retention sweep ({Trigger}) completed: dryRun={DryRun} eligible={Eligible} archived={Archived} deleted={Deleted} errors={ErrorCount}",
                trigger,
                dryRun,
                result.TotalEligible,
                result.TotalArchived,
                result.TotalDeleted,
                result.Errors.Count);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.IncrementRetentionSweepRuns();
            _metrics.IncrementRetentionSweepFailures();
            _metrics.SetRetentionLastRun(DateTimeOffset.UtcNow, (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds), succeeded: false);

            SetCompletedState(result: null, succeeded: false, error: ex.Message);
            throw;
        }
        finally
        {
            lock (_statusLock)
            {
                _status.IsRunning = false;
            }
            _runGate.Release();
        }
    }

    private RetentionSweepRequest BuildSweepRequest(DateTimeOffset nowUtc, bool dryRun)
    {
        var retention = _config.Memory.Retention;
        var archivePath = retention.ArchivePath;
        if (!Path.IsPathRooted(archivePath))
            archivePath = Path.GetFullPath(archivePath);

        return new RetentionSweepRequest
        {
            NowUtc = nowUtc,
            SessionExpiresBeforeUtc = nowUtc.AddDays(-Math.Max(1, retention.SessionTtlDays)),
            BranchExpiresBeforeUtc = nowUtc.AddDays(-Math.Max(1, retention.BranchTtlDays)),
            ArchiveEnabled = retention.ArchiveEnabled,
            ArchivePath = archivePath,
            ArchiveRetentionDays = Math.Max(1, retention.ArchiveRetentionDays),
            MaxItems = Math.Max(10, retention.MaxItemsPerSweep),
            DryRun = dryRun
        };
    }

    private async ValueTask<IReadOnlySet<string>> BuildProtectedSetAsync(CancellationToken ct)
    {
        var activeSessions = await _sessionManager.ListActiveAsync(ct);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in activeSessions)
        {
            if (!string.IsNullOrWhiteSpace(session.Id))
                set.Add(session.Id);
        }

        if (_metadataSnapshotProvider is not null)
        {
            foreach (var metadata in _metadataSnapshotProvider().Values)
            {
                if (metadata.Starred || metadata.Tags.Any(static tag => ProtectedRetentionTags.Contains(tag)))
                    set.Add(metadata.SessionId);
            }
        }

        return set;
    }

    private void SetRunningState(bool isRunning, DateTimeOffset startedAt)
    {
        lock (_statusLock)
        {
            _status.Enabled = _config.Memory.Retention.Enabled;
            _status.StoreSupportsRetention = _retentionStore is not null;
            _status.IsRunning = isRunning;
            _status.LastRunStartedAtUtc = startedAt;
            _status.LastError = null;
        }
    }

    private void SetCompletedState(RetentionSweepResult? result, bool succeeded, string? error)
    {
        lock (_statusLock)
        {
            _status.Enabled = _config.Memory.Retention.Enabled;
            _status.StoreSupportsRetention = _retentionStore is not null;
            _status.LastRunCompletedAtUtc = DateTimeOffset.UtcNow;
            _status.LastRunSucceeded = succeeded;
            _status.LastError = error;
            _status.TotalRuns++;
            if (!succeeded)
                _status.TotalSweepErrors++;

            if (result is not null)
            {
                _status.LastResult = result;
                _status.LastRunDurationMs = result.DurationMs;
                _status.TotalArchivedItems += result.TotalArchived;
                _status.TotalDeletedItems += result.TotalDeleted;
            }
            else if (_status.LastRunStartedAtUtc is not null)
            {
                _status.LastRunDurationMs = (long)Math.Max(
                    0,
                    (DateTimeOffset.UtcNow - _status.LastRunStartedAtUtc.Value).TotalMilliseconds);
            }
        }
    }

    private static RetentionRunStatus CloneStatus(RetentionRunStatus status)
    {
        return new RetentionRunStatus
        {
            Enabled = status.Enabled,
            StoreSupportsRetention = status.StoreSupportsRetention,
            IsRunning = status.IsRunning,
            LastRunStartedAtUtc = status.LastRunStartedAtUtc,
            LastRunCompletedAtUtc = status.LastRunCompletedAtUtc,
            LastRunDurationMs = status.LastRunDurationMs,
            LastRunSucceeded = status.LastRunSucceeded,
            LastError = status.LastError,
            TotalRuns = status.TotalRuns,
            TotalSweepErrors = status.TotalSweepErrors,
            TotalArchivedItems = status.TotalArchivedItems,
            TotalDeletedItems = status.TotalDeletedItems,
            LastResult = status.LastResult,
            StoreStats = status.StoreStats
        };
    }
}
