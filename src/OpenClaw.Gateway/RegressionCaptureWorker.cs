using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Testing;

namespace OpenClaw.Gateway;

internal sealed class RegressionCaptureWorker(GatewayConfig config, IMemoryStore memory,
    ISessionAdminStore sessions, IRedactionPipeline redaction, ILogger<RegressionCaptureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Memory.RegressionCaptureEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                for (var page = 1; ; page++)
                {
                    var batch = await sessions.ListSessionsAsync(page, 100, new SessionListQuery(), stoppingToken);
                    foreach (var summary in batch.Items)
                    {
                        var session = await memory.GetSessionAsync(summary.Id, stoppingToken);
                        if (session is not null)
                            await RegressionCapture.CaptureAsync(session, Path.Combine(config.Memory.StoragePath, "regression-captures"),
                                redaction, config.Memory.RegressionCaptureMaxFiles, stoppingToken);
                    }
                    if (!batch.HasMore) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic regression capture failed; runtime execution is unaffected."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
