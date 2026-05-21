using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway;

internal sealed class AdminObservabilityService
{
    private static readonly Regex EmailRegex = new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PhoneRegex = new(@"\+?\d[\d\s().-]{7,}\d", RegexOptions.CultureInvariant);
    private static readonly Regex SecretRegex = new(@"(sk-[A-Za-z0-9_-]{8,}|Bearer\s+[A-Za-z0-9._~+/=-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JsonSecretRegex = new("""("(?:api[_-]?key|token|password|secret)"\s*:\s*")[^"]+(")""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly GatewayStartupContext _startup;
    private readonly GatewayAppRuntime _runtime;
    private readonly GatewayAutomationService _automationService;
    private readonly OrganizationPolicyService _organizationPolicy;
    private readonly ToolUsageTracker _toolUsage;
    private readonly ISessionAdminStore _sessionAdminStore;
    private readonly IRedactionPipeline _redaction;
    private readonly EvidenceBundleService? _evidenceBundles;

    public AdminObservabilityService(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        GatewayAutomationService automationService,
        OrganizationPolicyService organizationPolicy,
        ToolUsageTracker toolUsage,
        ISessionAdminStore sessionAdminStore,
        IRedactionPipeline? redaction = null,
        EvidenceBundleService? evidenceBundles = null)
    {
        _startup = startup;
        _runtime = runtime;
        _automationService = automationService;
        _organizationPolicy = organizationPolicy;
        _toolUsage = toolUsage;
        _sessionAdminStore = sessionAdminStore;
        _redaction = redaction ?? new NoopRedactionPipeline();
        _evidenceBundles = evidenceBundles;
    }

    public async Task<OperatorInsightsResponse> BuildInsightsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct)
    {
        var (startUtc, endUtc, warnings) = NormalizeRange(fromUtc, toUtc, defaultWindow: TimeSpan.FromHours(24), applyRetention: false);
        var providerUsage = _runtime.ProviderUsage.Snapshot()
            .Select(item => new OperatorInsightsProviderUsage
            {
                ProviderId = item.ProviderId,
                ModelId = item.ModelId,
                Requests = item.Requests,
                Retries = item.Retries,
                Errors = item.Errors,
                InputTokens = item.InputTokens,
                OutputTokens = item.OutputTokens,
                CacheReadTokens = item.CacheReadTokens,
                CacheWriteTokens = item.CacheWriteTokens,
                EstimatedCostUsd = EstimateCostUsd(item)
            })
            .OrderByDescending(static item => item.EstimatedCostUsd)
            .ThenByDescending(static item => item.TotalTokens)
            .ThenBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tools = _toolUsage.Snapshot()
            .Take(20)
            .Select(static item => new OperatorInsightsToolFrequency
            {
                ToolName = item.ToolName,
                Calls = item.Calls,
                Failures = item.Failures,
                Timeouts = item.Timeouts,
                AverageDurationMs = item.Calls <= 0 ? 0 : item.TotalDurationMs / item.Calls
            })
            .ToArray();
        var sessions = await BuildSessionCountsAsync(startUtc, endUtc, ct);
        var scopeWarnings = warnings.ToList();
        scopeWarnings.Add("Provider and tool usage are live runtime counters; session counts use the selected date range.");

        return new OperatorInsightsResponse
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            Totals = new OperatorInsightsTotals
            {
                ProviderRequests = providerUsage.Sum(static item => item.Requests),
                ProviderErrors = providerUsage.Sum(static item => item.Errors),
                InputTokens = providerUsage.Sum(static item => item.InputTokens),
                OutputTokens = providerUsage.Sum(static item => item.OutputTokens),
                CacheReadTokens = providerUsage.Sum(static item => item.CacheReadTokens),
                CacheWriteTokens = providerUsage.Sum(static item => item.CacheWriteTokens),
                EstimatedCostUsd = providerUsage.Sum(static item => item.EstimatedCostUsd),
                ToolCalls = tools.Sum(static item => item.Calls)
            },
            Sessions = sessions,
            Providers = providerUsage,
            Tools = tools,
            Warnings = scopeWarnings
        };
    }

