using System.Linq;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class GovernanceLedgerService
{
    private const int MaxArgumentSummaryChars = 800;

    private readonly IGovernanceLedgerStore _store;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly IRedactionPipeline _redaction;
    private readonly ILogger<GovernanceLedgerService> _logger;

    public GovernanceLedgerService(
        IGovernanceLedgerStore store,
        RuntimeEventStore runtimeEvents,
        IRedactionPipeline? redaction,
        ILogger<GovernanceLedgerService> logger)
    {
        _store = store;
        _runtimeEvents = runtimeEvents;
        _redaction = redaction ?? new NoopRedactionPipeline();
        _logger = logger;
    }

    public ValueTask<GovernanceLedgerEntry> CreateAsync(GovernanceLedgerEntry entry, CancellationToken ct)
        => RecordDecisionAsync(entry, ct);

    public async ValueTask<GovernanceLedgerEntry> SaveAsync(GovernanceLedgerEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = Normalize(entry, DateTimeOffset.UtcNow, isNew: false);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "governance_ledger_entry_recorded",
            severity: EventSeverity(normalized),
            summary: $"Recorded governance ledger entry '{normalized.Id}'.");
        return normalized;
    }

    public async ValueTask<GovernanceLedgerEntry> RecordDecisionAsync(GovernanceLedgerEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var normalized = Normalize(entry, DateTimeOffset.UtcNow, isNew: true);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "governance_ledger_entry_recorded",
            severity: EventSeverity(normalized),
            summary: $"Recorded governance ledger entry '{normalized.Id}'.");
        return normalized;
    }

    public ValueTask<GovernanceLedgerEntry> RecordApprovalAsync(
        ToolApprovalRequest request,
        string source,
        string? decidedBy,
        string? actorChannelId,
        string? actorSenderId,
        CancellationToken ct)
        => RecordDecisionAsync(FromToolApproval(
            request,
            decision: GovernanceDecisions.Approved,
            status: GovernanceDecisionStatuses.Active,
            source,
            decidedBy,
            actorChannelId,
            actorSenderId,
            reason: "approved"), ct);

    public ValueTask<GovernanceLedgerEntry> RecordRejectionAsync(
        ToolApprovalRequest request,
        string source,
        string? decidedBy,
        string? actorChannelId,
        string? actorSenderId,
        CancellationToken ct)
        => RecordDecisionAsync(FromToolApproval(
            request,
            decision: GovernanceDecisions.Rejected,
            status: GovernanceDecisionStatuses.Active,
            source,
            decidedBy,
            actorChannelId,
            actorSenderId,
            reason: "rejected"), ct);

    public ValueTask<GovernanceLedgerEntry> RecordExpiredAsync(
        ToolApprovalRequest request,
        string source,
        string? decidedBy,
        CancellationToken ct)
        => RecordDecisionAsync(FromToolApproval(
            request,
            decision: GovernanceDecisions.Expired,
            status: GovernanceDecisionStatuses.Expired,
            source,
            decidedBy,
            actorChannelId: null,
            actorSenderId: null,
            reason: "approval timed out"), ct);

    public ValueTask<GovernanceLedgerEntry?> GetAsync(string id, CancellationToken ct)
        => _store.GetAsync(id, ct);

    public ValueTask<IReadOnlyList<GovernanceLedgerEntry>> ListAsync(GovernanceLedgerListQuery query, CancellationToken ct)
        => _store.ListAsync(query, ct);

    public async ValueTask<GovernanceLedgerEntry?> RevokeAsync(string id, string revokedBy, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revokedBy))
            throw new ArgumentException("Governance ledger revocation actor is required.", nameof(revokedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Governance ledger revocation reason is required.", nameof(reason));

        var revoked = await _store.RevokeAsync(
            id,
            revokedBy.Trim(),
            reason.Trim(),
            ct);
        if (revoked is null)
            return null;

        AppendEvent(
            revoked,
            action: "governance_ledger_entry_revoked",
            severity: "warning",
            summary: $"Revoked governance ledger entry '{revoked.Id}'.");
        return revoked;
    }

    public async ValueTask TryRecordApprovalDecisionAsync(
        ToolApprovalRequest request,
        bool approved,
        string source,
        string? decidedBy,
        string? actorChannelId,
        string? actorSenderId,
        CancellationToken ct)
    {
        try
        {
            if (approved)
                await RecordApprovalAsync(request, source, decidedBy, actorChannelId, actorSenderId, ct);
            else
                await RecordRejectionAsync(request, source, decidedBy, actorChannelId, actorSenderId, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record governance ledger approval decision for {ApprovalId}.", request.ApprovalId);
        }
    }

    public async ValueTask TryRecordExpiredAsync(
        ToolApprovalRequest request,
        string source,
        string? decidedBy,
        CancellationToken ct)
    {
        try
        {
            await RecordExpiredAsync(request, source, decidedBy, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record governance ledger timeout for {ApprovalId}.", request.ApprovalId);
        }
    }

    public async ValueTask TryRecordApprovalGrantConsumedAsync(
        ToolApprovalGrant grant,
        ToolApprovalRequest request,
        CancellationToken ct)
    {
        try
        {
            var scope = NormalizeGrantScope(grant.Scope);
            await RecordDecisionAsync(new GovernanceLedgerEntry
            {
                Id = $"gov_{Guid.NewGuid():N}"[..24],
                Decision = GovernanceDecisions.Approved,
                Status = GovernanceDecisionStatuses.Active,
                Source = GovernanceLedgerSources.ApprovalGrantConsumed,
                ToolName = request.ToolName,
                ActionType = request.Action,
                ActionSummary = string.IsNullOrWhiteSpace(request.Summary)
                    ? $"Reusable approval grant '{grant.Id}' applied for tool '{request.ToolName}'."
                    : request.Summary,
                ArgumentSummary = request.Arguments,
                RedactedArguments = request.Arguments,
                RiskLevel = DeriveRiskLevel(request),
                Scope = scope,
                ScopeKey = ScopeKeyForGrant(grant, scope),
                SessionId = request.SessionId,
                ApprovalId = grant.Id,
                ActorId = grant.GrantedBy,
                ChannelId = request.ChannelId,
                SenderId = request.SenderId,
                DecidedBy = grant.GrantedBy,
                DecisionReason = "approval grant consumed",
                Tags = ["approval", "approval_grant"],
                Metadata = new GovernanceLedgerMetadata
                {
                    CorrelationId = grant.Id,
                    Properties = BuildMetadata(
                        ("grantId", grant.Id),
                        ("grantScope", grant.Scope),
                        ("grantSessionId", grant.SessionId),
                        ("grantChannelId", grant.ChannelId),
                        ("grantSenderId", grant.SenderId),
                        ("grantSource", grant.GrantSource),
                        ("isMutation", request.IsMutation ? "true" : "false"))
                }
            }, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record governance ledger approval grant consumption for {GrantId}.", grant.Id);
        }
    }

    public GovernanceLedgerEntry FromToolApproval(
        ToolApprovalRequest request,
        string decision,
        string status,
        string source,
        string? decidedBy,
        string? actorChannelId,
        string? actorSenderId,
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new GovernanceLedgerEntry
        {
            Id = $"gov_{Guid.NewGuid():N}"[..24],
            Decision = decision,
            Status = status,
            Source = source,
            ToolName = request.ToolName,
            ActionType = request.Action,
            ActionSummary = string.IsNullOrWhiteSpace(request.Summary)
                ? $"Tool approval decision for '{request.ToolName}'."
                : request.Summary,
            ArgumentSummary = request.Arguments,
            RedactedArguments = request.Arguments,
            RiskLevel = DeriveRiskLevel(request),
            Scope = GovernanceScopes.Once,
            ScopeKey = request.ApprovalId,
            SessionId = request.SessionId,
            ApprovalId = request.ApprovalId,
            ActorId = actorSenderId ?? decidedBy,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            DecidedBy = decidedBy ?? actorSenderId,
            DecisionReason = reason,
            Tags = ["approval"],
            Metadata = new GovernanceLedgerMetadata
            {
                CorrelationId = request.ApprovalId,
                Properties = BuildMetadata(
                    ("actorChannelId", actorChannelId),
                    ("actorSenderId", actorSenderId),
                    ("isMutation", request.IsMutation ? "true" : "false"))
            }
        };
    }

    private GovernanceLedgerEntry Normalize(GovernanceLedgerEntry entry, DateTimeOffset now, bool isNew)
    {
        var id = string.IsNullOrWhiteSpace(entry.Id)
            ? $"gov_{Guid.NewGuid():N}"[..24]
            : entry.Id.Trim();
        var createdAt = entry.CreatedAtUtc == default || isNew ? now : entry.CreatedAtUtc;
        var decision = NormalizeDecision(entry.Decision);
        var status = string.IsNullOrWhiteSpace(entry.Status)
            ? StatusForDecision(decision)
            : NormalizeStatus(entry.Status);
        var redactedArgs = string.IsNullOrWhiteSpace(entry.RedactedArguments)
            ? null
            : _redaction.Redact(entry.RedactedArguments);
        var argumentSummary = string.IsNullOrWhiteSpace(entry.ArgumentSummary)
            ? Truncate(redactedArgs)
            : Truncate(_redaction.Redact(entry.ArgumentSummary));

        return new GovernanceLedgerEntry
        {
            Id = id,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = now,
            Decision = decision,
            Status = status,
            Source = NormalizeSource(entry.Source),
            ActionType = CleanRedactedOptional(entry.ActionType),
            ToolName = CleanRedactedOptional(entry.ToolName),
            ActionSummary = CleanRedactedOptional(entry.ActionSummary) ?? "",
            ArgumentSummary = argumentSummary,
            RedactedArguments = redactedArgs,
            RiskLevel = string.IsNullOrWhiteSpace(entry.RiskLevel)
                ? GovernanceRiskLevels.Unknown
                : NormalizeRiskLevel(entry.RiskLevel),
            Scope = string.IsNullOrWhiteSpace(entry.Scope)
                ? GovernanceScopes.Unknown
                : NormalizeScope(entry.Scope),
            ScopeKey = CleanRedactedOptional(entry.ScopeKey),
            SessionId = CleanOptional(entry.SessionId),
            HarnessContractId = CleanOptional(entry.HarnessContractId),
            EvidenceBundleId = CleanOptional(entry.EvidenceBundleId),
            LearningProposalId = CleanOptional(entry.LearningProposalId),
            ApprovalId = CleanOptional(entry.ApprovalId),
            ActorId = CleanOptional(entry.ActorId),
            ChannelId = CleanOptional(entry.ChannelId),
            SenderId = CleanOptional(entry.SenderId),
            DecidedBy = CleanRedactedOptional(entry.DecidedBy),
            DecisionReason = CleanRedactedOptional(entry.DecisionReason),
            ExpiresAtUtc = entry.ExpiresAtUtc,
            RevokedAtUtc = entry.RevokedAtUtc,
            RevokedBy = CleanRedactedOptional(entry.RevokedBy),
            RevocationReason = CleanRedactedOptional(entry.RevocationReason),
            PolicyHint = NormalizePolicyHint(entry.PolicyHint),
            Tags = CleanRedactedStrings(entry.Tags),
            Metadata = NormalizeMetadata(entry.Metadata)
        };
    }

    private void AppendEvent(GovernanceLedgerEntry entry, string action, string severity, string summary)
    {
        try
        {
            _runtimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = entry.SessionId,
                ChannelId = entry.ChannelId,
                SenderId = entry.SenderId,
                CorrelationId = entry.Id,
                Component = "harness",
                Action = action,
                Severity = severity,
                Summary = summary,
                Metadata = new Dictionary<string, string>
                {
                    ["governanceLedgerId"] = entry.Id,
                    ["decision"] = entry.Decision,
                    ["status"] = entry.Status,
                    ["riskLevel"] = entry.RiskLevel
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to append governance ledger runtime event for {EntryId}.", entry.Id);
        }
    }

    private GovernancePolicyHint? NormalizePolicyHint(GovernancePolicyHint? hint)
        => hint is null
            ? null
            : new GovernancePolicyHint
            {
                SuggestedFutureBehavior = CleanRedactedOptional(hint.SuggestedFutureBehavior),
                SuggestedScope = string.IsNullOrWhiteSpace(hint.SuggestedScope)
                    ? null
                    : NormalizeScope(hint.SuggestedScope),
                Confidence = CleanRedactedOptional(hint.Confidence),
                RequiresReview = hint.RequiresReview,
                Notes = CleanRedactedOptional(hint.Notes)
            };

    private GovernanceLedgerMetadata? NormalizeMetadata(GovernanceLedgerMetadata? metadata)
        => metadata is null
            ? null
            : new GovernanceLedgerMetadata
            {
                CreatedBy = CleanRedactedOptional(metadata.CreatedBy),
                CorrelationId = CleanOptional(metadata.CorrelationId),
                Properties = metadata.Properties is null
                    ? []
                    : new Dictionary<string, string>(
                        metadata.Properties
                            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
                            .Select(item => new KeyValuePair<string, string>(item.Key.Trim(), _redaction.Redact(item.Value))),
                        StringComparer.Ordinal)
            };

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? items)
        => items?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToArray() ?? [];

    private IReadOnlyList<string> CleanRedactedStrings(IReadOnlyList<string>? items)
        => CleanStrings(items)
            .Select(item => _redaction.Redact(item))
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

    private static string? CleanOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? CleanRedactedOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : _redaction.Redact(value.Trim());

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed.Length <= MaxArgumentSummaryChars
            ? trimmed
            : trimmed[..MaxArgumentSummaryChars] + "...";
    }

    private static string DeriveRiskLevel(ToolApprovalRequest request)
    {
        var text = $"{request.ToolName} {request.Action}".ToLowerInvariant();
        if (text.Contains("shell", StringComparison.Ordinal) ||
            text.Contains("process", StringComparison.Ordinal) ||
            text.Contains("external", StringComparison.Ordinal) ||
            text.Contains("deploy", StringComparison.Ordinal) ||
            text.Contains("security", StringComparison.Ordinal) ||
            text.Contains("public", StringComparison.Ordinal) ||
            text.Contains("payment", StringComparison.Ordinal))
            return GovernanceRiskLevels.High;

        return request.IsMutation ? GovernanceRiskLevels.Medium : GovernanceRiskLevels.Low;
    }

    private static string StatusForDecision(string decision)
        => decision is GovernanceDecisions.Expired
            ? GovernanceDecisionStatuses.Expired
            : decision is GovernanceDecisions.Revoked
                ? GovernanceDecisionStatuses.Revoked
                : GovernanceDecisionStatuses.Active;

    private static string EventSeverity(GovernanceLedgerEntry entry)
        => entry.Decision is GovernanceDecisions.Rejected or GovernanceDecisions.Expired or GovernanceDecisions.Revoked ||
           entry.Status is GovernanceDecisionStatuses.Expired or GovernanceDecisionStatuses.Revoked
            ? "warning"
            : "info";

    private static string NormalizeDecision(string? decision)
        => string.IsNullOrWhiteSpace(decision)
            ? GovernanceDecisions.Unknown
            : decision.Trim().ToLowerInvariant() switch
            {
                GovernanceDecisions.Approved => GovernanceDecisions.Approved,
                GovernanceDecisions.Rejected => GovernanceDecisions.Rejected,
                GovernanceDecisions.Escalated => GovernanceDecisions.Escalated,
                GovernanceDecisions.Expired => GovernanceDecisions.Expired,
                GovernanceDecisions.Revoked => GovernanceDecisions.Revoked,
                GovernanceDecisions.Unknown => GovernanceDecisions.Unknown,
                _ => throw new ArgumentException($"Unsupported governance decision '{decision}'.", nameof(decision))
            };

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? GovernanceDecisionStatuses.Active
            : status.Trim().ToLowerInvariant() switch
            {
                GovernanceDecisionStatuses.Active => GovernanceDecisionStatuses.Active,
                GovernanceDecisionStatuses.Expired => GovernanceDecisionStatuses.Expired,
                GovernanceDecisionStatuses.Revoked => GovernanceDecisionStatuses.Revoked,
                GovernanceDecisionStatuses.Superseded => GovernanceDecisionStatuses.Superseded,
                _ => throw new ArgumentException($"Unsupported governance decision status '{status}'.", nameof(status))
            };

    private static string NormalizeScope(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? GovernanceScopes.Unknown
            : scope.Trim().ToLowerInvariant() switch
            {
                GovernanceScopes.Once => GovernanceScopes.Once,
                GovernanceScopes.Session => GovernanceScopes.Session,
                GovernanceScopes.Actor => GovernanceScopes.Actor,
                GovernanceScopes.Channel => GovernanceScopes.Channel,
                GovernanceScopes.Project => GovernanceScopes.Project,
                GovernanceScopes.Tool => GovernanceScopes.Tool,
                GovernanceScopes.Global => GovernanceScopes.Global,
                GovernanceScopes.Unknown => GovernanceScopes.Unknown,
                _ => throw new ArgumentException($"Unsupported governance scope '{scope}'.", nameof(scope))
            };

    private static string NormalizeRiskLevel(string? riskLevel)
        => string.IsNullOrWhiteSpace(riskLevel)
            ? GovernanceRiskLevels.Unknown
            : riskLevel.Trim().ToLowerInvariant() switch
            {
                GovernanceRiskLevels.Unknown => GovernanceRiskLevels.Unknown,
                GovernanceRiskLevels.Low => GovernanceRiskLevels.Low,
                GovernanceRiskLevels.Medium => GovernanceRiskLevels.Medium,
                GovernanceRiskLevels.High => GovernanceRiskLevels.High,
                GovernanceRiskLevels.Critical => GovernanceRiskLevels.Critical,
                _ => throw new ArgumentException($"Unsupported governance risk level '{riskLevel}'.", nameof(riskLevel))
            };

    private static string NormalizeGrantScope(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? GovernanceScopes.Unknown
            : scope.Trim().ToLowerInvariant() switch
            {
                GovernanceScopes.Once => GovernanceScopes.Once,
                GovernanceScopes.Session => GovernanceScopes.Session,
                "sender_tool_window" => GovernanceScopes.Actor,
                GovernanceScopes.Actor => GovernanceScopes.Actor,
                GovernanceScopes.Channel => GovernanceScopes.Channel,
                GovernanceScopes.Project => GovernanceScopes.Project,
                GovernanceScopes.Tool => GovernanceScopes.Tool,
                GovernanceScopes.Global => GovernanceScopes.Global,
                _ => GovernanceScopes.Unknown
            };

    private static string? ScopeKeyForGrant(ToolApprovalGrant grant, string scope)
        => scope switch
        {
            GovernanceScopes.Session => CleanOptional(grant.SessionId),
            GovernanceScopes.Actor => CleanOptional($"{grant.ChannelId}:{grant.SenderId}:{grant.ToolName}".Trim(':')),
            GovernanceScopes.Channel => CleanOptional(grant.ChannelId),
            GovernanceScopes.Tool => CleanOptional(grant.ToolName),
            GovernanceScopes.Global => "global",
            _ => CleanOptional(grant.Id)
        };

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source)
            ? GovernanceLedgerSources.Unknown
            : source.Trim().ToLowerInvariant() switch
            {
                GovernanceLedgerSources.Manual => GovernanceLedgerSources.Manual,
                GovernanceLedgerSources.ToolApproval => GovernanceLedgerSources.ToolApproval,
                GovernanceLedgerSources.ApprovalTimeout => GovernanceLedgerSources.ApprovalTimeout,
                GovernanceLedgerSources.ApprovalGrantConsumed => GovernanceLedgerSources.ApprovalGrantConsumed,
                GovernanceLedgerSources.HarnessContract => GovernanceLedgerSources.HarnessContract,
                GovernanceLedgerSources.EvidenceReview => GovernanceLedgerSources.EvidenceReview,
                GovernanceLedgerSources.LearningProposal => GovernanceLedgerSources.LearningProposal,
                GovernanceLedgerSources.Unknown => GovernanceLedgerSources.Unknown,
                _ => throw new ArgumentException($"Unsupported governance ledger source '{source}'.", nameof(source))
            };

    private static Dictionary<string, string> BuildMetadata(params (string Key, string? Value)[] items)
        => items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.Ordinal);
}
