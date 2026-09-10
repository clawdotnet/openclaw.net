using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Testing;

namespace OpenClaw.Gateway;

internal sealed class RegressionCaptureWorker(GatewayConfig config, IMemoryStore memory,
    IRedactionPipeline redaction, ILogger<RegressionCaptureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Memory.RegressionCaptureEnabled) return;
        if (config.Memory.RegressionCaptureMaxFiles is < 1 or > 10_000 || memory is not ISessionSnapshotSource snapshots)
        { logger.LogError("Regression capture requires a snapshot-capable store and a file limit from 1 to 10000."); return; }
        var directory = Path.Combine(config.Memory.StoragePath, "regression-captures");
        DateTimeOffset? since = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.jsonl").Take(config.Memory.RegressionCaptureMaxFiles).Count() >= config.Memory.RegressionCaptureMaxFiles)
                    continue;
                var started = DateTimeOffset.UtcNow;
                await foreach (var session in snapshots.ReadSnapshotsAsync(since, stoppingToken))
                {
                    try { await RegressionCapture.CaptureAsync(session, directory, redaction, config.Memory.RegressionCaptureMaxFiles, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning(ex, "Skipping a session that could not be captured."); }
                    if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.jsonl").Take(config.Memory.RegressionCaptureMaxFiles).Count() >= config.Memory.RegressionCaptureMaxFiles) break;
                }
                since = started.AddSeconds(-30); // Overlap tolerates save timestamps around the scan boundary.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic regression capture failed; runtime execution is unaffected."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
