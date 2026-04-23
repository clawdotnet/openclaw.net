using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AutomationRunCoordinator : IAutomationRunDispatcher
{
    internal const int RunHistoryRetention = 200;
    internal const int QuarantineFailureThreshold = 3;
    internal static readonly TimeSpan StuckThreshold = TimeSpan.FromHours(6);

    private readonly IAutomationStore _store;
    private readonly ContractGovernanceService _contractGovernance;
    private readonly ILogger<AutomationRunCoordinator> _logger;

    public AutomationRunCoordinator(
        IAutomationStore store,
        ContractGovernanceService contractGovernance,
        ILogger<AutomationRunCoordinator> logger)
    {
        _store = store;
        _contractGovernance = contractGovernance;
        _logger = logger;
    }

    public async ValueTask<InboundMessage?> PrepareDispatchAsync(AutomationDispatchRequest request, CancellationToken ct)
    {
        var existingState = await _store.GetRunStateAsync(request.AutomationId, ct);
        if (existingState?.QuarantinedAtUtc is not null
            && request.TriggerSource is AutomationRunTriggerSources.Schedule or AutomationRunTriggerSources.Retry)
        {
            _logger.LogWarning(
                "Skipping automation '{AutomationId}' because it is quarantined.",
                request.AutomationId);
            return null;
        }

        var runId = $"ar_{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        var preview = BuildMessagePreview(request.Prompt);
        var heartbeat = IsHeartbeat(request.AutomationId, request.TriggerSource);

        var queuedState = new AutomationRunState
        {
            AutomationId = request.AutomationId,
            Outcome = AutomationRunStatusMapper.DeriveOutcome(
                request.AutomationId,
                AutomationLifecycleStates.Queued,
                AutomationVerificationStatuses.NotRun,
                existingState?.SignalSeverity),
            LifecycleState = AutomationLifecycleStates.Queued,
            VerificationStatus = AutomationVerificationStatuses.NotRun,
            HealthState = AutomationRunStatusMapper.DeriveHealthState(
                AutomationLifecycleStates.Queued,
                AutomationVerificationStatuses.NotRun,
                existingState?.QuarantinedAtUtc),
            LastRunAtUtc = now,
            LastCompletedAtUtc = existingState?.LastCompletedAtUtc,
            LastDeliveredAtUtc = existingState?.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = existingState?.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = existingState?.QuarantinedAtUtc,
            NextRetryAtUtc = null,
            DeliverySuppressed = false,
            InputTokens = 0,
            OutputTokens = 0,
            FailureStreak = existingState?.FailureStreak ?? 0,
            UnverifiedStreak = existingState?.UnverifiedStreak ?? 0,
            NextRetryAttempt = null,
            LastRunId = runId,
            SessionId = request.SessionId,
            MessagePreview = preview,
            VerificationSummary = null,
            QuarantineReason = existingState?.QuarantineReason,
            SignalSeverity = existingState?.SignalSeverity
        };

        await _store.SaveRunStateAsync(queuedState, ct);

        if (!heartbeat)
        {
            await _store.SaveRunRecordAsync(new AutomationRunRecord
            {
                RunId = runId,
                AutomationId = request.AutomationId,
                TriggerSource = request.TriggerSource,
                LifecycleState = AutomationLifecycleStates.Queued,
                VerificationStatus = AutomationVerificationStatuses.NotRun,
                ReplayOfRunId = request.ReplayOfRunId,
                RetryAttempt = request.RetryAttempt,
                SessionId = request.SessionId,
                MessagePreview = preview,
                VerificationSummary = null,
                VerificationChecks = [],
                StartedAtUtc = now
            }, ct);
            await _store.PruneRunRecordsAsync(request.AutomationId, RunHistoryRetention, ct);
        }

        return new InboundMessage
        {
            IsSystem = true,
            SessionId = request.SessionId,
            CronJobName = request.AutomationId,
            AutomationRunId = runId,
            AutomationTriggerSource = request.TriggerSource,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Subject = request.Subject,
            Text = request.Prompt
        };
    }

    public ValueTask<IReadOnlyList<AutomationRunRecord>> ListRunRecordsAsync(string automationId, int limit, CancellationToken ct)
        => _store.ListRunRecordsAsync(automationId, limit, ct);

    public ValueTask<AutomationRunRecord?> GetRunRecordAsync(string automationId, string runId, CancellationToken ct)
        => _store.GetRunRecordAsync(automationId, runId, ct);

    public async ValueTask MarkRunningAsync(AutomationDefinition automation, InboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.AutomationRunId))
            return;

        var existing = await _store.GetRunStateAsync(automation.Id, ct);
        var runningState = new AutomationRunState
        {
            AutomationId = automation.Id,
            Outcome = AutomationRunStatusMapper.DeriveOutcome(
                automation.Id,
                AutomationLifecycleStates.Running,
                AutomationVerificationStatuses.NotRun,
                existing?.SignalSeverity),
            LifecycleState = AutomationLifecycleStates.Running,
            VerificationStatus = AutomationVerificationStatuses.NotRun,
            HealthState = AutomationRunStatusMapper.DeriveHealthState(
                AutomationLifecycleStates.Running,
                AutomationVerificationStatuses.NotRun,
                existing?.QuarantinedAtUtc),
            LastRunAtUtc = existing?.LastRunAtUtc ?? DateTimeOffset.UtcNow,
            LastCompletedAtUtc = existing?.LastCompletedAtUtc,
            LastDeliveredAtUtc = existing?.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = existing?.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = existing?.QuarantinedAtUtc,
            NextRetryAtUtc = null,
            DeliverySuppressed = false,
            InputTokens = 0,
            OutputTokens = 0,
            FailureStreak = existing?.FailureStreak ?? 0,
            UnverifiedStreak = existing?.UnverifiedStreak ?? 0,
            NextRetryAttempt = null,
            LastRunId = message.AutomationRunId,
            SessionId = message.SessionId,
            MessagePreview = existing?.MessagePreview ?? BuildMessagePreview(message.Text),
            VerificationSummary = null,
            QuarantineReason = existing?.QuarantineReason,
            SignalSeverity = existing?.SignalSeverity
        };

        await _store.SaveRunStateAsync(runningState, ct);

        if (IsHeartbeat(automation.Id, message.AutomationTriggerSource))
            return;

        var record = await _store.GetRunRecordAsync(automation.Id, message.AutomationRunId, ct)
            ?? new AutomationRunRecord
            {
                RunId = message.AutomationRunId,
                AutomationId = automation.Id,
                TriggerSource = NormalizeTriggerSource(message.AutomationTriggerSource),
                RetryAttempt = 0,
                StartedAtUtc = DateTimeOffset.UtcNow
            };

        await _store.SaveRunRecordAsync(record with
        {
            LifecycleState = AutomationLifecycleStates.Running,
            SessionId = message.SessionId,
            MessagePreview = runningState.MessagePreview
        }, ct);
    }

    public async Task FinalizeRunAsync(
        AutomationDefinition automation,
        InboundMessage message,
        Session? session,
        AutomationRunCompletion completion,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _store.GetRunStateAsync(automation.Id, ct);
        var triggerSource = NormalizeTriggerSource(message.AutomationTriggerSource);
        var affectsStreaks = triggerSource is AutomationRunTriggerSources.Schedule or AutomationRunTriggerSources.Retry;
        var heartbeat = IsHeartbeat(automation.Id, triggerSource);

        string verificationStatus;
        string? verificationSummary;
        IReadOnlyList<VerificationCheckResult> verificationChecks;

        if (!string.IsNullOrWhiteSpace(completion.VerificationStatus))
        {
            verificationStatus = completion.VerificationStatus!;
            verificationSummary = completion.VerificationSummary;
            verificationChecks = completion.VerificationChecks ?? [];
        }
        else
        {
            (verificationStatus, verificationSummary, verificationChecks) =
                await _contractGovernance.EvaluateVerificationAsync(session?.ContractPolicy?.Verification ?? automation.Verification, ct);
        }

        var failureStreak = existing?.FailureStreak ?? 0;
        var unverifiedStreak = existing?.UnverifiedStreak ?? 0;
        var quarantinedAtUtc = existing?.QuarantinedAtUtc;
        var quarantineReason = existing?.QuarantineReason;
        DateTimeOffset? nextRetryAtUtc = null;
        int? nextRetryAttempt = null;

        if (affectsStreaks)
        {
            if (string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
            {
                failureStreak = 0;
                unverifiedStreak = 0;
            }
            else if (string.Equals(verificationStatus, AutomationVerificationStatuses.NotVerified, StringComparison.OrdinalIgnoreCase))
            {
                unverifiedStreak++;
                failureStreak = 0;
            }
            else if (verificationStatus is AutomationVerificationStatuses.Failed or AutomationVerificationStatuses.Blocked)
            {
                var canRetry = CanScheduleRetry(automation, triggerSource, completion.RetryAttempt);
                if (canRetry)
                {
                    nextRetryAttempt = completion.RetryAttempt + 1;
                    nextRetryAtUtc = now.Add(GetRetryDelay(nextRetryAttempt.Value));
                }
                else
                {
                    failureStreak++;
                    unverifiedStreak = 0;
                    if (failureStreak >= QuarantineFailureThreshold && !heartbeat)
                    {
                        quarantinedAtUtc ??= now;
                        quarantineReason = verificationSummary ?? $"Automation failed {failureStreak} consecutive scheduled runs.";
                    }
                }
            }
        }

        if (completion.ResetQuarantine)
        {
            quarantinedAtUtc = null;
            quarantineReason = null;
            failureStreak = 0;
            unverifiedStreak = 0;
            nextRetryAtUtc = null;
            nextRetryAttempt = null;
        }

        var signalSeverity = completion.SignalSeverity ?? existing?.SignalSeverity;
        var finalState = new AutomationRunState
        {
            AutomationId = automation.Id,
            Outcome = AutomationRunStatusMapper.DeriveOutcome(
                automation.Id,
                completion.LifecycleState,
                verificationStatus,
                signalSeverity),
            LifecycleState = completion.LifecycleState,
            VerificationStatus = verificationStatus,
            HealthState = AutomationRunStatusMapper.DeriveHealthState(
                completion.LifecycleState,
                verificationStatus,
                quarantinedAtUtc),
            LastRunAtUtc = existing?.LastRunAtUtc ?? now,
            LastCompletedAtUtc = completion.LifecycleState is AutomationLifecycleStates.Completed or AutomationLifecycleStates.Stuck ? now : existing?.LastCompletedAtUtc,
            LastDeliveredAtUtc = completion.LastDeliveredAtUtc ?? existing?.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase)
                ? now
                : existing?.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = quarantinedAtUtc,
            NextRetryAtUtc = nextRetryAtUtc,
            DeliverySuppressed = completion.DeliverySuppressed,
            InputTokens = completion.InputTokens,
            OutputTokens = completion.OutputTokens,
            FailureStreak = failureStreak,
            UnverifiedStreak = unverifiedStreak,
            NextRetryAttempt = nextRetryAttempt,
            LastRunId = message.AutomationRunId ?? existing?.LastRunId,
            SessionId = session?.Id ?? existing?.SessionId ?? message.SessionId,
            MessagePreview = existing?.MessagePreview ?? BuildMessagePreview(message.Text),
            VerificationSummary = verificationSummary,
            QuarantineReason = quarantineReason,
            SignalSeverity = signalSeverity
        };

        await _store.SaveRunStateAsync(finalState, ct);

        if (!heartbeat && !string.IsNullOrWhiteSpace(message.AutomationRunId))
        {
            var record = await _store.GetRunRecordAsync(automation.Id, message.AutomationRunId!, ct)
                ?? new AutomationRunRecord
                {
                    RunId = message.AutomationRunId!,
                    AutomationId = automation.Id,
                    TriggerSource = triggerSource,
                    RetryAttempt = completion.RetryAttempt,
                    StartedAtUtc = existing?.LastRunAtUtc ?? now
                };

            await _store.SaveRunRecordAsync(record with
            {
                TriggerSource = triggerSource,
                LifecycleState = completion.LifecycleState,
                VerificationStatus = verificationStatus,
                VerificationSummary = verificationSummary,
                VerificationChecks = verificationChecks,
                SessionId = session?.Id ?? record.SessionId,
                CompletedAtUtc = now,
                LastDeliveredAtUtc = completion.LastDeliveredAtUtc,
                DeliverySuppressed = completion.DeliverySuppressed,
                InputTokens = completion.InputTokens,
                OutputTokens = completion.OutputTokens,
                RetryAttempt = completion.RetryAttempt
            }, ct);
            await _store.PruneRunRecordsAsync(automation.Id, RunHistoryRetention, ct);
        }

        if (session?.ContractPolicy is not null)
        {
            _contractGovernance.AppendSnapshot(
                session,
                completion.ContractStatus,
                completion.LifecycleState,
                verificationStatus,
                verificationSummary,
                verificationChecks);
            _contractGovernance.DetachFromSession(session);
        }
    }

    public async Task ClearQuarantineAsync(string automationId, CancellationToken ct)
    {
        var state = await _store.GetRunStateAsync(automationId, ct);
        if (state is null)
            return;

        await _store.SaveRunStateAsync(new AutomationRunState
        {
            AutomationId = state.AutomationId,
            Outcome = state.Outcome,
            LifecycleState = state.LifecycleState,
            VerificationStatus = state.VerificationStatus,
            HealthState = AutomationRunStatusMapper.DeriveHealthState(state.LifecycleState, state.VerificationStatus, quarantinedAtUtc: null),
            LastRunAtUtc = state.LastRunAtUtc,
            LastCompletedAtUtc = state.LastCompletedAtUtc,
            LastDeliveredAtUtc = state.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = state.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = null,
            NextRetryAtUtc = null,
            DeliverySuppressed = state.DeliverySuppressed,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            FailureStreak = 0,
            UnverifiedStreak = 0,
            NextRetryAttempt = null,
            LastRunId = state.LastRunId,
            SessionId = state.SessionId,
            MessagePreview = state.MessagePreview,
            VerificationSummary = state.VerificationSummary,
            QuarantineReason = null,
            SignalSeverity = state.SignalSeverity
        }, ct);
    }

    public async Task<bool> MarkRunStuckAsync(AutomationDefinition automation, AutomationRunState state, CancellationToken ct)
    {
        if (!string.Equals(state.LifecycleState, AutomationLifecycleStates.Running, StringComparison.OrdinalIgnoreCase)
            || state.LastRunAtUtc is null
            || (DateTimeOffset.UtcNow - state.LastRunAtUtc.Value) < StuckThreshold)
        {
            return false;
        }

        await _store.SaveRunStateAsync(new AutomationRunState
        {
            AutomationId = state.AutomationId,
            Outcome = AutomationRunStatusMapper.DeriveOutcome(
                automation.Id,
                AutomationLifecycleStates.Stuck,
                AutomationVerificationStatuses.Failed,
                state.SignalSeverity),
            LifecycleState = AutomationLifecycleStates.Stuck,
            VerificationStatus = AutomationVerificationStatuses.Failed,
            HealthState = AutomationRunStatusMapper.DeriveHealthState(
                AutomationLifecycleStates.Stuck,
                AutomationVerificationStatuses.Failed,
                state.QuarantinedAtUtc),
            LastRunAtUtc = state.LastRunAtUtc,
            LastCompletedAtUtc = DateTimeOffset.UtcNow,
            LastDeliveredAtUtc = state.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = state.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = state.QuarantinedAtUtc,
            NextRetryAtUtc = null,
            DeliverySuppressed = state.DeliverySuppressed,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            FailureStreak = state.FailureStreak,
            UnverifiedStreak = state.UnverifiedStreak,
            NextRetryAttempt = null,
            LastRunId = state.LastRunId,
            SessionId = state.SessionId,
            MessagePreview = state.MessagePreview,
            VerificationSummary = $"Run exceeded the {StuckThreshold.TotalHours:0}-hour stuck threshold.",
            QuarantineReason = state.QuarantineReason,
            SignalSeverity = state.SignalSeverity
        }, ct);

        if (!string.IsNullOrWhiteSpace(state.LastRunId) && !IsHeartbeat(automation.Id, triggerSource: null))
        {
            var record = await _store.GetRunRecordAsync(automation.Id, state.LastRunId!, ct);
            if (record is not null)
            {
                await _store.SaveRunRecordAsync(record with
                {
                    LifecycleState = AutomationLifecycleStates.Stuck,
                    VerificationStatus = AutomationVerificationStatuses.Failed,
                    VerificationSummary = $"Run exceeded the {StuckThreshold.TotalHours:0}-hour stuck threshold.",
                    CompletedAtUtc = DateTimeOffset.UtcNow
                }, ct);
            }
        }

        return true;
    }

    private static bool CanScheduleRetry(AutomationDefinition automation, string triggerSource, int retryAttempt)
        => automation.RetryPolicy.Enabled
           && triggerSource is AutomationRunTriggerSources.Schedule or AutomationRunTriggerSources.Retry
           && retryAttempt < Math.Max(0, automation.RetryPolicy.MaxRetries);

    private static TimeSpan GetRetryDelay(int retryAttempt)
    {
        var minutes = retryAttempt switch
        {
            <= 1 => 1,
            2 => 2,
            3 => 4,
            _ => Math.Min(15, (int)Math.Pow(2, retryAttempt - 1))
        };
        return TimeSpan.FromMinutes(minutes);
    }

    private static string NormalizeTriggerSource(string? triggerSource)
        => string.IsNullOrWhiteSpace(triggerSource)
            ? AutomationRunTriggerSources.Manual
            : triggerSource.Trim();

    private static string BuildMessagePreview(string value)
        => value.Length > 180 ? value[..180] : value;

    private static bool IsHeartbeat(string automationId, string? triggerSource)
        => string.Equals(automationId, GatewayAutomationService.HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerSource, AutomationRunTriggerSources.Heartbeat, StringComparison.OrdinalIgnoreCase);
}

internal sealed class AutomationRunCompletion
{
    public string LifecycleState { get; init; } = AutomationLifecycleStates.Completed;
    public string ContractStatus { get; init; } = "completed";
    public string? VerificationStatus { get; init; }
    public string? VerificationSummary { get; init; }
    public IReadOnlyList<VerificationCheckResult>? VerificationChecks { get; init; }
    public bool DeliverySuppressed { get; init; }
    public DateTimeOffset? LastDeliveredAtUtc { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public int RetryAttempt { get; init; }
    public bool ResetQuarantine { get; init; }
    public string? SignalSeverity { get; init; }
}
