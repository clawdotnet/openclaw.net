using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
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
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

internal sealed class GatewayInboundMessageWorker
{
    public void Start(
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
        ContractGovernanceService? contractGovernance,
        GovernanceLedgerService? governanceLedger,
        AudioTranscriptionService? audioTranscriptionService = null,
        Background.BackgroundExecutionLimiter? backgroundLimiter = null,
        MediaCacheStore? mediaCache = null,
        SessionAbortRegistry? abortRegistry = null)
    {
        _ = isNonLoopbackBind;
        _ = sessionLocks;
        _ = lockLastUsed;

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
                        var historyCountBefore = 0;
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
                                    if (governanceLedger is not null)
                                    {
                                        await governanceLedger.TryRecordApprovalDecisionAsync(
                                            decisionOutcome.Request,
                                            msg.Approved.Value,
                                            GovernanceLedgerSources.ToolApproval,
                                            msg.SenderId,
                                            msg.ChannelId,
                                            msg.SenderId,
                                            lifetime.ApplicationStopping);
                                    }
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
                                            if (governanceLedger is not null)
                                            {
                                                await governanceLedger.TryRecordApprovalDecisionAsync(
                                                    decisionOutcome.Request,
                                                    approved,
                                                    GovernanceLedgerSources.ToolApproval,
                                                    msg.SenderId,
                                                    msg.ChannelId,
                                                    msg.SenderId,
                                                    lifetime.ApplicationStopping);
                                            }
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

                            var policy = "open";
                            if (msg.ChannelId == "sms") policy = config.Channels.Sms.DmPolicy;
                            if (msg.ChannelId == "telegram") policy = config.Channels.Telegram.DmPolicy;
                            if (msg.ChannelId == "whatsapp") policy = config.Channels.WhatsApp.DmPolicy;
                            if (msg.ChannelId == "teams") policy = config.Channels.Teams.DmPolicy;
                            if (msg.ChannelId == "slack") policy = config.Channels.Slack.DmPolicy;
                            if (msg.ChannelId == "discord") policy = config.Channels.Discord.DmPolicy;
                            if (msg.ChannelId == "signal") policy = config.Channels.Signal.DmPolicy;

                            if (policy is "closed")
                                continue;

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

                                continue;
                            }

                            var resolvedRoute = routeResolver?.Resolve(msg.ChannelId, msg.SenderId);

                            session = msg.SessionId is not null
                                ? await sessionManager.GetOrCreateByIdAsync(msg.SessionId, msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping)
                                : await sessionManager.GetOrCreateAsync(msg.ChannelId, conversationRecipientId, lifetime.ApplicationStopping);
                            if (session is null)
                                throw new InvalidOperationException("Session manager returned null session.");

