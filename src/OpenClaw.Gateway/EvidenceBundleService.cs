using System.Linq;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class EvidenceBundleService
{
    private readonly IEvidenceBundleStore _store;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly ILogger<EvidenceBundleService> _logger;

    public EvidenceBundleService(
        IEvidenceBundleStore store,
        RuntimeEventStore runtimeEvents,
        ILogger<EvidenceBundleService> logger)
    {
        _store = store;
        _runtimeEvents = runtimeEvents;
        _logger = logger;
    }

    public async ValueTask<EvidenceBundle> CreateAsync(EvidenceBundle bundle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var now = DateTimeOffset.UtcNow;
        var normalized = Normalize(bundle, now, isNew: true);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "evidence_bundle_created",
            summary: $"Created evidence bundle '{normalized.Id}'.");
        return normalized;
    }

    public async ValueTask<EvidenceBundle> SaveAsync(EvidenceBundle bundle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var normalized = Normalize(bundle, DateTimeOffset.UtcNow, isNew: false);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "evidence_bundle_updated",
            summary: $"Updated evidence bundle '{normalized.Id}'.");
        return normalized;
    }

    public ValueTask<EvidenceBundle?> GetAsync(string id, CancellationToken ct)
        => _store.GetAsync(id, ct);

    public ValueTask<IReadOnlyList<EvidenceBundle>> ListAsync(EvidenceBundleListQuery query, CancellationToken ct)
        => _store.ListAsync(query, ct);

    public async ValueTask<EvidenceBundle?> AddItemAsync(string bundleId, EvidenceItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existing = await _store.GetAsync(bundleId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        return await SaveAppendAsync(
            existing,
            now,
            items: [.. CleanList(existing.Items), NormalizeItem(item, CleanList(existing.Items).Count + 1, now)],
            checks: CleanList(existing.Checks),
            risks: CleanList(existing.Risks),
            reviews: CleanList(existing.HumanReviews),
            ct);
    }

    public async ValueTask<EvidenceBundle?> AddCheckAsync(string bundleId, EvidenceCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);

        var existing = await _store.GetAsync(bundleId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        return await SaveAppendAsync(
            existing,
            now,
            items: CleanList(existing.Items),
            checks: [.. CleanList(existing.Checks), NormalizeCheck(check, CleanList(existing.Checks).Count + 1)],
            risks: CleanList(existing.Risks),
            reviews: CleanList(existing.HumanReviews),
            ct);
    }

    public async ValueTask<EvidenceBundle?> AddRiskAsync(string bundleId, EvidenceRisk risk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(risk);

        var existing = await _store.GetAsync(bundleId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        return await SaveAppendAsync(
            existing,
            now,
            items: CleanList(existing.Items),
            checks: CleanList(existing.Checks),
            risks: [.. CleanList(existing.Risks), NormalizeRiskItem(risk)],
            reviews: CleanList(existing.HumanReviews),
            ct);
    }

    public async ValueTask<EvidenceBundle?> AddHumanReviewAsync(string bundleId, EvidenceHumanReview review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);

        var existing = await _store.GetAsync(bundleId, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        return await SaveAppendAsync(
            existing,
            now,
            items: CleanList(existing.Items),
            checks: CleanList(existing.Checks),
            risks: CleanList(existing.Risks),
            reviews: [.. CleanList(existing.HumanReviews), NormalizeReview(review, now)],
            ct);
    }

    public static EvidenceItem FromToolInvocation(ToolInvocation invocation, DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        return new EvidenceItem
        {
            Id = string.IsNullOrWhiteSpace(invocation.CallId) ? "" : invocation.CallId,
            Kind = EvidenceItemKinds.ToolCall,
            Title = $"Tool call: {invocation.ToolName}",
            Summary = string.IsNullOrWhiteSpace(invocation.ResultStatus)
                ? $"Tool '{invocation.ToolName}' was invoked."
                : $"Tool '{invocation.ToolName}' completed with status '{invocation.ResultStatus}'.",
            Source = new EvidenceSource { Kind = EvidenceItemKinds.ToolCall, Id = invocation.CallId },
            CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow,
            ToolName = invocation.ToolName,
            ToolCallId = invocation.CallId,
            Status = invocation.ResultStatus,
            InputSummary = invocation.Arguments,
            OutputSummary = invocation.Result,
            ErrorSummary = invocation.FailureMessage ?? invocation.FailureCode,
            Metadata = BuildMetadata(
                ("durationMs", ((long)Math.Max(0, invocation.Duration.TotalMilliseconds)).ToString()),
                ("governanceAction", invocation.GovernanceAction),
                ("governanceReason", invocation.GovernanceReason),
                ("governancePolicyId", invocation.GovernancePolicyId),
                ("governanceRuleId", invocation.GovernanceRuleId))
        };
    }

    public static EvidenceItem FromRuntimeEvent(RuntimeEventEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new EvidenceItem
        {
            Id = entry.Id,
            Kind = EvidenceItemKinds.RuntimeEvent,
            Title = $"Runtime event: {entry.Component}/{entry.Action}",
            Summary = entry.Summary,
            Source = new EvidenceSource { Kind = EvidenceItemKinds.RuntimeEvent, Id = entry.Id },
            CreatedAtUtc = entry.TimestampUtc,
            RuntimeEventId = entry.Id,
            Status = entry.Severity,
            Metadata = entry.Metadata is null
                ? []
                : new Dictionary<string, string>(entry.Metadata, StringComparer.Ordinal)
        };
    }

    public static EvidenceItem FromApprovalHistoryEntry(ApprovalHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var decision = entry.Approved is null ? entry.EventType : entry.Approved.Value ? "approved" : "rejected";
        return new EvidenceItem
        {
            Id = entry.ApprovalId,
            Kind = EvidenceItemKinds.Approval,
            Title = $"Approval {entry.EventType}: {entry.ToolName}",
            Summary = entry.Summary,
            Source = new EvidenceSource { Kind = EvidenceItemKinds.Approval, Id = entry.ApprovalId },
            CreatedAtUtc = entry.TimestampUtc,
            ToolName = entry.ToolName,
            Status = decision,
            InputSummary = entry.ArgumentsPreview,
            Metadata = BuildMetadata(
                ("eventType", entry.EventType),
                ("action", entry.Action),
                ("decisionSource", entry.DecisionSource),
                ("actorChannelId", entry.ActorChannelId),
                ("actorSenderId", entry.ActorSenderId))
        };
    }

    public static EvidenceItem FromDoctorReport(DoctorReportResponse report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new EvidenceItem
        {
            Id = $"doctor_{report.GeneratedAtUtc:yyyyMMddHHmmss}",
            Kind = EvidenceItemKinds.DoctorReport,
            Title = "Doctor report",
            Summary = $"Doctor status {report.OverallStatus}; failures={report.HasFailures}; warnings={report.HasWarnings}.",
            CreatedAtUtc = report.GeneratedAtUtc,
            Status = report.OverallStatus,
            OutputSummary = string.Join("; ", report.RecommendedNextActions ?? []),
            Metadata = BuildMetadata(
                ("checkCount", (report.Checks?.Count ?? 0).ToString()),
                ("hasSkips", report.HasSkips.ToString()))
        };
    }

    public static EvidenceItem FromPostureCheck(SecurityPostureResponse posture, DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(posture);

        return new EvidenceItem
        {
            Id = $"posture_{Guid.NewGuid():N}"[..20],
            Kind = EvidenceItemKinds.PostureCheck,
            Title = "Security posture",
            Summary = posture.RiskFlags.Count == 0
                ? "Security posture check reported no risk flags."
                : $"Security posture check reported {posture.RiskFlags.Count} risk flag(s).",
            CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow,
            Status = posture.RiskFlags.Count == 0 ? EvidenceCheckStatuses.Passed : EvidenceCheckStatuses.Warning,
            OutputSummary = string.Join("; ", posture.Recommendations ?? []),
            Metadata = BuildMetadata(
                ("publicBind", posture.PublicBind.ToString()),
                ("authTokenConfigured", posture.AuthTokenConfigured.ToString()),
                ("toolApprovalRequired", posture.ToolApprovalRequired.ToString()),
                ("riskFlags", string.Join(",", posture.RiskFlags ?? [])))
        };
    }

    private async ValueTask<EvidenceBundle> SaveAppendAsync(
        EvidenceBundle existing,
        DateTimeOffset now,
        IReadOnlyList<EvidenceItem> items,
        IReadOnlyList<EvidenceCheck> checks,
        IReadOnlyList<EvidenceRisk> risks,
        IReadOnlyList<EvidenceHumanReview> reviews,
        CancellationToken ct)
    {
        var updated = Copy(
            existing,
            existing.Id,
            NormalizeConfidence(existing.Confidence),
            existing.CreatedAtUtc == default ? now : existing.CreatedAtUtc,
            now,
            items,
            checks,
            risks,
            CleanList(existing.Assumptions),
            CleanList(existing.UntestedAreas),
            reviews);
        await _store.SaveAsync(updated, ct);
        AppendEvent(
            updated,
            action: "evidence_bundle_updated",
            summary: $"Updated evidence bundle '{updated.Id}'.");
        return updated;
    }

    private EvidenceBundle Normalize(EvidenceBundle bundle, DateTimeOffset now, bool isNew)
    {
        var id = string.IsNullOrWhiteSpace(bundle.Id)
            ? $"evb_{Guid.NewGuid():N}"[..24]
            : bundle.Id.Trim();
        var createdAt = bundle.CreatedAtUtc == default || isNew ? now : bundle.CreatedAtUtc;
        var items = CleanList(bundle.Items)
            .Select((item, index) => NormalizeItem(item, index + 1, now))
            .ToArray();
        var checks = CleanList(bundle.Checks)
            .Select((check, index) => NormalizeCheck(check, index + 1))
            .ToArray();

        return Copy(
            bundle,
            id,
            NormalizeConfidence(bundle.Confidence),
            createdAt,
            now,
            items,
            checks,
            CleanList(bundle.Risks).Select(NormalizeRiskItem).ToArray(),
            CleanList(bundle.Assumptions).Select((item, index) => NormalizeAssumption(item, index + 1)).ToArray(),
            CleanList(bundle.UntestedAreas).Select((item, index) => NormalizeUntestedArea(item, index + 1)).ToArray(),
            CleanList(bundle.HumanReviews).Select(review => NormalizeReview(review, now)).ToArray());
    }

    private static EvidenceBundle Copy(
        EvidenceBundle source,
        string id,
        string confidence,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<EvidenceItem> items,
        IReadOnlyList<EvidenceCheck> checks,
        IReadOnlyList<EvidenceRisk> risks,
        IReadOnlyList<EvidenceAssumption> assumptions,
        IReadOnlyList<EvidenceUntestedArea> untestedAreas,
        IReadOnlyList<EvidenceHumanReview> humanReviews)
        => new()
        {
            Id = id,
            Title = source.Title,
            Summary = source.Summary,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            SourceSessionId = source.SourceSessionId,
            HarnessContractId = source.HarnessContractId,
            LearningProposalId = source.LearningProposalId,
            ToolCallId = source.ToolCallId,
            AutomationRunId = source.AutomationRunId,
            ActorId = source.ActorId,
            ChannelId = source.ChannelId,
            SenderId = source.SenderId,
            Confidence = confidence,
            Items = items,
            Checks = checks,
            Risks = risks,
            Assumptions = assumptions,
            UntestedAreas = untestedAreas,
            HumanReviews = humanReviews,
            Tags = CleanStrings(source.Tags),
            Metadata = source.Metadata
        };

    private static EvidenceItem NormalizeItem(EvidenceItem item, int index, DateTimeOffset now)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? $"item_{index}" : item.Id.Trim(),
            Kind = NormalizeItemKind(item.Kind),
            Title = item.Title,
            Summary = item.Summary,
            Source = item.Source,
            CreatedAtUtc = item.CreatedAtUtc == default ? now : item.CreatedAtUtc,
            ToolName = item.ToolName,
            ToolCallId = item.ToolCallId,
            RuntimeEventId = item.RuntimeEventId,
            AuditEventId = item.AuditEventId,
            Status = item.Status,
            InputSummary = item.InputSummary,
            OutputSummary = item.OutputSummary,
            ErrorSummary = item.ErrorSummary,
            RedactedPayload = item.RedactedPayload,
            Metadata = item.Metadata is null
                ? []
                : new Dictionary<string, string>(item.Metadata.Where(static item => !string.IsNullOrWhiteSpace(item.Key)), StringComparer.Ordinal)
        };

    private static EvidenceCheck NormalizeCheck(EvidenceCheck check, int index)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(check.Id) ? $"check_{index}" : check.Id.Trim(),
            Name = check.Name,
            Kind = check.Kind,
            Required = check.Required,
            Status = NormalizeCheckStatus(check.Status),
            StartedAtUtc = check.StartedAtUtc,
            CompletedAtUtc = check.CompletedAtUtc,
            Summary = check.Summary,
            Details = check.Details,
            Command = check.Command,
            ExitCode = check.ExitCode,
            Error = check.Error
        };

    private static EvidenceRisk NormalizeRiskItem(EvidenceRisk risk)
        => new()
        {
            RiskLevel = NormalizeRiskLevel(risk.RiskLevel),
            Description = risk.Description,
            Mitigation = risk.Mitigation,
            Accepted = risk.Accepted,
            AcceptedBy = risk.AcceptedBy,
            AcceptedAtUtc = risk.AcceptedAtUtc
        };

    private static EvidenceAssumption NormalizeAssumption(EvidenceAssumption assumption, int index)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(assumption.Id) ? $"assumption_{index}" : assumption.Id.Trim(),
            Text = assumption.Text,
            Verified = assumption.Verified,
            EvidenceItemId = assumption.EvidenceItemId
        };

    private static EvidenceUntestedArea NormalizeUntestedArea(EvidenceUntestedArea area, int index)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(area.Id) ? $"untested_{index}" : area.Id.Trim(),
            Description = area.Description,
            Reason = area.Reason,
            RiskLevel = string.IsNullOrWhiteSpace(area.RiskLevel) ? null : NormalizeRiskLevel(area.RiskLevel)
        };

    private static EvidenceHumanReview NormalizeReview(EvidenceHumanReview review, DateTimeOffset now)
        => new()
        {
            Reviewer = review.Reviewer,
            Decision = review.Decision,
            Notes = review.Notes,
            ReviewedAtUtc = review.ReviewedAtUtc == default ? now : review.ReviewedAtUtc
        };

    private void AppendEvent(EvidenceBundle bundle, string action, string summary)
    {
        try
        {
            _runtimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = bundle.SourceSessionId,
                ChannelId = bundle.ChannelId,
                SenderId = bundle.SenderId,
                CorrelationId = bundle.Id,
                Component = "harness",
                Action = action,
                Severity = "info",
                Summary = summary,
                Metadata = new Dictionary<string, string>
                {
                    ["evidenceBundleId"] = bundle.Id,
                    ["confidence"] = bundle.Confidence
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to append evidence bundle runtime event for {BundleId}.", bundle.Id);
        }
    }

    private static IReadOnlyList<T> CleanList<T>(IReadOnlyList<T>? items)
        where T : class
        => items?.Where(static item => item is not null).ToArray() ?? [];

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? items)
        => items?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToArray() ?? [];

    private static string NormalizeConfidence(string? confidence)
        => string.IsNullOrWhiteSpace(confidence)
            ? EvidenceConfidenceLevels.Unknown
            : confidence.Trim().ToLowerInvariant() switch
            {
                EvidenceConfidenceLevels.Unknown => EvidenceConfidenceLevels.Unknown,
                EvidenceConfidenceLevels.Low => EvidenceConfidenceLevels.Low,
                EvidenceConfidenceLevels.Medium => EvidenceConfidenceLevels.Medium,
                EvidenceConfidenceLevels.High => EvidenceConfidenceLevels.High,
                _ => throw new ArgumentException($"Unsupported evidence confidence '{confidence}'.", nameof(confidence))
            };

    private static string NormalizeItemKind(string? kind)
        => string.IsNullOrWhiteSpace(kind)
            ? EvidenceItemKinds.Unknown
            : kind.Trim().ToLowerInvariant() switch
            {
                EvidenceItemKinds.ToolCall => EvidenceItemKinds.ToolCall,
                EvidenceItemKinds.TestResult => EvidenceItemKinds.TestResult,
                EvidenceItemKinds.BuildResult => EvidenceItemKinds.BuildResult,
                EvidenceItemKinds.StaticAnalysis => EvidenceItemKinds.StaticAnalysis,
                EvidenceItemKinds.SecurityCheck => EvidenceItemKinds.SecurityCheck,
                EvidenceItemKinds.Approval => EvidenceItemKinds.Approval,
                EvidenceItemKinds.HumanReview => EvidenceItemKinds.HumanReview,
                EvidenceItemKinds.RuntimeEvent => EvidenceItemKinds.RuntimeEvent,
                EvidenceItemKinds.AuditEvent => EvidenceItemKinds.AuditEvent,
                EvidenceItemKinds.DoctorReport => EvidenceItemKinds.DoctorReport,
                EvidenceItemKinds.PostureCheck => EvidenceItemKinds.PostureCheck,
                EvidenceItemKinds.ModelResponse => EvidenceItemKinds.ModelResponse,
                EvidenceItemKinds.MemoryLookup => EvidenceItemKinds.MemoryLookup,
                EvidenceItemKinds.VerificationResult => EvidenceItemKinds.VerificationResult,
                EvidenceItemKinds.Note => EvidenceItemKinds.Note,
                EvidenceItemKinds.Unknown => EvidenceItemKinds.Unknown,
                _ => throw new ArgumentException($"Unsupported evidence item kind '{kind}'.", nameof(kind))
            };

    private static string NormalizeCheckStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? EvidenceCheckStatuses.Unknown
            : status.Trim().ToLowerInvariant() switch
            {
                EvidenceCheckStatuses.NotRun => EvidenceCheckStatuses.NotRun,
                EvidenceCheckStatuses.Running => EvidenceCheckStatuses.Running,
                EvidenceCheckStatuses.Passed => EvidenceCheckStatuses.Passed,
                EvidenceCheckStatuses.Failed => EvidenceCheckStatuses.Failed,
                EvidenceCheckStatuses.Skipped => EvidenceCheckStatuses.Skipped,
                EvidenceCheckStatuses.Warning => EvidenceCheckStatuses.Warning,
                EvidenceCheckStatuses.Unknown => EvidenceCheckStatuses.Unknown,
                _ => throw new ArgumentException($"Unsupported evidence check status '{status}'.", nameof(status))
            };

    private static string NormalizeRiskLevel(string? riskLevel)
        => string.IsNullOrWhiteSpace(riskLevel)
            ? EvidenceRiskLevels.Unknown
            : riskLevel.Trim().ToLowerInvariant() switch
            {
                EvidenceRiskLevels.Unknown => EvidenceRiskLevels.Unknown,
                EvidenceRiskLevels.Low => EvidenceRiskLevels.Low,
                EvidenceRiskLevels.Medium => EvidenceRiskLevels.Medium,
                EvidenceRiskLevels.High => EvidenceRiskLevels.High,
                EvidenceRiskLevels.Critical => EvidenceRiskLevels.Critical,
                _ => throw new ArgumentException($"Unsupported evidence risk level '{riskLevel}'.", nameof(riskLevel))
            };

    private static Dictionary<string, string> BuildMetadata(params (string Key, string? Value)[] items)
        => items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.Ordinal);
}
