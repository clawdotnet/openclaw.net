namespace OpenClaw.Core.Models;

public sealed class LearningConfig
{
    public bool Enabled { get; set; } = true;
    public bool ReviewRequired { get; set; } = true;
    public int SkillProposalThreshold { get; set; } = 2;
    public int AutomationProposalThreshold { get; set; } = 3;
    public int MaxDraftChars { get; set; } = 4_000;
}

public static class LearningProposalKind
{
    public const string SkillDraft = "skill_draft";
    public const string ProfileUpdate = "profile_update";
    public const string AutomationSuggestion = "automation_suggestion";
}

public static class LearningProposalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string RolledBack = "rolled_back";
}

public static class LearningProposalRiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public static class LearningProposalValidationStatuses
{
    public const string NotRun = "not_run";
    public const string Valid = "valid";
    public const string Warning = "warning";
    public const string Error = "error";
}

public sealed class LearningToolObservation
{
    public required string ToolName { get; init; }
    public int SequenceIndex { get; init; }
    public bool? IsReadOnly { get; init; }
    public bool? IsMutating { get; init; }
    public bool? IsInteractive { get; init; }
    public bool? IsApprovalGated { get; init; }
    public bool? IsSandboxCapable { get; init; }
    public string? ClassificationReason { get; init; }
}

public sealed class ManagedLearningSkillMetadata
{
    public bool ManagedByLearning { get; init; } = true;
    public required string CreatedByProposalId { get; init; }
    public string? OriginalDraftHash { get; init; }
    public DateTimeOffset ApprovedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? SkillName { get; init; }
}

public sealed class LearningProposal
{
    public required string Id { get; init; }
    public string Kind { get; init; } = LearningProposalKind.SkillDraft;
    public string Status { get; init; } = LearningProposalStatus.Pending;
    public string? ActorId { get; init; }
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? SkillName { get; init; }
    public string? DraftContent { get; init; }
    public string? DraftContentHash { get; init; }
    public string? DraftPreview { get; init; }
    public UserProfile? ProfileUpdate { get; init; }
    public UserProfile? AppliedProfileBefore { get; init; }
    public AutomationDefinition? AutomationDraft { get; init; }
    public string? AppliedAutomationId { get; init; }
    public string? ManagedSkillPath { get; init; }
    public ManagedLearningSkillMetadata? ManagedSkillMetadata { get; init; }
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public IReadOnlyList<string> SourceTurnIds { get; init; } = [];
    public IReadOnlyList<string> ToolNames { get; init; } = [];
    public IReadOnlyList<string> ToolSequence { get; init; } = [];
    public IReadOnlyList<LearningToolObservation> ToolObservations { get; init; } = [];
    public int RepeatedCount { get; init; }
    public string? ProposalFingerprint { get; init; }
    public string RiskLevel { get; init; } = LearningProposalRiskLevels.Medium;
    public float Confidence { get; init; }
    public string? CreatedReason { get; init; }
    public string ValidationStatus { get; init; } = LearningProposalValidationStatuses.NotRun;
    public IReadOnlyList<string> ValidationWarnings { get; init; } = [];
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; init; }
    public string? ReviewNotes { get; init; }
    public bool RolledBack { get; init; }
    public DateTimeOffset? RolledBackAtUtc { get; init; }
    public string? RollbackReason { get; init; }
}
