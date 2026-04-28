using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Extensions;

internal static class GatewayWorkers
{
    public static void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        bool isNonLoopbackBind,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        IAgentRuntime agentRuntime,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        GatewayConfig config,
        CronScheduler? cronScheduler,
        HeartbeatService heartbeatService,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations,
        RuntimeMetrics? runtimeMetrics = null,
        LearningService? learningService = null,
        GatewayAutomationService? automationService = null,
        ContractGovernanceService? contractGovernance = null,
        AudioTranscriptionService? audioTranscriptionService = null)
    {
        new GatewaySessionCleanupWorker().Start(lifetime, logger, sessionManager);

        new GatewayInboundMessageWorker().Start(
            lifetime,
            logger,
            workerCount,
            isNonLoopbackBind,
            sessionManager,
            sessionLocks,
            lockLastUsed,
            pipeline,
            middlewarePipeline,
            wsChannel,
            agentRuntime,
            channelAdapters,
            config,
            cronScheduler,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations,
            runtimeMetrics,
            learningService,
            automationService,
            contractGovernance,
            audioTranscriptionService);

        new GatewayOutboundDeliveryWorker().Start(
            lifetime,
            logger,
            workerCount,
            pipeline,
            channelAdapters,
            heartbeatService);
    }

    internal static void CleanupSessionLocksOnce(
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        DateTimeOffset now,
        TimeSpan orphanThreshold,
        ILogger logger)
    {
        foreach (var kvp in sessionLocks)
        {
            var sessionKey = kvp.Key;
            var semaphore = kvp.Value;

            lockLastUsed.TryAdd(sessionKey, now);

            if (sessionManager.IsActive(sessionKey))
            {
                lockLastUsed[sessionKey] = now;
                continue;
            }

            var lastUsed = lockLastUsed.GetValueOrDefault(sessionKey, now);
            var isOrphaned = (now - lastUsed) > orphanThreshold;
            if (!isOrphaned || semaphore.CurrentCount != 1 || !semaphore.Wait(0))
                continue;

            var removed = false;
            try
            {
                if (sessionManager.IsActive(sessionKey))
                {
                    lockLastUsed[sessionKey] = now;
                    continue;
                }

                if (sessionLocks.TryRemove(sessionKey, out var removedSemaphore))
                {
                    removed = true;
                    lockLastUsed.TryRemove(sessionKey, out _);
                    try { removedSemaphore.Release(); } catch { }
                    removedSemaphore.Dispose();
                    logger.LogDebug("Cleaned up session lock for {SessionKey}", sessionKey);
                }
            }
            finally
            {
                if (!removed)
                {
                    try { semaphore.Release(); } catch { }
                }
            }
        }
    }

    internal static void DisposeSessionLocks(
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ILogger logger)
    {
        foreach (var sessionKey in sessionLocks.Keys)
        {
            if (!sessionLocks.TryRemove(sessionKey, out var semaphore))
                continue;

            try
            {
                semaphore.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to dispose session lock for {SessionKey}", sessionKey);
            }
        }
    }
}
