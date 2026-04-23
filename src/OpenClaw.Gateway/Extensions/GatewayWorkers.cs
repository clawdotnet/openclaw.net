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
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Agent.Plugins;
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
        HeartbeatService heartbeatService,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations,
        RuntimeMetrics? runtimeMetrics = null,
        LearningService? learningService = null,
        GatewayAutomationService? automationService = null,
        ContractGovernanceService? contractGovernance = null)
    {
        StartSessionCleanup(lifetime, logger, sessionManager, sessionLocks, lockLastUsed);
        StartInboundWorkers(lifetime, logger, workerCount, isNonLoopbackBind, sessionManager, sessionLocks, lockLastUsed, pipeline, middlewarePipeline, wsChannel, agentRuntime, channelAdapters, config, cronScheduler, heartbeatService, toolApprovalService, approvalAuditStore, pairingManager, commandProcessor, operations, runtimeMetrics, learningService, automationService, contractGovernance);
        StartOutboundWorkers(lifetime, logger, workerCount, pipeline, channelAdapters, heartbeatService);
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

                    sessionManager.CleanupSessionLocksOnce(DateTimeOffset.UtcNow, TimeSpan.FromHours(2));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during session lock cleanup");
                }
            }
        }, lifetime.ApplicationStopping);
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
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters,
        GatewayConfig config,
        CronScheduler? cronScheduler,
        HeartbeatService heartbeatService,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        PairingManager pairingManager,
        ChatCommandProcessor commandProcessor,
        RuntimeOperationsState operations,
        RuntimeMetrics? runtimeMetrics,
        LearningService? learningService,
        GatewayAutomationService? automationService,
        ContractGovernanceService? contractGovernance)
    {
        var routeResolver = config.Routing.Enabled
            ? new OpenClaw.Gateway.Integrations.AgentRouteResolver(config.Routing)
            : null;

        for (var i = 0; i < workerCount; i++)
        {
            _ = Task.Run(async () =>
            {
                while (await pipeline.InboundReader.WaitToReadAsync(lifetime.ApplicationStopping))
                {
                        while (pipeline.InboundReader.TryRead(out var msg))
                        {
                            Session? session = null;
                            AutomationDefinition? automation = null;
                            IAsyncDisposable? sessionLock = null;
                            IBridgedChannelControl? bridgedAdapter = null;
                            var bridgedTypingStarted = false;
                            long initialInputTokens = 0;
                            long initialOutputTokens = 0;
                            var automationRetryAttempt = 0;
                            var conversationRecipientId = ResolveConversationRecipientId(msg);
                            using var processingCts = CreateProcessingCts(msg.RequestCancellation, lifetime.ApplicationStopping);
                            var processingCt = processingCts?.Token ?? lifetime.ApplicationStopping;

                            async Task FinalizeAutomationRunAsync(AutomationRunCompletion completion, CancellationToken finalizeCt)
                            {
                                if (automation is null || automationService is null)
                                    return;

                                var contractIdBeforeFinalize = session?.ContractPolicy?.Id;
                                await automationService.FinalizeRunAsync(automation, msg, session, completion, finalizeCt);

                                if (session is not null &&
                                    !string.Equals(contractIdBeforeFinalize, session.ContractPolicy?.Id, StringComparison.Ordinal))
                                {
                                    await sessionManager.PersistAsync(session, finalizeCt, sessionLockHeld: true);
                                }
                            }

                            try
                            {
                                if (!msg.IsSystem)
                                {
                                    if (!operations.ActorRateLimits.TryConsume("channel_sender", $"{msg.ChannelId}:{msg.SenderId}", "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = "Rate limit exceeded. Please slow down.",
                                        ReplyToMessageId = msg.MessageId
                                    }, lifetime.ApplicationStopping);
                                    continue;
                                }

                                var effectiveSessionKey = msg.SessionId ?? $"{msg.ChannelId}:{conversationRecipientId}";
                                if (!operations.ActorRateLimits.TryConsume("session", effectiveSessionKey, "inbound_chat", out _))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
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
                                    runtimeMetrics?.IncrementApprovalDecisionsRecorded();
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
                                    runtimeMetrics?.IncrementApprovalDecisionsRejected();
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
                                    RecipientId = conversationRecipientId,
                                    AccountId = msg.AccountId,
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
                                        runtimeMetrics?.IncrementApprovalDecisionsRecorded();
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
                                        runtimeMetrics?.IncrementApprovalDecisionsRejected();
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
                                            RecipientId = conversationRecipientId,
                                            AccountId = msg.AccountId,
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
                            if (msg.ChannelId == "teams") policy = config.Channels.Teams.DmPolicy;
                            if (msg.ChannelId == "slack") policy = config.Channels.Slack.DmPolicy;
                            if (msg.ChannelId == "discord") policy = config.Channels.Discord.DmPolicy;
                            if (msg.ChannelId == "signal") policy = config.Channels.Signal.DmPolicy;

                            if (policy is "closed")
                                continue; // Silently drop all inbound messages

                            if (!msg.IsSystem && policy is "pairing" && !pairingManager.IsApproved(msg.ChannelId, msg.SenderId))
                            {
                                var code = pairingManager.GeneratePairingCode(msg.ChannelId, msg.SenderId);
                                var pairingMsg = $"Welcome to OpenClaw. Your pairing code is {code}. Your messages will be ignored until an admin approves this pair.";

                                await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    RecipientId = conversationRecipientId,
                                    AccountId = msg.AccountId,
                                    Text = pairingMsg,
                                    ReplyToMessageId = msg.MessageId
                                }, lifetime.ApplicationStopping);

                                continue; // Drop the inbound request after sending pairing code
                            }

                            // ── Multi-Agent Route Resolution ─────────────────
                            var resolvedRoute = routeResolver?.Resolve(msg.ChannelId, msg.SenderId);

                            session = msg.SessionId is not null
                                ? await sessionManager.GetOrCreateByIdAsync(msg.SessionId, msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping)
                                : await sessionManager.GetOrCreateAsync(msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping);
                            if (session is null)
                                throw new InvalidOperationException("Session manager returned null session.");

                            // Apply route overrides to session
                            if (resolvedRoute is not null)
                            {
                                session.ModelOverride = string.IsNullOrWhiteSpace(resolvedRoute.ModelOverride)
                                    ? session.ModelOverride
                                    : resolvedRoute.ModelOverride.Trim();
                                session.ModelProfileId = string.IsNullOrWhiteSpace(resolvedRoute.ModelProfileId)
                                    ? null
                                    : resolvedRoute.ModelProfileId.Trim();
                                session.PreferredModelTags = resolvedRoute.PreferredModelTags
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                session.FallbackModelProfileIds = resolvedRoute.FallbackModelProfileIds
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                                session.ModelRequirements = resolvedRoute.ModelRequirements ?? new ModelSelectionRequirements();
                                session.SystemPromptOverride = string.IsNullOrWhiteSpace(resolvedRoute.SystemPrompt)
                                    ? null
                                    : resolvedRoute.SystemPrompt.Trim();
                                session.RoutePresetId = string.IsNullOrWhiteSpace(resolvedRoute.PresetId)
                                    ? null
                                    : resolvedRoute.PresetId.Trim();
                                session.RouteAllowedTools = resolvedRoute.AllowedTools
                                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                                    .Select(static item => item.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
                            }
                            else
                            {
                                session.ModelProfileId = null;
                                session.PreferredModelTags = [];
                                session.FallbackModelProfileIds = [];
                                session.ModelRequirements = new ModelSelectionRequirements();
                                session.SystemPromptOverride = null;
                                session.RoutePresetId = null;
                                session.RouteAllowedTools = [];
                            }

                            initialInputTokens = session.TotalInputTokens;
                            initialOutputTokens = session.TotalOutputTokens;

                            sessionLock = await sessionManager.AcquireSessionLockAsync(session.Id, processingCt);

                            if (automationService is not null && !string.IsNullOrWhiteSpace(msg.CronJobName))
                            {
                                automation = await automationService.GetAsync(msg.CronJobName, processingCt);
                                if (automation is not null)
                                {
                                    await automationService.MarkRunRunningAsync(automation, msg, processingCt);
                                    if (!string.IsNullOrWhiteSpace(msg.AutomationRunId))
                                    {
                                        var runRecord = await automationService.GetRunRecordAsync(automation.Id, msg.AutomationRunId!, processingCt);
                                        automationRetryAttempt = runRecord?.RetryAttempt ?? 0;
                                    }

                                    if (contractGovernance is not null)
                                        automationService.AttachRunContract(session, automation, msg.AutomationRunId, contractGovernance);
                                }
                            }

                            // ── Chat Command Processing ──────────────────────
                            var (handled, cmdResponse) = await commandProcessor.TryProcessCommandAsync(session, msg.Text, processingCt);
                            if (handled)
                            {
                                if (cmdResponse is not null)
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = cmdResponse,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }

                                if (automation is not null && automationService is not null)
                                {
                                    await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                    {
                                        VerificationStatus = AutomationVerificationStatuses.Blocked,
                                        VerificationSummary = "Automation execution was intercepted by a chat command.",
                                        InputTokens = session.TotalInputTokens - initialInputTokens,
                                        OutputTokens = session.TotalOutputTokens - initialOutputTokens,
                                        RetryAttempt = automationRetryAttempt
                                    }, processingCt);
                                }
                                continue; // Skip LLM completely
                            }

                            var mwContext = new MessageContext
                            {
                                ChannelId = msg.ChannelId,
                                SenderId = msg.SenderId,
                                Text = msg.Text,
                                MessageId = msg.MessageId,
                                SessionId = session.Id,
                                SessionInputTokens = session.TotalInputTokens,
                                SessionOutputTokens = session.TotalOutputTokens
                            };

                            var shouldProceed = await middlewarePipeline.ExecuteAsync(mwContext, processingCt);
                            if (!shouldProceed)
                            {
                                var shortCircuitText = mwContext.ShortCircuitResponse ?? "Request blocked.";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "assistant_message", shortCircuitText, msg.MessageId,
                                        processingCt);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = shortCircuitText,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }

                                if (automation is not null && automationService is not null)
                                {
                                    await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                    {
                                        VerificationStatus = AutomationVerificationStatuses.Blocked,
                                        VerificationSummary = shortCircuitText,
                                        InputTokens = session.TotalInputTokens - initialInputTokens,
                                        OutputTokens = session.TotalOutputTokens - initialOutputTokens,
                                        RetryAttempt = automationRetryAttempt
                                    }, processingCt);
                                }
                                continue;
                            }

                            var messageText = mwContext.Text;
                            if (!string.IsNullOrWhiteSpace(msg.MediaUrl) && !messageText.Contains("[IMAGE_URL:", StringComparison.Ordinal))
                            {
                                var marker = BuildMediaMarker(msg);
                                if (!string.IsNullOrWhiteSpace(marker))
                                    messageText = string.IsNullOrWhiteSpace(messageText) ? marker : $"{marker}\n{messageText}";
                            }
                            var useStreaming = msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

                            var approvalCallback = ToolApprovalCallbackFactory.Create(
                                config,
                                toolApprovalService,
                                approvalAuditStore,
                                operations,
                                session,
                                msg.ChannelId,
                                msg.SenderId,
                                async (request, preview, ct) =>
                                {
                                    if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                    {
                                        await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                        {
                                            Type = "tool_approval_required",
                                            ApprovalId = request.ApprovalId,
                                            ToolName = request.ToolName,
                                            ArgumentsPreview = preview,
                                            InReplyToMessageId = msg.MessageId,
                                            Text = string.IsNullOrWhiteSpace(request.Summary) ? "Tool approval required." : request.Summary
                                        }, ct);
                                        return;
                                    }

                                    var prompt = $"Tool approval required.\n" +
                                                 $"- id: {request.ApprovalId}\n" +
                                                 $"- tool: {request.ToolName}\n" +
                                                 $"{(string.IsNullOrWhiteSpace(request.Action) ? "" : $"- action: {request.Action}\n")}" +
                                                 $"{(string.IsNullOrWhiteSpace(request.Summary) ? "" : $"- summary: {request.Summary}\n")}" +
                                                 $"- args: {preview}\n\n" +
                                                 $"Reply with: /approve {request.ApprovalId} yes|no";

                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = prompt,
                                        ReplyToMessageId = msg.MessageId
                                    }, ct);
                                });

                            if (useStreaming)
                            {
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_start", "", msg.MessageId, processingCt);

                                AgentStreamEvent? doneEvent = null;
                                var originalResponseMode = session.ResponseMode;
                                var effectiveResponseMode = ResolveOperationalResponseMode(session, automation);
                                if (!string.IsNullOrWhiteSpace(effectiveResponseMode))
                                    session.ResponseMode = effectiveResponseMode!;

                                try
                                {
                                    await foreach (var evt in agentRuntime.RunStreamingAsync(
                                        session, messageText, processingCt, approvalCallback: approvalCallback))
                                    {
                                        if (string.Equals(evt.EnvelopeType, "assistant_done", StringComparison.Ordinal))
                                        {
                                            doneEvent = evt;
                                            continue;
                                        }

                                        await wsChannel.SendStreamEventAsync(
                                            msg.SenderId,
                                            evt,
                                            msg.MessageId,
                                            processingCt);
                                    }
                                }
                                finally
                                {
                                    session.ResponseMode = originalResponseMode;
                                }
                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

                                // Send verbose footer via stream for streaming sessions
                                if (session.VerboseMode)
                                {
                                    var streamInputDelta = session.TotalInputTokens - initialInputTokens;
                                    var streamOutputDelta = session.TotalOutputTokens - initialOutputTokens;
                                    var streamToolCalls = 0;
                                    for (var ti = session.History.Count - 1; ti >= 0; ti--)
                                    {
                                        var turn = session.History[ti];
                                        if (turn.ToolCalls is { Count: > 0 })
                                            streamToolCalls += turn.ToolCalls.Count;
                                        if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                                            break;
                                    }
                                    var verboseFooter = $"\n\n---\n{streamToolCalls} tool call(s) | {streamInputDelta} in / {streamOutputDelta} out tokens (this turn)";
                                    await wsChannel.SendStreamEventAsync(msg.SenderId, "text_delta", verboseFooter, msg.MessageId, processingCt);
                                }

                                if (doneEvent is AgentStreamEvent completedEvent)
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId,
                                        completedEvent,
                                        msg.MessageId,
                                        processingCt);
                                }

                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, processingCt);

                                if (automation is not null && automationService is not null)
                                {
                                    await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                    {
                                        LastDeliveredAtUtc = DateTimeOffset.UtcNow,
                                        InputTokens = session.TotalInputTokens - initialInputTokens,
                                        OutputTokens = session.TotalOutputTokens - initialOutputTokens,
                                        RetryAttempt = automationRetryAttempt
                                    }, processingCt);
                                }
                            }
                            else
                            {
                                // Send read receipt and typing indicator for bridged channels
                                bridgedAdapter = channelAdapters.TryGetValue(msg.ChannelId, out var adapter)
                                    ? adapter as IBridgedChannelControl : null;
                                var isSelfChat = bridgedAdapter?.SelfIds.Any(selfId =>
                                    string.Equals(selfId, msg.SenderId, StringComparison.Ordinal)) == true;

                                if (bridgedAdapter is not null && !isSelfChat)
                                {
                                    if (msg.MessageId is not null)
                                    {
                                        var receiptJid = msg.IsGroup ? msg.GroupId : msg.SenderId;
                                        var receiptParticipant = msg.IsGroup ? msg.SenderId : null;
                                        ObserveBackgroundTask(
                                            bridgedAdapter.SendReadReceiptAsync(msg.MessageId, receiptJid, receiptParticipant, msg.AccountId, processingCt).AsTask(),
                                            logger,
                                            "bridged read receipt");
                                    }
                                    ObserveBackgroundTask(
                                        bridgedAdapter.SendTypingAsync(conversationRecipientId, true, msg.AccountId, processingCt).AsTask(),
                                        logger,
                                        "bridged typing start");
                                    bridgedTypingStarted = true;
                                }

                                var originalResponseMode = session.ResponseMode;
                                var effectiveResponseMode = ResolveOperationalResponseMode(session, automation);
                                if (!string.IsNullOrWhiteSpace(effectiveResponseMode))
                                    session.ResponseMode = effectiveResponseMode!;

                                string responseText;
                                try
                                {
                                    responseText = await agentRuntime.RunAsync(session, messageText, processingCt, approvalCallback: approvalCallback);
                                }
                                finally
                                {
                                    session.ResponseMode = originalResponseMode;
                                }

                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

                                var inputTokenDelta = session.TotalInputTokens - initialInputTokens;
                                var outputTokenDelta = session.TotalOutputTokens - initialOutputTokens;
                                var suppressHeartbeatDelivery = heartbeatService.ShouldSuppressResult(msg.CronJobName, responseText);
                                if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                                    heartbeatService.RecordResult(session, responseText, suppressHeartbeatDelivery, inputTokenDelta, outputTokenDelta);

                                // Append verbose mode footer (tool calls and token delta)
                                if (session.VerboseMode)
                                {
                                    // Tool calls may be spread across multiple turns added during this run
                                    var turnToolCalls = 0;
                                    for (var ti = session.History.Count - 1; ti >= 0; ti--)
                                    {
                                        var turn = session.History[ti];
                                        if (turn.ToolCalls is { Count: > 0 })
                                            turnToolCalls += turn.ToolCalls.Count;
                                        // Stop once we reach a user turn (start of this interaction)
                                        if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                                            break;
                                    }
                                    responseText += $"\n\n---\n{turnToolCalls} tool call(s) | {inputTokenDelta} in / {outputTokenDelta} out tokens (this turn)";
                                }

                                // Append Usage Tracking string if configured
                                if (config.UsageFooter is "tokens")
                                    responseText += $"\n\n---\n↑ {session.TotalInputTokens} in / {session.TotalOutputTokens} out tokens";

                                if (!suppressHeartbeatDelivery)
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = responseText,
                                        SessionId = session.Id,
                                        CronJobName = msg.CronJobName,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }

                                if (automation is not null && automationService is not null)
                                {
                                    var signalSeverity = heartbeatService.IsManagedHeartbeatJob(msg.CronJobName)
                                        ? responseText.Trim() == "HEARTBEAT_OK"
                                            ? null
                                            : AutomationSignalSeverities.Alert
                                        : null;

                                    await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                    {
                                        DeliverySuppressed = suppressHeartbeatDelivery,
                                        LastDeliveredAtUtc = suppressHeartbeatDelivery ? null : DateTimeOffset.UtcNow,
                                        InputTokens = inputTokenDelta,
                                        OutputTokens = outputTokenDelta,
                                        RetryAttempt = automationRetryAttempt,
                                        SignalSeverity = signalSeverity
                                    }, processingCt);
                                }
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

                            if (automation is not null && automationService is not null)
                            {
                                await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                {
                                    ContractStatus = "cancelled",
                                    VerificationStatus = AutomationVerificationStatuses.Blocked,
                                    VerificationSummary = "Automation run was cancelled before completion.",
                                    InputTokens = session is null ? 0 : session.TotalInputTokens - initialInputTokens,
                                    OutputTokens = session is null ? 0 : session.TotalOutputTokens - initialOutputTokens,
                                    RetryAttempt = automationRetryAttempt
                                }, CancellationToken.None);
                            }
                            else if (session?.ContractPolicy is not null && contractGovernance is not null)
                            {
                                contractGovernance.AppendSnapshot(session, "cancelled");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                            {
                                var inputTokenDelta = session is null ? 0 : session.TotalInputTokens - initialInputTokens;
                                var outputTokenDelta = session is null ? 0 : session.TotalOutputTokens - initialOutputTokens;
                                heartbeatService.RecordError(session, ex, inputTokenDelta, outputTokenDelta);
                            }

                            if (session is not null)
                                logger.LogError(ex, "Internal error processing message for session {SessionId}", session.Id);
                            else
                                logger.LogError(ex, "Internal error processing message for channel {ChannelId} sender {SenderId}", msg.ChannelId, msg.SenderId);

                            if (automation is not null && automationService is not null)
                            {
                                var signalSeverity = heartbeatService.IsManagedHeartbeatJob(msg.CronJobName)
                                    ? AutomationSignalSeverities.Error
                                    : null;

                                await FinalizeAutomationRunAsync(new AutomationRunCompletion
                                {
                                    VerificationStatus = AutomationVerificationStatuses.Failed,
                                    VerificationSummary = $"Automation run failed: {ex.Message}",
                                    InputTokens = session is null ? 0 : session.TotalInputTokens - initialInputTokens,
                                    OutputTokens = session is null ? 0 : session.TotalOutputTokens - initialOutputTokens,
                                    RetryAttempt = automationRetryAttempt,
                                    SignalSeverity = signalSeverity
                                }, CancellationToken.None);
                            }

                            if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                                continue;

                            try
                            {
                                const string errorText = "Internal error.";
                                if (msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    await wsChannel.SendStreamEventAsync(
                                        msg.SenderId, "error", errorText, msg.MessageId,
                                        processingCt);
                                        
                                    await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_stop", "", msg.MessageId, processingCt);
                                }
                                else
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = errorText,
                                        Subject = msg.Subject,
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                }
                            }
                            catch { /* Best effort */ }
                        }
                        finally
                        {
                            if (bridgedAdapter is not null && bridgedTypingStarted)
                            {
                                try
                                {
                                    await bridgedAdapter.SendTypingAsync(conversationRecipientId, false, msg.AccountId, CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Background bridged typing stop failed");
                                }
                            }

                            cronScheduler?.MarkJobCompleted(msg.CronJobName);
                            automationService?.MarkRunCompleted(msg.CronJobName);

                            if (sessionLock is not null)
                                await sessionLock.DisposeAsync();
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

                        const int maxDeliveryAttempts = 2;
                        for (var attempt = 1; attempt <= maxDeliveryAttempts; attempt++)
                        {
                            try
                            {
                                await adapter.SendAsync(outbound, lifetime.ApplicationStopping);
                                if (heartbeatService.IsManagedHeartbeatJob(outbound.CronJobName))
                                    heartbeatService.RecordDeliverySucceeded(outbound.SessionId);
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

    private static CancellationTokenSource? CreateProcessingCts(CancellationToken requestCancellation, CancellationToken appStopping)
    {
        if (!requestCancellation.CanBeCanceled || requestCancellation == appStopping)
            return null;

        return CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, appStopping);
    }

    private static string? ResolveOperationalResponseMode(Session session, AutomationDefinition? automation)
    {
        if (automation is not null)
        {
            if (string.Equals(automation.ResponseMode, SessionResponseModes.Full, StringComparison.OrdinalIgnoreCase))
                return SessionResponseModes.Full;

            return SessionResponseModes.ConciseOps;
        }

        if (session.ContractPolicy is not null &&
            string.Equals(session.ResponseMode, SessionResponseModes.Default, StringComparison.OrdinalIgnoreCase))
        {
            return SessionResponseModes.ConciseOps;
        }

        return null;
    }

    private static void ObserveBackgroundTask(Task task, ILogger logger, string operation)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    var (log, op) = ((ILogger, string))state!;
                    log.LogWarning(completed.Exception.GetBaseException(), "Background {Operation} failed", op);
                }
            },
            state: (logger, operation),
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default);
    }

    private static string ResolveConversationRecipientId(InboundMessage msg)
        => msg.IsGroup && !string.IsNullOrWhiteSpace(msg.GroupId)
            ? msg.GroupId!
            : msg.SenderId;

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

    private static string? BuildMediaMarker(InboundMessage message)
        => (message.MediaType ?? "").ToLowerInvariant() switch
        {
            "image" => $"[IMAGE_URL:{message.MediaUrl}]",
            "audio" => $"[AUDIO_URL:{message.MediaUrl}]",
            "video" => $"[VIDEO_URL:{message.MediaUrl}]",
            "document" or "file" => $"[FILE_URL:{message.MediaUrl}]",
            _ => null
        };
}
