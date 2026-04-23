using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal sealed class RunNowResult
{
    public static readonly RunNowResult NotFound = new() { Status = "not_found" };
    public static readonly RunNowResult AlreadyRunning = new() { Status = "already_running" };
    public static readonly RunNowResult Quarantined = new() { Status = "quarantined" };

    public required string Status { get; init; }
    public string? RunId { get; init; }
}

internal sealed class GatewayAutomationService
{
    public const string HeartbeatAutomationId = "heartbeat.default";

    private readonly ConcurrentDictionary<string, byte> _runningAutomations = new(StringComparer.OrdinalIgnoreCase);
    private readonly GatewayConfig _config;
    private readonly IAutomationStore _store;
    private readonly HeartbeatService _heartbeat;
    private readonly AutomationRunCoordinator _runCoordinator;
    private readonly ILogger<GatewayAutomationService>? _logger;
    private AutomationDefinition[] _cachedAutomations = [];

    public GatewayAutomationService(
        GatewayConfig config,
        IAutomationStore store,
        HeartbeatService heartbeat,
        AutomationRunCoordinator runCoordinator,
        ILogger<GatewayAutomationService>? logger = null)
    {
        _config = config;
        _store = store;
        _heartbeat = heartbeat;
        _runCoordinator = runCoordinator;
        _logger = logger;
    }

    public async ValueTask<IReadOnlyList<AutomationDefinition>> ListAsync(CancellationToken ct)
    {
        var items = new List<AutomationDefinition>();
        if (_config.Cron.Enabled)
            items.AddRange(_config.Cron.Jobs.Select(MapLegacyJob));

        var storedAutomations = await _store.ListAutomationsAsync(ct);
        _cachedAutomations = [.. storedAutomations];
        items.AddRange(storedAutomations);

        var heartbeat = MapHeartbeatConfig(_heartbeat.LoadConfig());
        items.Add(heartbeat);

        return items
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask<AutomationDefinition?> GetAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
            return MapHeartbeatConfig(_heartbeat.LoadConfig());

        var legacy = _config.Cron.Jobs.FirstOrDefault(item => string.Equals(GetLegacyAutomationId(item), automationId, StringComparison.OrdinalIgnoreCase));
        if (legacy is not null)
            return MapLegacyJob(legacy);

        return await _store.GetAutomationAsync(automationId, ct);
    }

    public async ValueTask<AutomationDefinition> SaveAsync(AutomationDefinition automation, CancellationToken ct)
    {
        var normalized = Normalize(automation);
        if (string.Equals(normalized.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            _heartbeat.SaveConfig(new HeartbeatConfigDto
            {
                Enabled = normalized.Enabled,
                CronExpression = normalized.Schedule,
                Timezone = normalized.Timezone,
                DeliveryChannelId = normalized.DeliveryChannelId,
                DeliveryRecipientId = normalized.DeliveryRecipientId,
                DeliverySubject = normalized.DeliverySubject,
                ModelId = normalized.ModelId,
                Tasks =
                [
                    new HeartbeatTaskDto
                    {
                        Id = "heartbeat-automation",
                        TemplateKey = "custom",
                        Title = string.IsNullOrWhiteSpace(normalized.Name) ? "Heartbeat" : normalized.Name,
                        Instruction = normalized.Prompt,
                        Enabled = true
                    }
                ]
            });

            return MapHeartbeatConfig(_heartbeat.LoadConfig());
        }

        await _store.SaveAutomationAsync(normalized, ct);
        await RefreshCacheAsync(ct);
        return normalized;
    }

    public async ValueTask DeleteAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            _heartbeat.SaveConfig(new HeartbeatConfigDto { Enabled = false, Tasks = [] });
            return;
        }