    public async Task<ObservabilitySummaryResponse> BuildSummaryAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct)
    {
        var (startUtc, endUtc, _) = NormalizeRange(fromUtc, toUtc, defaultWindow: TimeSpan.FromHours(24), applyRetention: false);
        var approvals = ReadApprovalHistory(startUtc, endUtc);
        var runtimeEvents = ReadRuntimeEvents(startUtc, endUtc);
        var operatorAudit = ReadOperatorAudit(startUtc, endUtc);
        var deadLetters = ReadDeadLetters(startUtc, endUtc);
        var automationCounts = await BuildAutomationCountsAsync(ct);
        var providerUsage = _runtime.ProviderUsage.Snapshot();
        var providerRoutes = _runtime.Operations.LlmExecution.SnapshotRoutes();
        var channelReadiness = ChannelReadinessEvaluator.Evaluate(_startup.Config, _startup.IsNonLoopbackBind);
        var degradedChannels = channelReadiness.Count(item => !item.Ready || !string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase));
        var pendingApprovals = _runtime.ToolApprovalService.ListPending().Count;
        var decisionLatencies = BuildApprovalLatencies(approvals);

        return new ObservabilitySummaryResponse
        {
            Cards =
            [
                new ObservabilitySummaryCard { Id = "approval-decisions", Label = "Approval decisions", Value = approvals.Count(static item => string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase)), Note = $"{pendingApprovals} pending" },
                new ObservabilitySummaryCard { Id = "automation-failures", Label = "Automation failures", Value = automationCounts.Failing, Note = $"{automationCounts.Total} total automations" },
                new ObservabilitySummaryCard { Id = "provider-errors", Label = "Provider errors", Value = checked((int)Math.Min(int.MaxValue, providerUsage.Sum(static item => item.Errors))), Note = $"{providerUsage.Count} provider snapshots" },
                new ObservabilitySummaryCard { Id = "dead-letters", Label = "Dead-letter items", Value = deadLetters.Count, Note = "Webhook delivery backlog" },
                new ObservabilitySummaryCard { Id = "channel-drift", Label = "Channel drift", Value = degradedChannels, Note = $"{channelReadiness.Count} channels checked" },
                new ObservabilitySummaryCard { Id = "operator-actions", Label = "Operator actions", Value = operatorAudit.Count, Note = $"{DistinctActorCount(operatorAudit)} active actors" }
            ],
            ApprovalLatencyBuckets = BuildMetrics(
                decisionLatencies.Select(static item => (item.Key, item.Key, item.Count))),
            ProviderErrorsByRoute = BuildMetrics(
                providerRoutes.Where(static item => item.Errors > 0)
                    .Select(static item => (BuildRouteKey(item), BuildRouteLabel(item), ToCount(item.Errors)))),
            ProviderRetriesByRoute = BuildMetrics(
                providerRoutes.Where(static item => item.Retries > 0)
                    .Select(static item => ($"retry:{BuildRouteKey(item)}", BuildRouteLabel(item), ToCount(item.Retries)))),
            OperatorActions = BuildMetrics(
                operatorAudit.GroupBy(static item => item.ActionType, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => ($"action:{group.Key}", group.Key, group.Count()))),
            OperatorActionsByRole = BuildMetrics(
                operatorAudit.GroupBy(item => string.IsNullOrWhiteSpace(item.ActorRole) ? OperatorRoleNames.Viewer : item.ActorRole, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => ($"role:{group.Key}", group.Key, group.Count()))),
            OperatorActionsByAccount = BuildMetrics(
                operatorAudit.GroupBy(item => BuildActorLabel(item), StringComparer.OrdinalIgnoreCase)
                    .Select(static group => ($"actor:{group.Key}", group.Key, group.Count()))),
            ChannelDrift = BuildMetrics(
                channelReadiness
                    .Where(static item => !item.Ready || !string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase))
                    .Select(static item => ($"channel:{item.ChannelId}", item.DisplayName, item.MissingRequirements.Count + Math.Max(1, item.Warnings.Count))))
        };
    }

    public async Task<ObservabilitySeriesResponse> BuildSeriesAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int bucketMinutes,
        CancellationToken ct)
    {
        var (startUtc, endUtc, _) = NormalizeRange(fromUtc, toUtc, defaultWindow: TimeSpan.FromHours(24), applyRetention: false);
        var effectiveBucketMinutes = Math.Clamp(bucketMinutes <= 0 ? 60 : bucketMinutes, 5, 24 * 60);
        var approvals = ReadApprovalHistory(startUtc, endUtc);
        var runtimeEvents = ReadRuntimeEvents(startUtc, endUtc);
        var operatorAudit = ReadOperatorAudit(startUtc, endUtc);
        var deadLetters = ReadDeadLetters(startUtc, endUtc);
        var channelDrift = ChannelReadinessEvaluator.Evaluate(_startup.Config, _startup.IsNonLoopbackBind)
            .Count(item => !item.Ready || !string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase));
        var activeSessions = _runtime.SessionManager.ActiveCount;
        var providerRoutes = _runtime.Operations.LlmExecution.SnapshotRoutes();
        var providerErrors = ToCount(providerRoutes.Sum(static item => item.Errors));
        var providerRetries = ToCount(providerRoutes.Sum(static item => item.Retries));
        var automationCounts = await BuildAutomationCountsAsync(ct);
        var approvalLifecycle = BuildApprovalLifecycle(approvals);
        var points = new List<ObservabilityMetricPoint>();
        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var next = cursor.AddMinutes(effectiveBucketMinutes);
            var pointApprovals = approvals.Where(item => item.TimestampUtc >= cursor && item.TimestampUtc < next).ToArray();
            var pointEvents = runtimeEvents.Where(item => item.TimestampUtc >= cursor && item.TimestampUtc < next).ToArray();
            var pointAudit = operatorAudit.Where(item => item.TimestampUtc >= cursor && item.TimestampUtc < next).ToArray();
            var pointDeadLetters = deadLetters.Where(item => item.CreatedAtUtc >= cursor && item.CreatedAtUtc < next).ToArray();

            points.Add(new ObservabilityMetricPoint
            {
                TimestampUtc = cursor,
                ApprovalDecisions = pointApprovals.Count(static item => string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase)),
                ApprovalPending = CountPendingApprovals(approvalLifecycle, next),
                AutomationRuns = automationCounts.RanRecently,
                AutomationFailures = automationCounts.Failing,
                ProviderErrors = providerErrors,
                ProviderRetries = providerRetries,
                RuntimeWarnings = pointEvents.Count(static item => string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                RuntimeErrors = pointEvents.Count(static item => string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                DeadLetters = pointDeadLetters.Length,
                ActiveSessions = activeSessions,
                ChannelDrift = channelDrift,
                OperatorActions = pointAudit.Length
            });

            cursor = next;
        }

        return new ObservabilitySeriesResponse
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            BucketMinutes = effectiveBucketMinutes,
            Points = points
        };
    }

    public async Task<byte[]> ExportAuditBundleAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken ct)
    {
        var (startUtc, endUtc, warnings) = NormalizeRange(fromUtc, toUtc, defaultWindow: TimeSpan.FromDays(30), applyRetention: true);
        var approvals = ReadApprovalHistory(startUtc, endUtc);
        var runtimeEvents = ReadRuntimeEvents(startUtc, endUtc);
        var operatorAudit = ReadOperatorAudit(startUtc, endUtc);
        var deadLetters = ReadDeadLetters(startUtc, endUtc);
        var providerUsage = _runtime.ProviderUsage.Snapshot().ToList();
        var providerRoutes = _runtime.Operations.LlmExecution.SnapshotRoutes().ToList();
        var sessionMetadata = _runtime.Operations.SessionMetadata.GetAll().Values
            .OrderBy(static item => item.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var policy = _organizationPolicy.GetSnapshot();
        var files = new[]
        {
            "manifest.json",
            "operator-audit.jsonl",
            "runtime-events.jsonl",
            "approval-history.jsonl",
            "provider-usage.json",
            "provider-routes.json",
            "dead-letter.jsonl",
            "session-metadata.json"
        };

        var manifest = new AuditExportManifest
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Files = files,
            Policy = policy,
            RetentionDays = policy.ExportRetentionDays,
            OperatorAuditSequenceStart = operatorAudit.FirstOrDefault()?.Sequence,
            OperatorAuditSequenceEnd = operatorAudit.LastOrDefault()?.Sequence,
            OperatorAuditPreviousEntryHash = operatorAudit.FirstOrDefault()?.PreviousEntryHash,
            OperatorAuditLastEntryHash = operatorAudit.LastOrDefault()?.EntryHash,
            FileEntryCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["operator-audit.jsonl"] = operatorAudit.Count,
                ["runtime-events.jsonl"] = runtimeEvents.Count,
                ["approval-history.jsonl"] = approvals.Count,
                ["provider-usage.json"] = providerUsage.Count,
                ["provider-routes.json"] = providerRoutes.Count,
                ["dead-letter.jsonl"] = deadLetters.Count,
                ["session-metadata.json"] = sessionMetadata.Count
            },
            Warnings = warnings
        };

        await using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(zip, "manifest.json", manifest, CoreJsonContext.Default.AuditExportManifest);
            WriteJsonlEntry(zip, "operator-audit.jsonl", operatorAudit, CoreJsonContext.Default.OperatorAuditEntry);
            WriteJsonlEntry(zip, "runtime-events.jsonl", runtimeEvents, CoreJsonContext.Default.RuntimeEventEntry);
            WriteJsonlEntry(zip, "approval-history.jsonl", approvals, CoreJsonContext.Default.ApprovalHistoryEntry);
            WriteJsonEntry(zip, "provider-usage.json", providerUsage, CoreJsonContext.Default.ListProviderUsageSnapshot);
            WriteJsonEntry(zip, "provider-routes.json", providerRoutes, CoreJsonContext.Default.ListProviderRouteHealthSnapshot);
            WriteJsonlEntry(zip, "dead-letter.jsonl", deadLetters, CoreJsonContext.Default.WebhookDeadLetterEntry);
            WriteJsonEntry(zip, "session-metadata.json", sessionMetadata, CoreJsonContext.Default.ListSessionMetadataSnapshot);
        }

        ct.ThrowIfCancellationRequested();
        return ms.ToArray();
    }

    public async Task<byte[]> ExportTrajectoryJsonlAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? sessionId,
        bool anonymize,
        bool includeEvidence,
        CancellationToken ct)
    {
        DateTimeOffset startUtc;
        DateTimeOffset endUtc;
        if (!string.IsNullOrWhiteSpace(sessionId) && fromUtc is null && toUtc is null)
        {
            startUtc = DateTimeOffset.MinValue;
            endUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            (startUtc, endUtc, _) = NormalizeRange(fromUtc, toUtc, defaultWindow: TimeSpan.FromHours(24), applyRetention: false);
        }

        var sessions = (await ListTrajectorySessionsAsync(sessionId, ct))
            .OrderBy(static item => item.CreatedAt)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var evidenceBySession = await LoadEvidenceBySessionAsync(sessions, startUtc, endUtc, includeEvidence, ct);
        await using var ms = new MemoryStream();
        await using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();

                for (var i = 0; i < session.History.Count; i++)
                {
                    var turn = session.History[i];
                    if (turn.Timestamp < startUtc || turn.Timestamp > endUtc)
                        continue;

                    await WriteTrajectoryRecordAsync(writer, BuildMessageTrajectoryRecord(session, turn, i, anonymize), ct);

                    if (turn.ToolCalls is null)
                        continue;

                    foreach (var toolCall in turn.ToolCalls)
                    {
                        await WriteTrajectoryRecordAsync(writer, BuildToolCallTrajectoryRecord(session, turn, i, toolCall, anonymize), ct);
                        await WriteTrajectoryRecordAsync(writer, BuildToolResultTrajectoryRecord(session, turn, i, toolCall, anonymize), ct);
                    }
                }

                if (evidenceBySession.TryGetValue(session.Id, out var evidence))
                {
                    foreach (var bundle in evidence)
                    {
                        await WriteTrajectoryRecordAsync(writer, BuildEvidenceTrajectoryRecord(session, bundle, anonymize), ct);
                    }
                }
            }
        }

        return ms.ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<EvidenceBundle>>> LoadEvidenceBySessionAsync(
        IReadOnlyList<Session> sessions,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool includeEvidence,
        CancellationToken ct)
    {
        if (!includeEvidence || _evidenceBundles is null || sessions.Count == 0)
            return new Dictionary<string, IReadOnlyList<EvidenceBundle>>(StringComparer.Ordinal);

        var sessionIds = sessions
            .Select(static session => session.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (sessionIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<EvidenceBundle>>(StringComparer.Ordinal);

        var query = new EvidenceBundleListQuery
        {
            SourceSessionId = sessions.Count == 1 ? sessions[0].Id : null,
            CreatedFromUtc = startUtc == DateTimeOffset.MinValue ? null : startUtc,
            CreatedToUtc = endUtc,
            Limit = 5000
        };
        var evidence = await _evidenceBundles.ListAsync(query, ct);
        return evidence
            .Where(bundle => !string.IsNullOrWhiteSpace(bundle.SourceSessionId) && sessionIds.Contains(bundle.SourceSessionId))
            .GroupBy(static bundle => bundle.SourceSessionId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EvidenceBundle>)group
                    .OrderBy(static item => item.CreatedAtUtc)
                    .ThenBy(static item => item.Id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    private IReadOnlyList<ApprovalHistoryEntry> ReadApprovalHistory(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => ReadJsonlEntries(
            _runtime.ApprovalAuditStore.Path,
            CoreJsonContext.Default.ApprovalHistoryEntry,
            static item => item.TimestampUtc,
            startUtc,
            endUtc);

    private IReadOnlyList<RuntimeEventEntry> ReadRuntimeEvents(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => ReadJsonlEntries(
            _runtime.Operations.RuntimeEvents.Path,
            CoreJsonContext.Default.RuntimeEventEntry,
            static item => item.TimestampUtc,
            startUtc,
            endUtc);

    private IReadOnlyList<OperatorAuditEntry> ReadOperatorAudit(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => ReadJsonlEntries(
            _runtime.Operations.OperatorAudit.Path,
            CoreJsonContext.Default.OperatorAuditEntry,
            static item => item.TimestampUtc,
            startUtc,
            endUtc);

    private IReadOnlyList<WebhookDeadLetterEntry> ReadDeadLetters(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => _runtime.Operations.WebhookDeliveries.List()
            .Where(item => item.CreatedAtUtc >= startUtc && item.CreatedAtUtc <= endUtc)
            .OrderBy(static item => item.CreatedAtUtc)
            .ToArray();

    private async Task<IReadOnlyList<Session>> ListTrajectorySessionsAsync(string? sessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var one = await _runtime.SessionManager.LoadAsync(sessionId.Trim(), ct);
            return one is null ? [] : [one];
        }

        var active = await _runtime.SessionManager.ListActiveAsync(ct);
        var metadataById = _runtime.Operations.SessionMetadata.GetAll();
        var persisted = await SessionAdminPersistedListing.ListAllMatchingSummariesAsync(
            _sessionAdminStore,
            new SessionListQuery(),
            metadataById,
            ct);

        var sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        foreach (var session in active)
            sessions[session.Id] = session;

        foreach (var summary in persisted)
        {
            ct.ThrowIfCancellationRequested();
            if (sessions.ContainsKey(summary.Id))
                continue;

            var session = await _runtime.SessionManager.LoadAsync(summary.Id, ct);
            if (session is not null)
                sessions[session.Id] = session;
        }

        return sessions.Values.ToArray();
    }

    private static async Task WriteTrajectoryRecordAsync(StreamWriter writer, TrajectoryExportRecord record, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(record, CoreJsonContext.Default.TrajectoryExportRecord).AsMemory(), ct);
    }

    private TrajectoryExportRecord BuildMessageTrajectoryRecord(Session session, ChatTurn turn, int turnIndex, bool anonymize)
    {
        var isAssistant = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase);
        return new TrajectoryExportRecord
        {
            Type = isAssistant ? "response" : "prompt",
            TimestampUtc = turn.Timestamp,
            SessionId = ExportSessionId(session.Id, anonymize),
            ChannelId = ExportSessionId(session.ChannelId, anonymize),
            SenderId = ExportSessionId(session.SenderId, anonymize),
            TurnIndex = turnIndex,
            Role = turn.Role,
            Content = ExportText(turn.Content, anonymize, _redaction),
            Anonymized = anonymize
        };
    }

    private TrajectoryExportRecord BuildToolCallTrajectoryRecord(
        Session session,
        ChatTurn turn,
        int turnIndex,
        ToolInvocation toolCall,
        bool anonymize)
        => new()
        {
            Type = "tool_call",
            TimestampUtc = turn.Timestamp,
            SessionId = ExportSessionId(session.Id, anonymize),
            ChannelId = ExportSessionId(session.ChannelId, anonymize),
            SenderId = ExportSessionId(session.SenderId, anonymize),
            TurnIndex = turnIndex,
            ToolName = toolCall.ToolName,
            CallId = ExportOptionalId(toolCall.CallId, anonymize),
            Arguments = ExportText(toolCall.Arguments, anonymize, _redaction),
            Anonymized = anonymize
        };

    private TrajectoryExportRecord BuildToolResultTrajectoryRecord(
        Session session,
        ChatTurn turn,
        int turnIndex,
        ToolInvocation toolCall,
        bool anonymize)
        => new()
        {
            Type = "tool_result",
            TimestampUtc = turn.Timestamp,
            SessionId = ExportSessionId(session.Id, anonymize),
            ChannelId = ExportSessionId(session.ChannelId, anonymize),
            SenderId = ExportSessionId(session.SenderId, anonymize),
            TurnIndex = turnIndex,
            ToolName = toolCall.ToolName,
            CallId = ExportOptionalId(toolCall.CallId, anonymize),
            Result = ExportText(toolCall.Result, anonymize, _redaction),
            DurationMs = (long)Math.Max(0, toolCall.Duration.TotalMilliseconds),
            ResultStatus = toolCall.ResultStatus,
            FailureCode = toolCall.FailureCode,
            FailureMessage = ExportText(toolCall.FailureMessage, anonymize, _redaction),
            Anonymized = anonymize
        };

    private TrajectoryExportRecord BuildEvidenceTrajectoryRecord(Session session, EvidenceBundle bundle, bool anonymize)
        => new()
        {
            Type = "evidence_bundle",
            TimestampUtc = bundle.UpdatedAtUtc == default ? bundle.CreatedAtUtc : bundle.UpdatedAtUtc,
            SessionId = ExportSessionId(session.Id, anonymize),
            ChannelId = ExportSessionId(session.ChannelId, anonymize),
            SenderId = ExportSessionId(session.SenderId, anonymize),
            TurnIndex = -1,
            EvidenceBundle = ExportEvidenceBundle(bundle, anonymize),
            Anonymized = anonymize
        };

    private EvidenceBundle ExportEvidenceBundle(EvidenceBundle bundle, bool anonymize)
    {
        if (!anonymize)
            return bundle;

        return new EvidenceBundle
        {
            Id = ExportSessionId(bundle.Id, anonymize),
            Title = ExportText(bundle.Title, anonymize, _redaction) ?? "",
            Summary = ExportText(bundle.Summary, anonymize, _redaction) ?? "",
            CreatedAtUtc = bundle.CreatedAtUtc,
            UpdatedAtUtc = bundle.UpdatedAtUtc,
            SourceSessionId = ExportOptionalId(bundle.SourceSessionId, anonymize),
            HarnessContractId = ExportOptionalId(bundle.HarnessContractId, anonymize),
            LearningProposalId = ExportOptionalId(bundle.LearningProposalId, anonymize),
            ToolCallId = ExportOptionalId(bundle.ToolCallId, anonymize),
            AutomationRunId = ExportOptionalId(bundle.AutomationRunId, anonymize),
            ActorId = ExportOptionalId(bundle.ActorId, anonymize),
            ChannelId = ExportOptionalId(bundle.ChannelId, anonymize),
            SenderId = ExportOptionalId(bundle.SenderId, anonymize),
            Confidence = bundle.Confidence,
            Items = bundle.Items.Select(item => ExportEvidenceItem(item, anonymize)).ToArray(),
            Checks = bundle.Checks.Select(check => ExportEvidenceCheck(check, anonymize)).ToArray(),
            Risks = bundle.Risks.Select(risk => ExportEvidenceRisk(risk, anonymize)).ToArray(),
            Assumptions = bundle.Assumptions.Select(assumption => new EvidenceAssumption
            {
                Id = ExportSessionId(assumption.Id, anonymize),
                Text = ExportText(assumption.Text, anonymize, _redaction) ?? "",
                Verified = assumption.Verified,
                EvidenceItemId = ExportOptionalId(assumption.EvidenceItemId, anonymize)
            }).ToArray(),
            UntestedAreas = bundle.UntestedAreas.Select(area => new EvidenceUntestedArea
            {
                Id = ExportSessionId(area.Id, anonymize),
                Description = ExportText(area.Description, anonymize, _redaction) ?? "",
                Reason = ExportText(area.Reason, anonymize, _redaction),
                RiskLevel = area.RiskLevel
            }).ToArray(),
            HumanReviews = bundle.HumanReviews.Select(review => new EvidenceHumanReview
            {
                Reviewer = ExportOptionalId(review.Reviewer, anonymize),
                Decision = ExportText(review.Decision, anonymize, _redaction),
                Notes = ExportText(review.Notes, anonymize, _redaction),
                ReviewedAtUtc = review.ReviewedAtUtc
            }).ToArray(),
            Tags = bundle.Tags
                .Select(tag => ExportText(tag, anonymize, _redaction))
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!)
                .ToArray(),
            Metadata = ExportEvidenceMetadata(bundle.Metadata, anonymize)
        };
    }

    private EvidenceItem ExportEvidenceItem(EvidenceItem item, bool anonymize)
        => new()
        {
            Id = ExportSessionId(item.Id, anonymize),
            Kind = item.Kind,
            Title = ExportText(item.Title, anonymize, _redaction) ?? "",
            Summary = ExportText(item.Summary, anonymize, _redaction) ?? "",
            Source = ExportEvidenceSource(item.Source, anonymize),
            CreatedAtUtc = item.CreatedAtUtc,
            ToolName = item.ToolName,
            ToolCallId = ExportOptionalId(item.ToolCallId, anonymize),
            RuntimeEventId = ExportOptionalId(item.RuntimeEventId, anonymize),
            AuditEventId = ExportOptionalId(item.AuditEventId, anonymize),
            Status = item.Status,
            InputSummary = ExportText(item.InputSummary, anonymize, _redaction),
            OutputSummary = ExportText(item.OutputSummary, anonymize, _redaction),
            ErrorSummary = ExportText(item.ErrorSummary, anonymize, _redaction),
            RedactedPayload = ExportText(item.RedactedPayload, anonymize, _redaction),
            Metadata = ExportStringDictionary(item.Metadata, anonymize)
        };

    private EvidenceCheck ExportEvidenceCheck(EvidenceCheck check, bool anonymize)
        => new()
        {
            Id = ExportSessionId(check.Id, anonymize),
            Name = ExportText(check.Name, anonymize, _redaction) ?? "",
            Kind = check.Kind,
            Required = check.Required,
            Status = check.Status,
            StartedAtUtc = check.StartedAtUtc,
            CompletedAtUtc = check.CompletedAtUtc,
            Summary = ExportText(check.Summary, anonymize, _redaction) ?? "",
            Details = ExportText(check.Details, anonymize, _redaction),
            Command = ExportText(check.Command, anonymize, _redaction),
            ExitCode = check.ExitCode,
            Error = ExportText(check.Error, anonymize, _redaction)
        };

    private EvidenceRisk ExportEvidenceRisk(EvidenceRisk risk, bool anonymize)
        => new()
        {
            RiskLevel = risk.RiskLevel,
            Description = ExportText(risk.Description, anonymize, _redaction) ?? "",
            Mitigation = ExportText(risk.Mitigation, anonymize, _redaction),
            Accepted = risk.Accepted,
            AcceptedBy = ExportOptionalId(risk.AcceptedBy, anonymize),
            AcceptedAtUtc = risk.AcceptedAtUtc
        };

    private EvidenceSource? ExportEvidenceSource(EvidenceSource? source, bool anonymize)
        => source is null
            ? null
            : new EvidenceSource
            {
                Kind = source.Kind,
                Id = ExportOptionalId(source.Id, anonymize),
                Path = ExportText(source.Path, anonymize, _redaction),
                Uri = ExportText(source.Uri, anonymize, _redaction),
                Description = ExportText(source.Description, anonymize, _redaction)
            };

    private EvidenceBundleMetadata? ExportEvidenceMetadata(EvidenceBundleMetadata? metadata, bool anonymize)
        => metadata is null
            ? null
            : new EvidenceBundleMetadata
            {
                CreatedBy = ExportOptionalId(metadata.CreatedBy, anonymize),
                Source = ExportText(metadata.Source, anonymize, _redaction),
                CorrelationId = ExportOptionalId(metadata.CorrelationId, anonymize),
                Properties = ExportStringDictionary(metadata.Properties, anonymize)
            };

    private Dictionary<string, string> ExportStringDictionary(Dictionary<string, string>? values, bool anonymize)
        => values is null
            ? []
            : values.ToDictionary(
                static pair => pair.Key,
                pair => ExportText(pair.Value, anonymize, _redaction) ?? "",
                StringComparer.Ordinal);

    private static string ExportSessionId(string value, bool anonymize)
        => anonymize ? $"anon_{HashForExport(value)}" : value;

    private static string? ExportOptionalId(string? value, bool anonymize)
        => string.IsNullOrWhiteSpace(value) ? value : ExportSessionId(value, anonymize);

    private static string? ExportText(string? value, bool anonymize, IRedactionPipeline redaction)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var text = redaction.Redact(value);
        if (!anonymize)
            return text;

        text = EmailRegex.Replace(text, "[email]");
        text = PhoneRegex.Replace(text, "[phone]");
        text = SecretRegex.Replace(text, "[secret]");
        text = JsonSecretRegex.Replace(text, "$1[secret]$2");
        return text;
    }

    private static string HashForExport(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private async Task<OperatorInsightsSessionCounts> BuildSessionCountsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct)
    {
        var activeSessions = await _runtime.SessionManager.ListActiveAsync(ct);
        var metadataById = _runtime.Operations.SessionMetadata.GetAll();
        var persisted = await SessionAdminPersistedListing.ListAllMatchingSummariesAsync(
            _sessionAdminStore,
            new SessionListQuery(),
            metadataById,
            ct);
        var byId = new Dictionary<string, SessionSummary>(StringComparer.Ordinal);

        foreach (var item in persisted)
            byId[item.Id] = item;

        foreach (var session in activeSessions)
        {
            byId[session.Id] = new SessionSummary
            {
                Id = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                StableSessionId = session.StableSessionBinding?.ExternalSessionId,
                StableSessionNamespace = session.StableSessionBinding?.Namespace,
                StableSessionOwnerKey = session.StableSessionBinding?.OwnerKey,
                CreatedAt = session.CreatedAt,
                LastActiveAt = session.LastActiveAt,
                State = session.State,
                HistoryTurns = session.History.Count,
                TotalInputTokens = session.TotalInputTokens,
                TotalOutputTokens = session.TotalOutputTokens,
                IsActive = true
            };
        }

        var all = byId.Values.ToArray();
        var now = DateTimeOffset.UtcNow;
        return new OperatorInsightsSessionCounts
        {
            Active = activeSessions.Count,
            Persisted = persisted.Count,
            UniqueTotal = all.Length,
            Last24Hours = all.Count(item => item.LastActiveAt >= now.AddDays(-1)),
            Last7Days = all.Count(item => item.LastActiveAt >= now.AddDays(-7)),
            InRange = all.Count(item => item.LastActiveAt >= startUtc && item.LastActiveAt <= endUtc),
            ByChannel = BuildMetrics(
                all.GroupBy(static item => item.ChannelId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => ($"channel:{group.Key}", group.Key, group.Count()))),
            ByState = BuildMetrics(
                all.GroupBy(static item => item.State.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Select(static group => ($"state:{group.Key}", group.Key, group.Count())))
        };
    }

    private async Task<(int Total, int Failing, int RanRecently)> BuildAutomationCountsAsync(CancellationToken ct)
    {
        var items = await _automationService.ListAsync(ct);
        var failing = 0;
        var ranRecently = 0;
        foreach (var item in items)
        {
            var state = await _automationService.GetRunStateAsync(item.Id, ct);
            if (state is null)
                continue;

            if (string.Equals(state.HealthState, AutomationHealthStates.Degraded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state.HealthState, AutomationHealthStates.Quarantined, StringComparison.OrdinalIgnoreCase))
            {
                failing++;
            }

            if (state.LastRunAtUtc is not null && state.LastRunAtUtc >= DateTimeOffset.UtcNow.AddDays(-1))
                ranRecently++;
        }

        return (items.Count, failing, ranRecently);
    }

    private static IReadOnlyList<(string Key, int Count)> BuildApprovalLatencies(IReadOnlyList<ApprovalHistoryEntry> approvals)
    {
        var lifecycle = BuildApprovalLifecycle(approvals);
        var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["lt_1m"] = 0,
            ["1m_to_5m"] = 0,
            ["5m_to_15m"] = 0,
            ["gt_15m"] = 0
        };

        foreach (var item in lifecycle.Values)
        {
            if (item.CreatedAtUtc is null || item.DecisionAtUtc is null)
                continue;

            var latency = item.DecisionAtUtc.Value - item.CreatedAtUtc.Value;
            var bucket = latency.TotalMinutes switch
            {
                < 1 => "lt_1m",
                < 5 => "1m_to_5m",
                < 15 => "5m_to_15m",
                _ => "gt_15m"
            };
            buckets[bucket]++;
        }

        return buckets.Select(static item => (item.Key, item.Value)).ToArray();
    }

    private static Dictionary<string, ApprovalLifecycle> BuildApprovalLifecycle(IReadOnlyList<ApprovalHistoryEntry> approvals)
    {
        var lifecycle = new Dictionary<string, ApprovalLifecycle>(StringComparer.Ordinal);
        foreach (var item in approvals.OrderBy(static item => item.TimestampUtc))
        {
            if (!lifecycle.TryGetValue(item.ApprovalId, out var state))
            {
                state = new ApprovalLifecycle();
                lifecycle[item.ApprovalId] = state;
            }

            if (string.Equals(item.EventType, "created", StringComparison.OrdinalIgnoreCase))
                state.CreatedAtUtc ??= item.TimestampUtc;

            if (string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase))
                state.DecisionAtUtc = item.DecisionAtUtc ?? item.TimestampUtc;
        }

        return lifecycle;
    }

    private static int CountPendingApprovals(Dictionary<string, ApprovalLifecycle> lifecycle, DateTimeOffset thresholdUtc)
        => lifecycle.Values.Count(item => item.CreatedAtUtc is not null && item.CreatedAtUtc <= thresholdUtc && (item.DecisionAtUtc is null || item.DecisionAtUtc > thresholdUtc));

    private (DateTimeOffset StartUtc, DateTimeOffset EndUtc, string[] Warnings) NormalizeRange(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        TimeSpan defaultWindow,
        bool applyRetention)
    {
        var warnings = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var endUtc = toUtc ?? now;
        var startUtc = fromUtc ?? endUtc.Subtract(defaultWindow);
        if (endUtc < startUtc)
            (startUtc, endUtc) = (endUtc, startUtc);

        if (endUtc > now)
        {
            endUtc = now;
            warnings.Add("Requested end time was in the future and was clamped to now.");
        }

        if (applyRetention)
        {
            var policy = _organizationPolicy.GetSnapshot();
            var retentionFloor = now.AddDays(-policy.ExportRetentionDays);
            if (startUtc < retentionFloor)
            {
                startUtc = retentionFloor;
                warnings.Add($"Requested start time exceeded the {policy.ExportRetentionDays}-day retention window and was clamped.");
            }
        }

        return (startUtc, endUtc, warnings.ToArray());
    }

    private static IReadOnlyList<T> ReadJsonlEntries<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        Func<T, DateTimeOffset> timestampSelector,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (!File.Exists(path))
            return [];

        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var item = JsonSerializer.Deserialize(line, typeInfo);
                if (item is null)
                    continue;

                var timestamp = timestampSelector(item);
                if (timestamp < startUtc || timestamp > endUtc)
                    continue;

                items.Add(item);
            }
            catch
            {
            }
        }

        return items.OrderBy(timestampSelector).ToArray();
    }

    private static IReadOnlyList<DashboardNamedMetric> BuildMetrics(IEnumerable<(string Key, string Label, int Count)> source, int limit = 8)
        => source
            .Where(static item => item.Count > 0)
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(static item => new DashboardNamedMetric
            {
                Key = item.Key,
                Label = item.Label,
                Count = item.Count
            })
            .ToArray();

    private static void WriteJsonEntry<T>(ZipArchive zip, string entryName, T value, JsonTypeInfo<T> typeInfo)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, typeInfo);
    }

    private static void WriteJsonlEntry<T>(ZipArchive zip, string entryName, IEnumerable<T> items, JsonTypeInfo<T> typeInfo)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
        foreach (var item in items)
        {
            writer.WriteLine(JsonSerializer.Serialize(item, typeInfo));
        }
    }

    private static string BuildRouteKey(ProviderRouteHealthSnapshot item)
        => $"{item.ProviderId}:{item.ModelId}";

    private static string BuildRouteLabel(ProviderRouteHealthSnapshot item)
        => string.IsNullOrWhiteSpace(item.ProfileId)
            ? $"{item.ProviderId}/{item.ModelId}"
            : $"{item.ProfileId} ({item.ProviderId}/{item.ModelId})";

    private static int DistinctActorCount(IReadOnlyList<OperatorAuditEntry> items)
        => items.Select(BuildActorLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string BuildActorLabel(OperatorAuditEntry item)
        => string.IsNullOrWhiteSpace(item.ActorDisplayName)
            ? item.ActorId
            : $"{item.ActorDisplayName} ({item.ActorId})";

    private static int ToCount(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private decimal EstimateCostUsd(ProviderUsageSnapshot usage)
    {
        var rate = TokenCostRateResolver.Resolve(_startup.Config, usage.ProviderId, usage.ModelId);
        var inputCost = (decimal)usage.InputTokens / 1000m * rate.InputUsdPer1K;
        var outputCost = (decimal)usage.OutputTokens / 1000m * rate.OutputUsdPer1K;
        return Math.Round(inputCost + outputCost, 6, MidpointRounding.AwayFromZero);
    }

    private sealed class ApprovalLifecycle
    {
        public DateTimeOffset? CreatedAtUtc { get; set; }
        public DateTimeOffset? DecisionAtUtc { get; set; }
    }
}
