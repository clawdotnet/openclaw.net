using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Extensions;

internal sealed class GatewayOutboundDeliveryWorker
{
    public void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        MessagePipeline pipeline,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        HeartbeatService heartbeatService)
    {
        for (var j = 0; j < workerCount; j++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.OutboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                    while (pipeline.OutboundReader.TryRead(out var outbound))
                    {
                        if (!channelAdapters.TryGetValue(outbound.ChannelId, out var adapter))
                        {
                            logger.LogWarning("Unknown channel {ChannelId} for outbound message to {RecipientId}", outbound.ChannelId, outbound.RecipientId);
                            continue;
                        }

                        try
                        {
                            if (await DeliverAsync(adapter, outbound, logger, lifetime.ApplicationStopping) &&
                                heartbeatService.IsManagedHeartbeatJob(outbound.CronJobName))
                            {
                                try
                                {
                                    heartbeatService.RecordDeliverySucceeded(outbound.SessionId);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    logger.LogError(ex,
                                        "Failed to persist heartbeat delivery for session {SessionId}", outbound.SessionId);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }

    internal static async Task<bool> DeliverAsync(
        IChannelAdapter adapter, OutboundMessage outbound, ILogger logger, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        const int maxDeliveryAttempts = 2;
        for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
        {
            try
            {
                await adapter.SendAsync(outbound, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex,
                    "Outbound send for {ChannelId} was canceled without shutdown. Outcome unknown; not retrying.",
                    outbound.ChannelId);
                return false;
            }
            catch (ChannelDeliveryException ex) when (!ex.CanRetry)
            {
                logger.LogError(ex,
                    "Outbound delivery stopped for {ChannelId}. Delivered parts: {DeliveredParts}; outcome unknown: {OutcomeUnknown}. Automatic retry is unsafe or the rejection is permanent.",
                    outbound.ChannelId, ex.DeliveredParts, ex.OutcomeUnknown);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == maxDeliveryAttempts)
                {
                    logger.LogError(ex, "Outbound send failed for channel {ChannelId} after {Attempts} attempts", outbound.ChannelId, attempt);
                    return false;
                }
                logger.LogWarning(ex, "Outbound send failed for channel {ChannelId}, retrying…", outbound.ChannelId);
                var delay = ex is ChannelDeliveryException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero
                    ? retryAfter
                    : TimeSpan.FromMilliseconds(500);
                if (retryDelay is not null)
                    await retryDelay(delay, ct);
                else
                    await DelayWithCancellationAsync(delay, ct);
            }
        }
        return false;
    }

    private static async Task DelayWithCancellationAsync(TimeSpan delay, CancellationToken ct)
    {
        var remaining = delay;
        var maximumChunk = TimeSpan.FromDays(20);
        while (remaining > maximumChunk)
        {
            await Task.Delay(maximumChunk, ct);
            remaining -= maximumChunk;
        }
        await Task.Delay(remaining, ct);
    }
}