                            // Abort intercept: handle /stop, /cancel, /abort BEFORE acquiring the session lock
                            // so the abort does not wait for the in-flight execution to release the lock.
                            if (!msg.IsSystem && abortRegistry is not null && IsAbortCommand(msg.Text))
                            {
                                if (abortRegistry.TryAbort(session.Id))
                                {
                                    await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                    {
                                        ChannelId = msg.ChannelId,
                                        RecipientId = conversationRecipientId,
                                        AccountId = msg.AccountId,
                                        Text = "Stopped.",
                                        ReplyToMessageId = msg.MessageId
                                    }, processingCt);
                                    continue;
                                }
                                // No active execution — fall through to the command processor below
                                // (which will return "There is no active execution to stop.").
                            }

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

                            if (!string.IsNullOrWhiteSpace(msg.AuthenticatedUserId) &&
                                !string.Equals(session.AuthenticatedUserId, msg.AuthenticatedUserId, StringComparison.Ordinal))
                            {
                                session.AuthenticatedUserId = msg.AuthenticatedUserId;
                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                            }

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
                                continue;
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
                            var mediaMarker = BuildMediaMarker(msg);
                            var hasCurrentMediaMarker = ContainsExactMediaMarker(messageText, mediaMarker);
                            var transcriptionProviderName = AudioTranscriptionService.ResolveProviderName(config.Multimodal.Transcription.Provider);
                            var audioTranscriptionSucceeded = false;
                            if (AudioTranscriptionService.IsAudioMessage(msg))
                            {
                                var transcriptionStopwatch = Stopwatch.StartNew();
                                try
                                {
                                    if (audioTranscriptionService is null)
                                        throw new InvalidOperationException("Voice transcription is unavailable in this runtime.");

                                    var transcription = await audioTranscriptionService.TranscribeAsync(msg, processingCt);
                                    transcriptionStopwatch.Stop();
                                    audioTranscriptionSucceeded = true;
                                    if (!config.Multimodal.Transcription.InjectAudioMarker)
                                        messageText = RemoveExactMediaMarker(messageText, mediaMarker);
                                    messageText = VoiceMemoTranscriptionText.PrependTranscript(messageText, transcription);
                                    AppendVoiceTranscriptionEvent(
                                        operations,
                                        session,
                                        msg,
                                        action: "transcription_succeeded",
                                        severity: "info",
                                        summary: "Voice memo transcribed.",
                                        provider: transcription.Provider,
                                        reason: null,
                                        elapsed: transcriptionStopwatch.Elapsed,
                                        sizeBytes: transcription.SizeBytes,
                                        mimeType: transcription.MimeType ?? msg.MediaMimeType);
                                }
                                catch (Exception ex) when (!processingCt.IsCancellationRequested)
                                {
                                    transcriptionStopwatch.Stop();
                                    var reason = VoiceMemoTranscriptionText.FailureReason(ex);
                                    logger.LogWarning(ex, "Voice memo transcription failed for {ChannelId}:{SenderId}: {Reason}", msg.ChannelId, msg.SenderId, reason);
                                    AppendVoiceTranscriptionEvent(
                                        operations,
                                        session,
                                        msg,
                                        action: "transcription_failed",
                                        severity: "warning",
                                        summary: $"Voice memo transcription unavailable: {reason}.",
                                        provider: transcriptionProviderName,
                                        reason: reason,
                                        elapsed: transcriptionStopwatch.Elapsed,
                                        sizeBytes: null,
                                        mimeType: msg.MediaMimeType);
                                    if (!AudioTranscriptionService.ShouldDegrade(config.Multimodal.Transcription.FailureMode))
                                        throw;

                                    messageText = VoiceMemoTranscriptionText.AppendUnavailable(messageText, reason);
                                }
                            }

                            var shouldInjectMediaMarker = !AudioTranscriptionService.IsAudioMessage(msg)
                                || !audioTranscriptionSucceeded
                                || config.Multimodal.Transcription.InjectAudioMarker;
                            if (shouldInjectMediaMarker &&
                                !string.IsNullOrWhiteSpace(mediaMarker) &&
                                !hasCurrentMediaMarker)
                            {
                                messageText = string.IsNullOrWhiteSpace(messageText) ? mediaMarker : $"{mediaMarker}\n{messageText}";
                            }
                            // Resolve [FILE_URL:/media/{id}] markers written into history by a previous
                            // turn back to [FILE_PATH:{diskPath}] so the agent can use read_file directly.
                            if (mediaCache is not null && messageText.Contains("[FILE_URL:/media/", StringComparison.Ordinal))
                            {
                                messageText = await ResolveFileUrlMarkersAsync(messageText, mediaCache, processingCt);
                            }

                            var useStreaming = ShouldUseStreaming(msg, wsChannel);

                            var approvalCallback = ToolApprovalCallbackFactory.Create(
                                config,
                                toolApprovalService,
                                approvalAuditStore,
                                operations,
                                session,
                                msg.ChannelId,
                                msg.SenderId,
                                governanceLedger,
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

                            // Register session cancellation source so /stop can abort in-flight execution.
                            var abortCts = abortRegistry?.Register(session.Id, processingCt);
                            var executionCt = abortCts?.Token ?? processingCt;

                            if (useStreaming)
                            {
                                await wsChannel.SendStreamEventAsync(msg.SenderId, "typing_start", "", msg.MessageId, processingCt);

                                AgentStreamEvent? doneEvent = null;
                                var originalResponseMode = session.ResponseMode;
                                var effectiveResponseMode = ResolveOperationalResponseMode(session, automation);
                                if (!string.IsNullOrWhiteSpace(effectiveResponseMode))
                                    session.ResponseMode = effectiveResponseMode!;

                                var streamHistoryCountBefore = session.History.Count;
                                try
                                {
                                    var streamLimiterReleaser = backgroundLimiter is not null
                                        ? await backgroundLimiter.TryAcquireAsync(msg, processingCt)
                                        : null;
                                    using var streamLimiterScope = streamLimiterReleaser is { } acquiredStreamLimiter
                                        ? (IDisposable)acquiredStreamLimiter
                                        : null;

                                    if (streamLimiterReleaser is null && Background.BackgroundExecutionLimiter.IsBackgroundContinuation(msg))
                                    {
                                        RequeueBackgroundContinuation(
                                            pipeline,
                                            msg,
                                            TimeSpan.FromSeconds(1),
                                            lifetime.ApplicationStopping,
                                            logger);
                                        await wsChannel.SendStreamEventAsync(
                                            msg.SenderId, "text_delta",
                                            "\n\nBackground execution concurrency limit reached. Turn will be retried.",
                                            msg.MessageId, processingCt);
                                        continue;
                                    }

                                    await foreach (var evt in agentRuntime.RunStreamingAsync(
                                        session, messageText, executionCt, approvalCallback: approvalCallback))
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

                                // Upload files written by write_file by scanning history (same as non-streaming path).
                                var streamFileUploads = new List<StoredMediaAsset>();
                                if (mediaCache is not null)
                                {
                                    for (var hi = streamHistoryCountBefore; hi < session.History.Count; hi++)
                                    {
                                        var hturn = session.History[hi];
                                        if (hturn.ToolCalls is null) continue;
                                        foreach (var call in hturn.ToolCalls)
                                        {
                                            if (call.Result is null || !call.Result.Contains("[FILE_PATH:", StringComparison.Ordinal))
                                                continue;
                                            var (fileMarkers, _) = MediaMarkerProtocol.Extract(call.Result);
                                            foreach (var marker in fileMarkers.Where(m => m.Kind == MediaMarkerKind.FilePath))
                                            {
                                                var fileAsset = await TryUploadFilePathAsync(marker.Value, config, mediaCache, processingCt);
                                                if (fileAsset is not null)
                                                    streamFileUploads.Add(fileAsset);
                                            }
                                        }
                                    }
                                }

                                // Inject FILE_URL markers into the last assistant ChatTurn BEFORE persisting
                                // so history replays show download links.
                                if (streamFileUploads.Count > 0)
                                    InjectFileUrlMarkersIntoHistory(session, streamFileUploads);

                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

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

                                // Deliver file_attachment envelopes for files uploaded during this streaming turn.
                                foreach (var fileAsset in streamFileUploads)
                                {
                                    await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                    {
                                        Type = "file_attachment",
                                        Text = fileAsset.FileName,
                                        FileUrl = $"/media/{fileAsset.Id}",
                                        FileName = fileAsset.FileName,
                                        MimeType = fileAsset.MediaType,
                                        FileSizeBytes = fileAsset.SizeBytes,
                                        InReplyToMessageId = msg.MessageId
                                    }, processingCt);
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

                                var responseText = string.Empty;

                                AgentTurnResult turnResult;
                                try
                                {
                                    var limiterReleaser = backgroundLimiter is not null
                                        ? await backgroundLimiter.TryAcquireAsync(msg, processingCt)
                                        : null;
                                    using var limiterScope = limiterReleaser is { } acquiredLimiter
                                        ? (IDisposable)acquiredLimiter
                                        : null;

                                    if (limiterReleaser is null && Background.BackgroundExecutionLimiter.IsBackgroundContinuation(msg))
                                    {
                                        RequeueBackgroundContinuation(
                                            pipeline,
                                            msg,
                                            TimeSpan.FromSeconds(1),
                                            lifetime.ApplicationStopping,
                                            logger);
                                        responseText = "Background execution concurrency limit reached. Turn will be retried.";
                                        continue;
                                    }

                                    turnResult = await agentRuntime.RunTurnAsync(session, messageText, processingCt, approvalCallback: approvalCallback);
                                    responseText = turnResult.Text;
                                }
                                finally
                                {
                                    session.ResponseMode = originalResponseMode;
                                }

                                // Upload files written by write_file tool results and build asset list
                                // BEFORE persisting, so FILE_URL markers are injected into history for replay.
                                var nonStreamFileUploads = new List<StoredMediaAsset>();
                                if (mediaCache is not null)
                                {
                                    for (var hi = historyCountBefore; hi < session.History.Count; hi++)
                                    {
                                        var hturn = session.History[hi];
                                        if (hturn.ToolCalls is null) continue;
                                        foreach (var call in hturn.ToolCalls)
                                        {
                                            if (call.Result is null || !call.Result.Contains("[FILE_PATH:", StringComparison.Ordinal))
                                                continue;
                                            var (fileMarkers, _) = MediaMarkerProtocol.Extract(call.Result);
                                            foreach (var marker in fileMarkers.Where(m => m.Kind == MediaMarkerKind.FilePath))
                                            {
                                                var fileAsset = await TryUploadFilePathAsync(marker.Value, config, mediaCache, processingCt);
                                                if (fileAsset is not null)
                                                    nonStreamFileUploads.Add(fileAsset);
                                            }
                                        }
                                    }
                                }

                                // Inject FILE_URL markers into history and append download links to responseText.
                                if (nonStreamFileUploads.Count > 0)
                                {
                                    InjectFileUrlMarkersIntoHistory(session, nonStreamFileUploads);
                                    // WebSocket envelope clients get a dedicated file_attachment envelope;
                                    // appending FILE_URL to responseText would produce a duplicate link.
                                    if (!(msg.ChannelId == "websocket" && wsChannel.IsClientUsingEnvelopes(msg.SenderId)))
                                        responseText += BuildFileUrlSuffix(nonStreamFileUploads);
                                }
                                await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);

                                // Background continuation
                                if (turnResult.ShouldContinue && config.BackgroundExecution.Enabled)
                                {
                                    // Lazy-init BackgroundRun on first continuation
                                    session.BackgroundRun ??= new BackgroundRunMetadata
                                    {
                                        RunId = $"bg_{session.Id}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                                        StartedAtUtc = DateTimeOffset.UtcNow,
                                        TokenBudget = config.BackgroundExecution.DefaultTokenBudget,
                                        MaxContinuationTurns = config.BackgroundExecution.MaxContinuationTurns
                                    };

                                    session.RunState = SessionRunState.Continuing;
                                    session.BackgroundRun.ContinuationCount++;
                                    session.BackgroundRun.ContinuationSequence++;
                                    session.BackgroundRun.LastContinuedAtUtc = DateTimeOffset.UtcNow;
                                    session.BackgroundRun.LastStopReason = turnResult.StopReason.ToString();

                                    // Check against MaxContinuationTurns cap
                                    var maxContinuationTurns = session.BackgroundRun.MaxContinuationTurns > 0
                                        ? session.BackgroundRun.MaxContinuationTurns
                                        : config.BackgroundExecution.MaxContinuationTurns;
                                    if (session.BackgroundRun.ContinuationSequence >= maxContinuationTurns)
                                    {
                                        session.RunState = SessionRunState.BudgetLimited;
                                        session.BackgroundRun.LastStopReason = "MaxContinuationTurnsReached";
                                        await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);
                                    }
                                    else
                                    {
                                        session.RunState = SessionRunState.Continuing;
                                        await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);

                                        await pipeline.InboundWriter.WriteAsync(new InboundMessage
                                        {
                                            ChannelId = msg.ChannelId,
                                            SenderId = msg.SenderId,
                                            AccountId = msg.AccountId,
                                            SessionId = session.Id,
                                            Text = turnResult.ContinuePrompt ?? "Continue working toward the active goal.",
                                            Type = BackgroundMessageTypes.AutoContinue,
                                            IsSystem = true,
                                            BackgroundRunId = session.BackgroundRun.RunId,
                                            BackgroundContinuationSequence = session.BackgroundRun.ContinuationSequence
                                        }, lifetime.ApplicationStopping);
                                    }
                                }

                                // Lifecycle notifications for background task terminal states
                                if (session.BackgroundRun is not null && !turnResult.ShouldContinue)
                                {
                                    // Map StopReason to final SessionRunState and persist
                                    session.RunState = turnResult.StopReason switch
                                    {
                                        AgentTurnStopReason.Completed => SessionRunState.Completed,
                                        AgentTurnStopReason.Blocked => SessionRunState.Blocked,
                                        AgentTurnStopReason.BudgetLimited => SessionRunState.BudgetLimited,
                                        AgentTurnStopReason.Failed => SessionRunState.Failed,
                                        _ => session.RunState
                                    };
                                    await sessionManager.PersistAsync(session, processingCt, sessionLockHeld: true);

                                    var notifyText = turnResult.StopReason switch
                                    {
                                        AgentTurnStopReason.Completed => $"Background task completed: {turnResult.Text}",
                                        AgentTurnStopReason.Blocked => $"Background task blocked: {turnResult.Text}",
                                        AgentTurnStopReason.BudgetLimited => $"Background task paused: budget reached — {turnResult.Text}",
                                        AgentTurnStopReason.Failed => $"Background task failed: {turnResult.Text}",
                                        _ => null
                                    };

                                    if (notifyText is not null)
                                    {
                                        var shouldNotify = turnResult.StopReason switch
                                        {
                                            AgentTurnStopReason.Completed => config.BackgroundExecution.NotifyOnCompletion,
                                            AgentTurnStopReason.Blocked => config.BackgroundExecution.NotifyOnBlocked,
                                            AgentTurnStopReason.BudgetLimited => config.BackgroundExecution.NotifyOnBudgetLimited,
                                            _ => false
                                        };

                                        if (shouldNotify)
                                        {
                                            await pipeline.OutboundWriter.WriteAsync(new OutboundMessage
                                            {
                                                ChannelId = msg.ChannelId,
                                                RecipientId = conversationRecipientId,
                                                AccountId = msg.AccountId,
                                                Text = notifyText,
                                                SessionId = session.Id,
                                                ReplyToMessageId = msg.MessageId
                                            }, lifetime.ApplicationStopping);
                                        }
                                    }
                                }

                                if (learningService is not null)
                                    await learningService.ObserveSessionAsync(session, processingCt);

                                var inputTokenDelta = session.TotalInputTokens - initialInputTokens;
                                var outputTokenDelta = session.TotalOutputTokens - initialOutputTokens;
                                var suppressHeartbeatDelivery = heartbeatService.ShouldSuppressResult(msg.CronJobName, responseText);
                                if (heartbeatService.IsManagedHeartbeatJob(msg.CronJobName))
                                    heartbeatService.RecordResult(session, responseText, suppressHeartbeatDelivery, inputTokenDelta, outputTokenDelta);

                                if (session.VerboseMode)
                                {
                                    var turnToolCalls = 0;
                                    for (var ti = session.History.Count - 1; ti >= 0; ti--)
                                    {
                                        var turn = session.History[ti];
                                        if (turn.ToolCalls is { Count: > 0 })
                                            turnToolCalls += turn.ToolCalls.Count;
                                        if (string.Equals(turn.Role, "user", StringComparison.Ordinal))
                                            break;
                                    }
                                    responseText += $"\n\n---\n{turnToolCalls} tool call(s) | {inputTokenDelta} in / {outputTokenDelta} out tokens (this turn)";
                                }

                                if (config.UsageFooter is "tokens")
                                    responseText += $"\n\n---\n↑ {session.TotalInputTokens} in / {session.TotalOutputTokens} out tokens";

                                // Deliver file_attachment envelopes for WebSocket envelope clients.
                                if (nonStreamFileUploads.Count > 0 &&
                                    msg.ChannelId == "websocket" &&
                                    wsChannel.IsClientUsingEnvelopes(msg.SenderId))
                                {
                                    foreach (var fileAsset in nonStreamFileUploads)
                                    {
                                        await wsChannel.SendEnvelopeAsync(msg.SenderId, new WsServerEnvelope
                                        {
                                            Type = "file_attachment",
                                            Text = fileAsset.FileName,
                                            FileUrl = $"/media/{fileAsset.Id}",
                                            FileName = fileAsset.FileName,
                                            MimeType = fileAsset.MediaType,
                                            FileSizeBytes = fileAsset.SizeBytes,
                                            InReplyToMessageId = msg.MessageId
                                        }, processingCt);
                                    }
                                }

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
                            catch
                            {
                            }
                        }
                        finally
                        {
                            if (session is not null)
                                abortRegistry?.Unregister(session.Id);

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

    private static CancellationTokenSource? CreateProcessingCts(CancellationToken requestCancellation, CancellationToken appStopping)
    {
        if (!requestCancellation.CanBeCanceled || requestCancellation == appStopping)
            return null;

        return CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, appStopping);
    }

    private static bool IsAbortCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return string.Equals(t, "/stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "/cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "/abort", StringComparison.OrdinalIgnoreCase);
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

    internal static bool ShouldUseStreaming(InboundMessage msg, WebSocketChannel wsChannel)
        => !Background.BackgroundExecutionLimiter.IsBackgroundContinuation(msg)
        && string.Equals(msg.ChannelId, "websocket", StringComparison.Ordinal)
        && wsChannel.IsClientUsingEnvelopes(msg.SenderId);

    internal static void RequeueBackgroundContinuation(
        MessagePipeline pipeline,
        InboundMessage msg,
        TimeSpan delay,
        CancellationToken ct,
        ILogger logger)
    {
        if (!Background.BackgroundExecutionLimiter.IsBackgroundContinuation(msg))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);

                await pipeline.InboundWriter.WriteAsync(msg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Gateway is stopping; do not requeue background work.
            }
            catch (ChannelClosedException ex)
            {
                logger.LogWarning(ex, "Failed to requeue background continuation for session {SessionId}", msg.SessionId);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Failed to requeue background continuation for session {SessionId}", msg.SessionId);
            }
        }, CancellationToken.None);
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

    private static bool ContainsExactMediaMarker(string text, string? marker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
            return false;

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.Equals(line.Trim(), marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string RemoveExactMediaMarker(string text, string? marker)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(marker))
            return text;

        var changed = false;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (string.Equals(line.Trim(), marker, StringComparison.Ordinal))
            {
                changed = true;
                continue;
            }

            kept.Add(line);
        }

        return changed ? string.Join("\n", kept).Trim() : text;
    }

    private static void AppendVoiceTranscriptionEvent(
        RuntimeOperationsState operations,
        Session session,
        InboundMessage message,
        string action,
        string severity,
        string summary,
        string? provider,
        string? reason,
        TimeSpan elapsed,
        long? sizeBytes,
        string? mimeType)
    {
        var metadata = new Dictionary<string, string>
        {
            ["provider"] = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider,
            ["elapsedMs"] = ((long)elapsed.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(reason))
            metadata["reason"] = reason;
        if (!string.IsNullOrWhiteSpace(mimeType))
            metadata["mimeType"] = mimeType;
        if (sizeBytes.HasValue)
            metadata["sizeBytes"] = sizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = session.Id,
            ChannelId = message.ChannelId,
            SenderId = message.SenderId,
            Component = "voice_transcription",
            Action = action,
            Severity = severity,
            Summary = summary,
            Metadata = metadata
        });
    }

    // Resolves [FILE_URL:/media/{id}] markers in an inbound message back to [FILE_PATH:{diskPath}]
    // so the agent can read the file directly via the read_file tool instead of an HTTP path.
    private static async Task<string> ResolveFileUrlMarkersAsync(string text, MediaCacheStore mediaCache, CancellationToken ct)
    {
        if (!text.Contains("[FILE_URL:/media/", StringComparison.Ordinal))
            return text;

        var result = new System.Text.StringBuilder(text.Length);
        const string prefix = "[FILE_URL:/media/";
        var searchFrom = 0;

        while (true)
        {
            var startIdx = text.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                result.Append(text, searchFrom, text.Length - searchFrom);
                break;
            }

            result.Append(text, searchFrom, startIdx - searchFrom);

            var closeIdx = text.IndexOf(']', startIdx + prefix.Length);
            if (closeIdx < 0)
            {
                result.Append(text, startIdx, text.Length - startIdx);
                break;
            }

            // Extract just the ID portion (strip any |fileName suffix).
            var rawId = text.Substring(startIdx + prefix.Length, closeIdx - (startIdx + prefix.Length));
            var pipeIdx = rawId.IndexOf('|', StringComparison.Ordinal);
            var mediaId = pipeIdx >= 0 ? rawId[..pipeIdx] : rawId;

            // Only resolve IDs that look like our generated ones (no path separators or extensions).
            if (!mediaId.Contains('/') && !mediaId.Contains('\\') && !mediaId.Contains('.'))
            {
                var asset = await mediaCache.GetAsync(mediaId, ct);
                if (asset is not null && File.Exists(asset.Path))
                {
                    result.Append($"[FILE_PATH:{asset.Path}]");
                    searchFrom = closeIdx + 1;
                    continue;
                }
            }

            // Leave unchanged if not resolvable.
            result.Append(text, startIdx, closeIdx + 1 - startIdx);
            searchFrom = closeIdx + 1;
        }

        return result.ToString();
    }

    // Reads a file from disk (validating it is within an allowed write root) and stores it
    // in the media cache, returning the stored asset. Returns null if the file does not
    // exist or the path is outside all allowed write roots.
    private static async Task<StoredMediaAsset?> TryUploadFilePathAsync(
        string filePath,
        GatewayConfig config,
        MediaCacheStore mediaCache,
        CancellationToken ct)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);

            var roots = config.Tooling.AllowedWriteRoots;
            if (roots.Length == 0)
                return null;

            var pathAllowed = false;
            foreach (var root in roots)
            {
                if (root == "*")
                {
                    pathAllowed = true;
                    break;
                }

                var fullRoot = Path.GetFullPath(root);
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                    (fullPath.Length == fullRoot.Length ||
                     fullPath[fullRoot.Length] == Path.DirectorySeparatorChar ||
                     fullPath[fullRoot.Length] == Path.AltDirectorySeparatorChar))
                {
                    pathAllowed = true;
                    break;
                }
            }

            if (!pathAllowed || !File.Exists(fullPath))
                return null;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var fileName = Path.GetFileName(fullPath);
            var mimeType = GuessMimeTypeFromExtension(Path.GetExtension(fullPath));
            return await mediaCache.SaveAsync(bytes.AsMemory(), mimeType, fileName, ct);
        }
        catch
        {
            return null;
        }
    }

    private static string GuessMimeTypeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".css"            => "text/css",
            ".js"             => "application/javascript",
            ".json"           => "application/json",
            ".xml"            => "application/xml",
            ".txt"            => "text/plain",
            ".md"             => "text/markdown",
            ".csv"            => "text/csv",
            ".py"             => "text/x-python",
            ".sh"             => "text/x-sh",
            ".zip"            => "application/zip",
            ".tar"            => "application/x-tar",
            ".gz"             => "application/gzip",
            ".pdf"            => "application/pdf",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".svg"            => "image/svg+xml",
            ".webp"           => "image/webp",
            ".mp3"            => "audio/mpeg",
            ".wav"            => "audio/wav",
            ".mp4"            => "video/mp4",
            _                 => "application/octet-stream"
        };

    // Builds a newline-prefixed block of [FILE_URL:] markers to append to response text.
    // Format: [FILE_URL:/media/{id}|{fileName}] so preprocessMediaMarkers can render a named link.
    private static string BuildFileUrlSuffix(List<StoredMediaAsset> assets)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in assets)
        {
            var nameSuffix = string.IsNullOrWhiteSpace(a.FileName) ? "" : $"|{a.FileName}";
            sb.Append($"\n[FILE_URL:/media/{a.Id}{nameSuffix}]");
        }
        return sb.ToString();
    }

    // Appends [FILE_URL:] markers to the last assistant ChatTurn in history so that
    // history replays show download links via preprocessMediaMarkers on the front-end.
    private static void InjectFileUrlMarkersIntoHistory(Session session, List<StoredMediaAsset> assets)
    {
        if (assets.Count == 0) return;
        for (var i = session.History.Count - 1; i >= 0; i--)
        {
            var turn = session.History[i];
            if (!string.Equals(turn.Role, "assistant", StringComparison.Ordinal))
                continue;
            // Skip the [tool_use] placeholder — target the text response turn.
            if (string.Equals(turn.Content, "[tool_use]", StringComparison.Ordinal))
                continue;

            session.History[i] = turn with { Content = turn.Content + BuildFileUrlSuffix(assets) };
            return;
        }
    }
}