        await _store.DeleteAutomationAsync(automationId, ct);
        await RefreshCacheAsync(ct);
    }

    public async ValueTask<AutomationRunState?> GetRunStateAsync(string automationId, CancellationToken ct)
    {
        if (string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            var overlay = AutomationRunStatusMapper.NormalizeState(automationId, await _store.GetRunStateAsync(automationId, ct));
            var status = _heartbeat.LoadStatus();
            if (status is null)
                return overlay;

            return AutomationRunStatusMapper.MapHeartbeatState(status, overlay);
        }

        return AutomationRunStatusMapper.NormalizeState(automationId, await _store.GetRunStateAsync(automationId, ct));
    }

    public ValueTask SaveRunStateAsync(AutomationRunState runState, CancellationToken ct)
        => _store.SaveRunStateAsync(runState, ct);

    public ValueTask<IReadOnlyList<AutomationRunRecord>> ListRunRecordsAsync(string automationId, int limit, CancellationToken ct)
        => string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)
            ? ValueTask.FromResult<IReadOnlyList<AutomationRunRecord>>([])
            : _runCoordinator.ListRunRecordsAsync(automationId, limit, ct);

    public ValueTask<AutomationRunRecord?> GetRunRecordAsync(string automationId, string runId, CancellationToken ct)
        => string.Equals(automationId, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)
            ? ValueTask.FromResult<AutomationRunRecord?>(null)
            : _runCoordinator.GetRunRecordAsync(automationId, runId, ct);

    public ValueTask ClearQuarantineAsync(string automationId, CancellationToken ct)
        => new(_runCoordinator.ClearQuarantineAsync(automationId, ct));

    public ValueTask MarkRunRunningAsync(AutomationDefinition automation, InboundMessage message, CancellationToken ct)
        => _runCoordinator.MarkRunningAsync(automation, message, ct);

    public Task FinalizeRunAsync(
        AutomationDefinition automation,
        InboundMessage message,
        Session? session,
        AutomationRunCompletion completion,
        CancellationToken ct)
        => _runCoordinator.FinalizeRunAsync(automation, message, session, completion, ct);

    public IReadOnlyCollection<string> ListRunningIds()
        => _runningAutomations.Keys.ToArray();

    public async ValueTask<RunNowResult> RunNowAsync(string automationId, MessagePipeline pipeline, CancellationToken ct)
    {
        var automation = await GetAsync(automationId, ct);
        if (automation is null)
            return RunNowResult.NotFound;

        if (!_runningAutomations.TryAdd(automation.Id, 0))
        {
            _logger?.LogWarning("Skipping automation '{AutomationId}' because a previous run is still active.", automationId);
            return RunNowResult.AlreadyRunning;
        }

        try
        {
            return await QueueAutomationAsync(automation, pipeline, AutomationRunTriggerSources.Manual, replayOfRunId: null, retryAttempt: 0, ct);
        }
        catch
        {
            _runningAutomations.TryRemove(automation.Id, out _);
            throw;
        }
    }

    public async ValueTask<RunNowResult> ReplayAsync(string automationId, string runId, MessagePipeline pipeline, CancellationToken ct)
    {
        var automation = await GetAsync(automationId, ct);
        if (automation is null)
            return RunNowResult.NotFound;

        var run = await GetRunRecordAsync(automationId, runId, ct);
        if (run is null)
            return RunNowResult.NotFound;

        if (!_runningAutomations.TryAdd(automation.Id, 0))
            return RunNowResult.AlreadyRunning;

        try
        {
            return await QueueAutomationAsync(automation, pipeline, AutomationRunTriggerSources.Replay, runId, retryAttempt: 0, ct);
        }
        catch
        {
            _runningAutomations.TryRemove(automation.Id, out _);
            throw;
        }
    }

    public void MarkRunCompleted(string? automationId)
    {
        if (!string.IsNullOrWhiteSpace(automationId))
            _runningAutomations.TryRemove(automationId!, out _);
    }

    public async ValueTask RunMaintenanceAsync(MessagePipeline pipeline, CancellationToken ct)
    {
        var automations = await ListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var automation in automations)
        {
            var state = await GetRunStateAsync(automation.Id, ct);
            if (state is null)
                continue;

            if (await _runCoordinator.MarkRunStuckAsync(automation, state, ct))
            {
                _runningAutomations.TryRemove(automation.Id, out _);
                continue;
            }

            if (string.Equals(automation.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)
                || !automation.Enabled
                || automation.IsDraft
                || state.QuarantinedAtUtc is not null
                || state.NextRetryAtUtc is null
                || state.NextRetryAttempt is null
                || state.NextRetryAtUtc > now)
            {
                continue;
            }

            if (!_runningAutomations.TryAdd(automation.Id, 0))
                continue;

            try
            {
                var result = await QueueAutomationAsync(
                    automation,
                    pipeline,
                    AutomationRunTriggerSources.Retry,
                    replayOfRunId: null,
                    retryAttempt: state.NextRetryAttempt.Value,
                    ct);

                if (!string.Equals(result.Status, "queued", StringComparison.Ordinal))
                    _runningAutomations.TryRemove(automation.Id, out _);
            }
            catch
            {
                _runningAutomations.TryRemove(automation.Id, out _);
                throw;
            }
        }
    }

    public void AttachRunContract(Session session, AutomationDefinition automation, string? runId, ContractGovernanceService governance)
    {
        if (session.ContractPolicy is not null
            || string.IsNullOrWhiteSpace(runId)
            || string.Equals(automation.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        governance.AttachToSession(session, new ContractPolicy
        {
            Id = BuildAutomationRunContractId(automation.Id, runId!),
            Name = $"Automation run: {automation.Name}",
            CreatedBy = automation.Source,
            Verification = automation.Verification,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    internal static string BuildAutomationRunContractId(string automationId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var contractId = $"ctr_{automationId}_{runId}".Replace(':', '_');
        return contractId.Length > 20 ? contractId[..20] : contractId;
    }

    internal static bool IsAutomationRunContract(string? contractId, string automationId, string? runId)
        => !string.IsNullOrWhiteSpace(contractId)
           && !string.IsNullOrWhiteSpace(runId)
           && string.Equals(contractId, BuildAutomationRunContractId(automationId, runId!), StringComparison.Ordinal);

    public async ValueTask<IReadOnlyList<AutomationDefinition>> MigrateLegacyAsync(bool apply, CancellationToken ct)
    {
        var migrated = new List<AutomationDefinition>();
        if (_config.Cron.Enabled)
            migrated.AddRange(_config.Cron.Jobs.Select(MapLegacyJob));

        var heartbeat = _heartbeat.LoadConfig();
        if (heartbeat.Enabled)
        {
            var mappedHeartbeat = MapHeartbeatConfig(heartbeat);
            migrated.Add(new AutomationDefinition
            {
                Id = mappedHeartbeat.Id,
                Name = mappedHeartbeat.Name,
                Enabled = false,
                Schedule = mappedHeartbeat.Schedule,
                Timezone = mappedHeartbeat.Timezone,
                Prompt = mappedHeartbeat.Prompt,
                ModelId = mappedHeartbeat.ModelId,
                RunOnStartup = mappedHeartbeat.RunOnStartup,
                SessionId = mappedHeartbeat.SessionId,
                DeliveryChannelId = mappedHeartbeat.DeliveryChannelId,
                DeliveryRecipientId = mappedHeartbeat.DeliveryRecipientId,
                DeliverySubject = mappedHeartbeat.DeliverySubject,
                Tags = mappedHeartbeat.Tags,
                IsDraft = true,
                Source = "migrated-heartbeat",
                TemplateKey = mappedHeartbeat.TemplateKey,
                CreatedAtUtc = mappedHeartbeat.CreatedAtUtc,
                UpdatedAtUtc = mappedHeartbeat.UpdatedAtUtc
            });
        }

        if (apply)
        {
            foreach (var automation in migrated.Where(static item => !string.Equals(item.Id, HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase)))
                await _store.SaveAutomationAsync(automation, ct);
            await RefreshCacheAsync(ct);
        }

        return migrated;
    }

    public async ValueTask RefreshCacheAsync(CancellationToken ct)
    {
        _cachedAutomations = [.. await _store.ListAutomationsAsync(ct)];
    }

    public IReadOnlyList<AutomationTemplate> GetTemplates()
        =>
        [
            new AutomationTemplate
            {
                Key = "heartbeat",
                Label = "Heartbeat",
                Description = "Managed heartbeat automation compatible with the existing heartbeat flow.",
                Category = "ops",
                SuggestedName = "Managed heartbeat",
                Schedule = "@hourly",
                Prompt = "Summarize operational health, active issues, and urgent follow-ups.",
                DeliveryChannelId = "cron",
                Tags = ["ops", "heartbeat"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "custom",
                Label = "Custom",
                Description = "A direct scheduled prompt delivered through any supported channel.",
                Category = "general",
                SuggestedName = "Custom automation",
                Schedule = "@daily",
                Prompt = "Describe the recurring task this automation should perform.",
                DeliveryChannelId = "cron",
                Tags = ["custom"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "inbox_triage",
                Label = "Inbox Triage",
                Description = "Review new inbox activity and surface only the messages that need an operator response.",
                Category = "productivity",
                SuggestedName = "Inbox triage",
                Schedule = "0 8 * * *",
                Prompt = "Review new inbox activity since the last run. Group urgent, blocking, and informational items separately and recommend the next operator actions.",
                DeliveryChannelId = "cron",
                Tags = ["inbox", "triage", "review"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "daily_summary",
                Label = "Daily Summary",
                Description = "Produce a concise day-start or day-end summary of recent sessions, approvals, and notable events.",
                Category = "ops",
                SuggestedName = "Daily summary",
                Schedule = "0 9 * * *",
                Prompt = "Summarize the last 24 hours of agent activity, approvals, failed actions, and important follow-ups. Keep it concise and actionable.",
                DeliveryChannelId = "cron",
                Tags = ["summary", "daily", "ops"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "channel_moderation",
                Label = "Channel Moderation",
                Description = "Review recent channel traffic for abuse, escalation signals, or messages that should be added to allowlist workflows.",
                Category = "channels",
                SuggestedName = "Channel moderation sweep",
                Schedule = "0 */6 * * *",
                Prompt = "Review recent channel traffic for abuse, moderation issues, escalation signals, and sender patterns that need operator action. Highlight risky senders and suggested allowlist changes.",
                DeliveryChannelId = "cron",
                Tags = ["channels", "moderation", "safety"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "incident_follow_up",
                Label = "Incident Follow-Up",
                Description = "Track unresolved runtime failures, dead-letter items, and degraded integrations until they are cleared.",
                Category = "ops",
                SuggestedName = "Incident follow-up",
                Schedule = "@hourly",
                Prompt = "Review unresolved runtime failures, dead-letter items, approval bottlenecks, and degraded integrations. Report what changed since the last run and what still needs operator attention.",
                DeliveryChannelId = "cron",
                Tags = ["incident", "ops", "follow-up"],
                Available = true
            },
            new AutomationTemplate
            {
                Key = "repo_hygiene",
                Label = "Repo Hygiene",
                Description = "Check codebase health tasks such as stale branches, docs drift, test failures, and compatibility gaps.",
                Category = "engineering",
                SuggestedName = "Repo hygiene review",
                Schedule = "0 10 * * 1-5",
                Prompt = "Review repository hygiene for stale work, failing tests, docs drift, or compatibility regressions. Summarize high-priority fixes and deferred cleanup separately.",
                DeliveryChannelId = "cron",
                Tags = ["repo", "engineering", "hygiene"],
                Available = true
            }
        ];

    public AutomationPreview BuildPreview(AutomationDefinition automation)
    {
        var normalized = Normalize(automation);
        var issues = new List<AutomationValidationIssue>();
        if (string.IsNullOrWhiteSpace(normalized.Name))
            issues.Add(new AutomationValidationIssue { Code = "name_required", Message = "Automation name is required." });
        if (string.IsNullOrWhiteSpace(normalized.Prompt))
            issues.Add(new AutomationValidationIssue { Code = "prompt_required", Message = "Automation prompt is required." });
        if (string.IsNullOrWhiteSpace(normalized.Schedule))
            issues.Add(new AutomationValidationIssue { Code = "schedule_required", Message = "Automation schedule is required." });

        return new AutomationPreview
        {
            Definition = normalized,
            Issues = issues,
            Templates = GetTemplates(),
            PromptPreview = normalized.Prompt,
            EstimatedRunsPerMonth = EstimateRunsPerMonth(normalized.Schedule)
        };
    }

    public IReadOnlyList<CronJobConfig> BuildCronJobs()
    {
        var jobs = new List<CronJobConfig>();
        if (_config.Cron.Enabled && _config.Cron.Jobs is { Count: > 0 })
        {
            jobs.AddRange(_config.Cron.Jobs.Select(job => new CronJobConfig
            {
                Name = job.Name,
                CronExpression = job.CronExpression,
                Prompt = job.Prompt,
                RunOnStartup = job.RunOnStartup,
                SessionId = job.SessionId,
                ChannelId = job.ChannelId,
                RecipientId = job.RecipientId,
                Subject = job.Subject,
                AutomationId = job.AutomationId ?? GetLegacyAutomationId(job),
                AutomationTriggerSource = job.AutomationTriggerSource ?? AutomationRunTriggerSources.Schedule,
                Timezone = job.Timezone
            }));
        }

        var managedHeartbeatJob = _heartbeat.BuildManagedJob();
        if (managedHeartbeatJob is not null)
        {
            managedHeartbeatJob.AutomationId = HeartbeatAutomationId;
            managedHeartbeatJob.AutomationTriggerSource = AutomationRunTriggerSources.Heartbeat;
            jobs.Add(managedHeartbeatJob);
        }

        foreach (var automation in _cachedAutomations)
        {
            if (!automation.Enabled || automation.IsDraft)
                continue;

            jobs.Add(new CronJobConfig
            {
                Name = automation.Id,
                CronExpression = automation.Schedule,
                Prompt = automation.Prompt,
                RunOnStartup = automation.RunOnStartup,
                SessionId = automation.SessionId,
                ChannelId = automation.DeliveryChannelId,
                RecipientId = automation.DeliveryRecipientId,
                Subject = automation.DeliverySubject,
                AutomationId = automation.Id,
                AutomationTriggerSource = AutomationRunTriggerSources.Schedule,
                Timezone = automation.Timezone
            });
        }

        return jobs;
    }

    private static AutomationDefinition Normalize(AutomationDefinition automation)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationDefinition
        {
            Id = string.IsNullOrWhiteSpace(automation.Id) ? $"automation-{Guid.NewGuid():N}"[..20] : automation.Id.Trim(),
            Name = automation.Name.Trim(),
            Enabled = automation.Enabled,
            Schedule = string.IsNullOrWhiteSpace(automation.Schedule) ? "@hourly" : automation.Schedule.Trim(),
            Timezone = string.IsNullOrWhiteSpace(automation.Timezone) ? null : automation.Timezone.Trim(),
            Prompt = automation.Prompt ?? "",
            ModelId = string.IsNullOrWhiteSpace(automation.ModelId) ? null : automation.ModelId.Trim(),
            ResponseMode = NormalizeResponseMode(automation.ResponseMode),
            RunOnStartup = automation.RunOnStartup,
            SessionId = string.IsNullOrWhiteSpace(automation.SessionId) ? null : automation.SessionId.Trim(),
            DeliveryChannelId = string.IsNullOrWhiteSpace(automation.DeliveryChannelId) ? "cron" : automation.DeliveryChannelId.Trim(),
            DeliveryRecipientId = string.IsNullOrWhiteSpace(automation.DeliveryRecipientId) ? null : automation.DeliveryRecipientId.Trim(),
            DeliverySubject = string.IsNullOrWhiteSpace(automation.DeliverySubject) ? null : automation.DeliverySubject.Trim(),
            Tags = (automation.Tags ?? [])
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IsDraft = automation.IsDraft,
            Source = string.IsNullOrWhiteSpace(automation.Source) ? "managed" : automation.Source.Trim(),
            TemplateKey = string.IsNullOrWhiteSpace(automation.TemplateKey) ? null : automation.TemplateKey.Trim(),
            Verification = automation.Verification,
            RetryPolicy = automation.RetryPolicy ?? new AutomationRetryPolicy(),
            CreatedAtUtc = automation.CreatedAtUtc == default ? now : automation.CreatedAtUtc,
            UpdatedAtUtc = now
        };
    }

    private static AutomationDefinition MapLegacyJob(CronJobConfig job)
        => new()
        {
            Id = GetLegacyAutomationId(job),
            Name = string.IsNullOrWhiteSpace(job.Name) ? "Legacy Cron Job" : job.Name,
            Enabled = true,
            Schedule = string.IsNullOrWhiteSpace(job.CronExpression) ? "@hourly" : job.CronExpression,
            Timezone = string.IsNullOrWhiteSpace(job.Timezone) ? null : job.Timezone,
            Prompt = job.Prompt ?? "",
            RunOnStartup = job.RunOnStartup,
            SessionId = job.SessionId,
            ResponseMode = SessionResponseModes.ConciseOps,
            DeliveryChannelId = string.IsNullOrWhiteSpace(job.ChannelId) ? "cron" : job.ChannelId!,
            DeliveryRecipientId = job.RecipientId,
            DeliverySubject = job.Subject,
            Tags = ["legacy"],
            Source = "legacy-cron",
            TemplateKey = "custom",
            RetryPolicy = new AutomationRetryPolicy()
        };

    private AutomationDefinition MapHeartbeatConfig(HeartbeatConfigDto config)
        => new()
        {
            Id = HeartbeatAutomationId,
            Name = "Managed Heartbeat",
            Enabled = config.Enabled,
            Schedule = config.CronExpression,
            Timezone = config.Timezone,
            Prompt = _heartbeat.BuildManagedPrompt(config, _heartbeat.RenderMarkdown(config)),
            ModelId = config.ModelId,
            ResponseMode = SessionResponseModes.ConciseOps,
            DeliveryChannelId = config.DeliveryChannelId,
            DeliveryRecipientId = config.DeliveryRecipientId,
            DeliverySubject = config.DeliverySubject,
            Tags = ["heartbeat"],
            Source = "heartbeat",
            TemplateKey = "heartbeat",
            RetryPolicy = new AutomationRetryPolicy()
        };

    private async ValueTask<RunNowResult> QueueAutomationAsync(
        AutomationDefinition automation,
        MessagePipeline pipeline,
        string triggerSource,
        string? replayOfRunId,
        int retryAttempt,
        CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(automation.SessionId)
            ? $"automation:{automation.Id}"
            : automation.SessionId;

        var dispatch = await _runCoordinator.PrepareDispatchAsync(new AutomationDispatchRequest
        {
            AutomationId = automation.Id,
            TriggerSource = triggerSource,
            ReplayOfRunId = replayOfRunId,
            RetryAttempt = retryAttempt,
            SessionId = sessionId,
            ChannelId = automation.DeliveryChannelId,
            SenderId = automation.DeliveryRecipientId ?? sessionId,
            Prompt = automation.Prompt,
            Subject = automation.DeliverySubject
        }, ct);

        if (dispatch is null)
            return RunNowResult.Quarantined;

        if (!pipeline.InboundWriter.TryWrite(dispatch))
            await pipeline.InboundWriter.WriteAsync(dispatch, ct);

        return new RunNowResult
        {
            Status = "queued",
            RunId = dispatch.AutomationRunId
        };
    }

    private static string GetLegacyAutomationId(CronJobConfig job)
    {
        if (!string.IsNullOrWhiteSpace(job.Name))
            return $"legacy:{job.Name.Trim()}";

        var seed = $"{job.CronExpression}|{job.Prompt}|{job.ChannelId}|{job.RecipientId}|{job.Subject}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        return $"legacy:{hash[..12].ToLowerInvariant()}";
    }

    private static int EstimateRunsPerMonth(string schedule)
    {
        if (string.Equals(schedule, "@hourly", StringComparison.OrdinalIgnoreCase))
            return 24 * 30;
        if (string.Equals(schedule, "@daily", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (string.Equals(schedule, "@weekly", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (string.Equals(schedule, "@monthly", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 30;
    }

    private static string NormalizeResponseMode(string? responseMode)
    {
        var normalized = responseMode?.Trim().ToLowerInvariant();
        return normalized switch
        {
            SessionResponseModes.Default => SessionResponseModes.Default,
            SessionResponseModes.ConciseOps => SessionResponseModes.ConciseOps,
            SessionResponseModes.Full => SessionResponseModes.Full,
            _ => SessionResponseModes.Default
        };
    }
}
