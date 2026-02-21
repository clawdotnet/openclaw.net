using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Extensions;

public static class GatewayWorkers
{
    public static void Start(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        AgentRuntime agentRuntime,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        GatewayConfig config,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor)
    {
        StartSessionCleanup(lifetime, logger, sessionManager, sessionLocks, lockLastUsed);
        StartInboundWorkers(lifetime, logger, workerCount, sessionManager, sessionLocks, pipeline, middlewarePipeline, wsChannel, agentRuntime, config, pairingManager, commandProcessor);
        StartOutboundWorkers(lifetime, logger, workerCount, pipeline, channelAdapters);
    }

    private static void StartSessionCleanup(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
            {
                try 
                {
                    var now = DateTimeOffset.UtcNow;
                    var orphanThreshold = TimeSpan.FromHours(2);
                    
                    foreach (var kvp in sessionLocks.ToArray())
                    {
                        var sessionKey = kvp.Key;
                        var semaphore = kvp.Value;
                        
                        if (!lockLastUsed.ContainsKey(sessionKey))
                            lockLastUsed[sessionKey] = now;
                        
                        if (!sessionManager.IsActive(sessionKey))
                        {
                            var lastUsed = lockLastUsed.GetValueOrDefault(sessionKey, now);
                            var isOrphaned = (now - lastUsed) > orphanThreshold;
                            
                            if (semaphore.Wait(0))
                            {
                                try
                                {
                                    if (!sessionManager.IsActive(sessionKey))
                                    {
                                        if (sessionLocks.TryRemove(sessionKey, out _))
                                        {
                                            lockLastUsed.TryRemove(sessionKey, out _);
                                            semaphore.Dispose();
                                            logger.LogDebug("Cleaned up session lock for {SessionKey}", sessionKey);
                                        }
                                    }
                                    else
                                    {
                                        lockLastUsed[sessionKey] = now;
                                    }
                                }
                                finally
                                {
                                    if (sessionLocks.ContainsKey(sessionKey))
                                        semaphore.Release();
                                }
                            }
                            else if (isOrphaned)
                            {
                                if (sessionLocks.TryRemove(sessionKey, out var removed))
                                {
                                    lockLastUsed.TryRemove(sessionKey, out _);
                                    logger.LogWarning("Force-removed orphaned session lock for {SessionKey}", sessionKey);
                                    try { removed.Dispose(); } catch { /* ignore */ }
                                }
                            }
                        }
                        else
                        {
                            lockLastUsed[sessionKey] = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during session lock cleanup");
                }
            }
        }, lifetime.ApplicationStopping);
    }

    private static void StartInboundWorkers(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        AgentRuntime agentRuntime,
        GatewayConfig config,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor)
    {
        for (var i = 0; i < workerCount; i++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.InboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                    while (pipeline.InboundReader.TryRead(out var msg))
                    {
                        // ── DM Pairing Check ─────────────────────────────────
                        var policy = "open";
                        if (msg.ChannelId == "sms") policy = config.Channels.Sms.DmPolicy;
                        if (msg.ChannelId == "telegram") policy = config.Channels.Telegram.DmPolicy;

                        if (policy is "closed")
                            continue; // Silently drop all inbound messages
                            
                        if (policy is "pairing" && !pairingManager.IsApproved(msg.ChannelId, msg.SenderId))
                        {
                            var code = pairingManager.GeneratePairingCode(msg.ChannelId, msg.SenderId);
                            var pairingMsg = $"Welcome to OpenClaw. Your pairing code is {code}. Your messages will be ignored until an admin approves this pair.";
                            
                            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                            {
                                ChannelId = msg.ChannelId,
                                RecipientId = msg.SenderId,
                                Text = pairingMsg,
                                ReplyToMessageId = msg.MessageId
                            }, lifetime.ApplicationStopping);

                            continue; // Drop the inbound request after sending pairing code
                        }

                        var session = await sessionManager.GetOrCreateAsync(msg.ChannelId, msg.SenderId, lifetime.ApplicationStopping);
                        var lockObj = sessionLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
                        await lockObj.WaitAsync(lifetime.ApplicationStopping);

                        try
                        {
                            // ── Chat Command Processing ──────────────────────
                            var (handled, cmdResponse) = await commandProcessor.TryProcessCommandAsync(session, msg.Text, lifetime.ApplicationStopping);
                            if (handled)
                            {
                                if (cmdResponse is not null)
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = cmdResponse,
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                }
                                continue; // Skip LLM completely
                            }

                            var mwContext = new MessageContext
                            {
                                ChannelId = msg.ChannelId,
                                SenderId = msg.SenderId,
                                Text = msg.Text,
                                MessageId = msg.MessageId,
                                SessionInputTokens = session.TotalInputTokens,
                                SessionOutputTokens = session.TotalOutputTokens
                            };

                            var shouldProceed = await middlewarePipeline.ExecuteAsync(mwContext, lifetime.ApplicationStopping);
                            if (!shouldProceed)
                            {
                                var shortCircuitText = mwContext.ShortCircuitResponse ?? "Request blocked.";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "assistant_message", shortCircuitText, msg.MessageId,
                                        lifetime.ApplicationStopping);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = shortCircuitText,
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                }
                                continue;
                            }

                            var messageText = mwContext.Text;
                            var useStreaming = msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

                            if (useStreaming)
                            {
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_start", "", msg.MessageId, lifetime.ApplicationStopping);

                                await foreach (var evt in agentRuntime.RunStreamingAsync(
                                    session, messageText, lifetime.ApplicationStopping))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, evt.EnvelopeType, evt.Content, msg.MessageId,
                                        lifetime.ApplicationStopping);
                                }
                                
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, lifetime.ApplicationStopping);
                                await sessionManager.PersistAsync(session, lifetime.ApplicationStopping);
                            }
                            else
                            {
                                var responseText = await agentRuntime.RunAsync(session, messageText, lifetime.ApplicationStopping);
                                await sessionManager.PersistAsync(session, lifetime.ApplicationStopping);

                                // Append Usage Tracking string if configured
                                if (config.UsageFooter is "tokens")
                                    responseText += $"\n\n---\n↑ {session.TotalInputTokens} in / {session.TotalOutputTokens} out tokens";

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = msg.SenderId,
                                    Text = responseText,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogWarning("Request canceled for session {SessionId}", session.Id);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Internal error processing message for session {SessionId}", session.Id);

                            try
                            {
                                var errorText = $"Internal error ({ex.GetType().Name}).";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "error", errorText, msg.MessageId,
                                        lifetime.ApplicationStopping);
                                        
                                    await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, lifetime.ApplicationStopping);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = errorText,
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                }
                            }
                            catch { /* Best effort */ }
                        }
                        finally
                        {
                            lockObj.Release();
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }

    private static void StartOutboundWorkers(
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int workerCount,
        MessagePipeline pipeline,
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters)
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

                        const int maxDeliveryAttempts = 2;
                        for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
                        {
                            try
                            {
                                await adapter.SendAsync(outbound, lifetime.ApplicationStopping);
                                break;
                            }
                            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                if (attempt < maxDeliveryAttempts)
                                {
                                    logger.LogWarning(ex, "Outbound send failed for channel {ChannelId}, retrying…", outbound.ChannelId);
                                    await Task.Delay(500, lifetime.ApplicationStopping);
                                }
                                else
                                {
                                    logger.LogError(ex, "Outbound send failed for channel {ChannelId} after {Attempts} attempts", outbound.ChannelId, maxDeliveryAttempts);
                                }
                            }
                        }
                    }
                }
            }, lifetime.ApplicationStopping);
        }
    }
}
