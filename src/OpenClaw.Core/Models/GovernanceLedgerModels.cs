namespace OpenClaw.Core.Models;

public static class GovernanceDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Escalated = "escalated";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string Unknown = "unknown";
}

public static class GovernanceDecisionStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string Superseded = "superseded";
}

public static class GovernanceScopes
{
    public const string Once = "once";
    public const string Session = "session";
    public const string Actor = "actor";
    public const string Channel = "channel";
    public const string Project = "project";
    public const string Tool = "tool";
    public const string Global = "global";
    public const string Unknown = "unknown";
}

public static class GovernanceRiskLevels
{
    public const string Unknown = "unknown";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";
}

public static class GovernanceLedgerSources
{
    public const string Manual = "manual";
    public const string ToolApproval = "tool_approval";
    public const string ApprovalTimeout = "approval_timeout";
    public const string ApprovalGrantConsumed = "approval_grant_consumed";
    public const string HarnessContract = "harness_contract";
    public const string EvidenceReview = "evidence_review";
    public const string LearningProposal = "learning_proposal";
    public const string Unknown = "unknown";
}

public sealed class GovernanceLedgerEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Decision { get; init; } = GovernanceDecisions.Unknown;
    public string Status { get; init; } = GovernanceDecisionStatuses.Active;
    public string Source { get; init; } = GovernanceLedgerSources.Manual;
    public string? ActionType { get; init; }
    public string? ToolName { get; init; }
    public string ActionSummary { get; init; } = "";
    public string? ArgumentSummary { get; init; }
    public string? RedactedArguments { get; init; }
    public string RiskLevel { get; init; } = GovernanceRiskLevels.Unknown;
    public string Scope { get; init; } = GovernanceScopes.Unknown;
    public string? ScopeKey { get; init; }
    public string? SessionId { get; init; }
    public string? HarnessContractId { get; init; }
    public string? EvidenceBundleId { get; init; }
    public string? LearningProposalId { get; init; }
    public string? ApprovalId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionReason { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
    public string? RevokedBy { get; init; }
    public string? RevocationReason { get; init; }
    public GovernancePolicyHint? PolicyHint { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public GovernanceLedgerMetadata? Metadata { get; init; }
}

public sealed class GovernancePolicyHint
{
    public string? SuggestedFutureBehavior { get; init; }
    public string? SuggestedScope { get; init; }
    public string? Confidence { get; init; }
    public bool RequiresReview { get; init; } = true;
    public string? Notes { get; init; }
}

public sealed class GovernanceLedgerMetadata
{
    public string? CreatedBy { get; init; }
    public string? CorrelationId { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}

public sealed class GovernanceLedgerListQuery
{
    public string? Decision { get; init; }
    public string? Status { get; init; }
    public string? ToolName { get; init; }
    public string? ActionType { get; init; }
    public string? RiskLevel { get; init; }
    public string? Scope { get; init; }
    public string? SessionId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? DecidedBy { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedToUtc { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class GovernanceLedgerRevokeRequest
{
    public string? RevokedBy { get; init; }
    public string? Reason { get; init; }
}

public sealed class GovernanceLedgerListResponse
{
    public IReadOnlyList<GovernanceLedgerEntry> Items { get; init; } = [];
}

public sealed class GovernanceLedgerDetailResponse
{
    public GovernanceLedgerEntry? Entry { get; init; }
}

public sealed class GovernanceLedgerMutationResponse
{
    public bool Success { get; init; }
    public GovernanceLedgerEntry? Entry { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}
