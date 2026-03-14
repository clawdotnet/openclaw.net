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
using OpenClaw.Gateway;

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
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations)
    {
        StartSessionCleanup(lifetime, logger, sessionManager, sessionLocks, lockLastUsed);
        StartInboundWorkers(lifetime, logger, workerCount, isNonLoopbackBind, sessionManager, sessionLocks, lockLastUsed, pipeline, middlewarePipeline, wsChannel, agentRuntime, config, cronScheduler, toolApprovalService, approvalAuditStore, pairingManager, commandProcessor, operations);
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
                    var evicted = sessionManager.SweepExpiredActiveSessions();
                    if (evicted > 0)
                        logger.LogDebug("Proactive active-session sweep evicted {Count} expired sessions", evicted);

                    var now = DateTimeOffset.UtcNow;
                    var orphanThreshold = TimeSpan.FromHours(2);
                    
                    foreach (var kvp in sessionLocks)
                    {
                        var sessionKey = kvp.Key;
                        var semaphore = kvp.Value;
                        
                        if (!lockLastUsed.ContainsKey(sessionKey))
                            lockLastUsed[sessionKey] = now;
                        
                        if (!sessionManager.IsActive(sessionKey))
                        {
                            var lastUsed = lockLastUsed.GetValueOrDefault(sessionKey, now);
                            var isOrphaned = (now - lastUsed) > orphanThreshold;
                            
                            if (isOrphaned && semaphore.CurrentCount == 1 && semaphore.Wait(0))
                            {
                                try
                                {
                                    if (!sessionManager.IsActive(sessionKey))
                                    {
                                        if (sessionLocks.TryRemove(sessionKey, out _))
                                        {
                                            lockLastUsed.TryRemove(sessionKey, out _);
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
                                    try { semaphore.Release(); } catch { /* ignore */ }
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
        bool isNonLoopbackBind,
        SessionManager sessionManager,
        ConcurrentDictionary<string, SemaphoreSlim> sessionLocks,
        ConcurrentDictionary<string, DateTimeOffset> lockLastUsed,
        MessagePipeline pipeline,
        MiddlewarePipeline middlewarePipeline,
        WebSocketChannel wsChannel,
        IAgentRuntime agentRuntime,
        GatewayConfig config,
        CronScheduler? cronScheduler,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations)
    {
        for (var i = 0; i < workerCount; i++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.InboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                    while (pipeline.InboundReader.TryRead(out var msg))
                    {
                        Session? session = null;
                        SemaphoreSlim? lockObj = null;
                        var lockAcquired = false;
                        try
                        {
                            if (!msg.IsSystem)
                            {
                                if (!operations.ActorRateLimits.TryConsume("channel_sender", $"{msg.ChannelId}:{msg.SenderId}", "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = "Rate limit exceeded. Please slow down.",
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                    continue;
                                }

                                var effectiveSessionKey = msg.SessionId ?? $"{msg.ChannelId}:{msg.SenderId}";
                                if (!operations.ActorRateLimits.TryConsume("session", effectiveSessionKey, "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = "Session rate limit exceeded. Please retry shortly.",
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                    continue;
                                }
                            }

                            // ── Tool Approval Decision Short-Circuit ────────────
                            if (string.Equals(msg.Type, "tool_approval_decision", StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(msg.ApprovalId) &&
                                msg.Approved is not null)
                            {
                                var decisionOutcome = toolApprovalService.TrySetDecisionWithRequest(
                                    msg.ApprovalId,
                                    msg.Approved.Value,
                                    msg.ChannelId,
                                    msg.SenderId,
                                    requireRequesterMatch: true);

                                if (decisionOutcome.Result == ToolApprovalDecisionResult.Recorded && decisionOutcome.Request is not null)
                                {
                                    approvalAuditStore.RecordDecision(
                                        decisionOutcome.Request,
                                        msg.Approved.Value,
                                        "chat",
                                        msg.ChannelId,
                                        msg.SenderId);
                                    RecordApprovalDecisionEvent(
                                        operations,
                                        decisionOutcome.Request,
                                        msg.Approved.Value,
                                        "chat",
                                        msg.ChannelId,
                                        msg.SenderId);
                                }
                                else if (decisionOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                                {
                                    RecordApprovalDecisionRejectedEvent(
                                        operations,
                                        decisionOutcome.Request,
                                        msg.ApprovalId,
                                        "requester_mismatch",
                                        msg.ChannelId,
                                        msg.SenderId);
                                }

                                var ack = decisionOutcome.Result switch
                                {
                                    ToolApprovalDecisionResult.Recorded => $"Tool approval recorded: {msg.ApprovalId} = {(msg.Approved.Value ? "approved" : "denied")}",
                                    ToolApprovalDecisionResult.Unauthorized => $"Approval id is not valid for this sender/channel: {msg.ApprovalId}",
                                    _ => $"No pending approval found for id: {msg.ApprovalId}"
                                };

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = msg.SenderId,
                                    Text = ack,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);

                                continue;
                            }

                            // Text fallback: "/approve <approvalId> yes|no"
                            if (!string.IsNullOrWhiteSpace(msg.Text) && msg.Text.StartsWith("/approve ", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = msg.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 3)
                                {
                                    var approvalId = parts[1];
                                    var decision = parts[2];
                                    var approved = decision.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("y", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("approve", StringComparison.OrdinalIgnoreCase)
                                                   || decision.Equals("true", StringComparison.OrdinalIgnoreCase);
                                    var denied = decision.Equals("no", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("n", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("deny", StringComparison.OrdinalIgnoreCase)
                                                 || decision.Equals("false", StringComparison.OrdinalIgnoreCase);

                                    if (approved || denied)
                                    {
                                        var decisionOutcome = toolApprovalService.TrySetDecisionWithRequest(
                                            approvalId,
                                            approved,
                                            msg.ChannelId,
                                            msg.SenderId,
                                            requireRequesterMatch: true);

                                        if (decisionOutcome.Result == ToolApprovalDecisionResult.Recorded && decisionOutcome.Request is not null)
                                        {
                                            approvalAuditStore.RecordDecision(
                                                decisionOutcome.Request,
                                                approved,
                                                "chat",
                                                msg.ChannelId,
                                                msg.SenderId);
                                            RecordApprovalDecisionEvent(
                                                operations,
                                                decisionOutcome.Request,
                                                approved,
                                                "chat",
                                                msg.ChannelId,
                                                msg.SenderId);
                                        }
                                        else if (decisionOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                                        {
                                            RecordApprovalDecisionRejectedEvent(
                                                operations,
                                                decisionOutcome.Request,
                                                approvalId,
                                                "requester_mismatch",
                                                msg.ChannelId,
                                                msg.SenderId);
                                        }

                                        var ack = decisionOutcome.Result switch
                                        {
                                            ToolApprovalDecisionResult.Recorded => $"Tool approval recorded: {approvalId} = {(approved ? "approved" : "denied")}",
                                            ToolApprovalDecisionResult.Unauthorized => $"Approval id is not valid for this sender/channel: {approvalId}",
                                            _ => $"No pending approval found for id: {approvalId}"
                                        };

                                        await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                        {
                                            ChannelId = msg.ChannelId,
                                            RecipientId = msg.SenderId,
                                            Text = ack,
                                            ReplyToMessageId = msg.MessageId
                                        }, lifetime.ApplicationStopping);

                                        continue;
                                    }
                                }
                            }

                            // ── DM Pairing Check ─────────────────────────────────
                            var policy = "open";
                            if (msg.ChannelId == "sms") policy = config.Channels.Sms.DmPolicy;
                            if (msg.ChannelId == "telegram") policy = config.Channels.Telegram.DmPolicy;
                            if (msg.ChannelId == "whatsapp") policy = config.Channels.WhatsApp.DmPolicy;

                            if (policy is "closed")
                                continue; // Silently drop all inbound messages

                            if (!msg.IsSystem && policy is "pairing" && !pairingManager.IsApproved(msg.ChannelId, msg.SenderId))
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

                            session = msg.SessionId is not null
                                ? await sessionManager.GetOrCreateByIdAsync(msg.SessionId, msg.ChannelId, msg.SenderId, lifetime.ApplicationStopping)
                                : await sessionManager.GetOrCreateAsync(msg.ChannelId, msg.SenderId, lifetime.ApplicationStopping);
                            if (session is null)
                                throw new InvalidOperationException("Session manager returned null session.");

                            lockObj = sessionLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
                            await lockObj.WaitAsync(lifetime.ApplicationStopping);
                            lockAcquired = true;

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
                                        Subject = msg.Subject,
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
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                }
                                continue;
                            }

                            var messageText = mwContext.Text;
                            var useStreaming = msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

                            var approvalTimeout = TimeSpan.FromSeconds(Math.Clamp(config.Tooling.ToolApprovalTimeoutSeconds, 5, 3600));
                            async ValueTask<bool> ApprovalCallback(string toolName, string argsJson, CancellationToken ct)
                            {
                                var grant = operations.ApprovalGrants.TryConsume(session.Id, msg.ChannelId, msg.SenderId, toolName);
                                if (grant is not null)
                                {
                                    operations.RuntimeEvents.Append(new RuntimeEventEntry
                                    {
                                        Id = $"evt_{Guid.NewGuid():N}"[..20],
                                        SessionId = session.Id,
                                        ChannelId = msg.ChannelId,
                                        SenderId = msg.SenderId,
                                        Component = "approval",
                                        Action = "grant_consumed",
                                        Severity = "info",
                                        Summary = $"Reusable approval grant '{grant.Id}' applied for tool '{toolName}'.",
                                        Metadata = new Dictionary<string, string>
                                        {
                                            ["toolName"] = toolName,
                                            ["grantId"] = grant.Id,
                                            ["scope"] = grant.Scope
                                        }
                                    });
                                    return true;
                                }

                                var req = toolApprovalService.Create(session.Id, msg.ChannelId, msg.SenderId, toolName, argsJson, approvalTimeout);
                                approvalAuditStore.RecordCreated(req);
                                operations.RuntimeEvents.Append(new RuntimeEventEntry
                                {
                                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                                    SessionId = session.Id,
                                    ChannelId = msg.ChannelId,
                                    SenderId = msg.SenderId,
                                    Component = "approval",
                                    Action = "requested",
                                    Severity = "info",
                                    Summary = $"Tool approval requested for '{toolName}'.",
                                    Metadata = new Dictionary<string, string>
                                    {
                                        ["toolName"] = toolName,
                                        ["approvalId"] = req.ApprovalId
                                    }
                                });

                                var preview = argsJson.Length <= 800 ? argsJson : argsJson[..800] + "…";

                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                    {
                                        Type = "tool_approval_required",
                                        ApprovalId = req.ApprovalId,
                                        ToolName = toolName,
                                        ArgumentsPreview = preview,
                                        InReplyToMessageId = msg.MessageId,
                                        Text = "Tool approval required."
                                    }, ct);
                                }
                                else
                                {
                                    var prompt = $"Tool approval required.\n" +
                                                 $"- id: {req.ApprovalId}\n" +
                                                 $"- tool: {toolName}\n" +
                                                 $"- args: {preview}\n\n" +
                                                 $"Reply with: /approve {req.ApprovalId} yes|no";

                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = msg.SenderId,
                                        Text = prompt,
                                        ReplyToMessageId = msg.MessageId
                                    }, ct);
                                }

                                var outcome = await toolApprovalService.WaitForDecisionOutcomeAsync(req.ApprovalId, approvalTimeout, ct);
                                if (outcome.Result == ToolApprovalWaitResult.TimedOut && outcome.Request is not null)
                                {
                                    approvalAuditStore.RecordDecision(
                                        outcome.Request,
                                        approved: false,
                                        "timeout",
                                        actorChannelId: null,
                                        actorSenderId: null);
                                    RecordApprovalTimedOutEvent(operations, outcome.Request);
                                }

                                return outcome.Result == ToolApprovalWaitResult.Approved;
                            }

                            if (useStreaming)
                            {
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_start", "", msg.MessageId, lifetime.ApplicationStopping);

                                await foreach (var evt in agentRuntime.RunStreamingAsync(
                                    session, messageText, lifetime.ApplicationStopping, approvalCallback: ApprovalCallback))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, evt.EnvelopeType, evt.Content, msg.MessageId,
                                        lifetime.ApplicationStopping);
                                }
                                await sessionManager.PersistAsync(session, lifetime.ApplicationStopping);
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, lifetime.ApplicationStopping);
                            }
                            else
                            {
                                var responseText = await agentRuntime.RunAsync(session, messageText, lifetime.ApplicationStopping, approvalCallback: ApprovalCallback);
                                await sessionManager.PersistAsync(session, lifetime.ApplicationStopping);

                                // Append Usage Tracking string if configured
                                if (config.UsageFooter is "tokens")
                                    responseText += $"\n\n---\n↑ {session.TotalInputTokens} in / {session.TotalOutputTokens} out tokens";

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = msg.SenderId,
                                    Text = responseText,
                                    Subject = msg.Subject,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);
                            }
                        }
                        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            if (session is not null)
                                logger.LogWarning("Request canceled for session {SessionId}", session.Id);
                            else
                                logger.LogWarning("Request canceled for channel {ChannelId} sender {SenderId}", msg.ChannelId, msg.SenderId);
                        }
                        catch (Exception ex)
                        {
                            if (session is not null)
                                logger.LogError(ex, "Internal error processing message for session {SessionId}", session.Id);
                            else
                                logger.LogError(ex, "Internal error processing message for channel {ChannelId} sender {SenderId}", msg.ChannelId, msg.SenderId);

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
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                }
                            }
                            catch { /* Best effort */ }
                        }
                        finally
                        {
                            cronScheduler?.MarkJobCompleted(msg.CronJobName);

                            if (lockAcquired && lockObj is not null)
                            {
                                try { lockObj.Release(); } catch { /* ignore */ }
                            }

                            if (session is not null)
                                lockLastUsed[session.Id] = DateTimeOffset.UtcNow;
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

    private static void RecordApprovalDecisionEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest request,
        bool approved,
        string decisionSource,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = request.ApprovalId,
            ["toolName"] = request.ToolName,
            ["approved"] = approved ? "true" : "false",
            ["decisionSource"] = decisionSource
        };

        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "decision_recorded",
            Severity = "info",
            Summary = $"{decisionSource} {(approved ? "approved" : "denied")} tool approval '{request.ApprovalId}'.",
            Metadata = metadata
        });
    }

    private static void RecordApprovalDecisionRejectedEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest? request,
        string approvalId,
        string reason,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = approvalId,
            ["reason"] = reason
        };

        if (request is not null)
            metadata["toolName"] = request.ToolName;
        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request?.SessionId,
            ChannelId = request?.ChannelId,
            SenderId = request?.SenderId,
            Component = "approval",
            Action = "decision_rejected",
            Severity = "warning",
            Summary = $"Rejected approval decision attempt for '{approvalId}'.",
            Metadata = metadata
        });
    }

    private static void RecordApprovalTimedOutEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest request)
    {
        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "timed_out",
            Severity = "warning",
            Summary = $"Tool approval timed out for '{request.ToolName}'.",
            Metadata = new Dictionary<string, string>
            {
                ["approvalId"] = request.ApprovalId,
                ["toolName"] = request.ToolName
            }
        });
    }
}
