namespace OpenClaw.Core.Models;

public sealed class LearningConfig
{
    public bool Enabled { get; set; } = true;
    public bool ReviewRequired { get; set; } = true;
    public int SkillProposalThreshold { get; set; } = 2;
    public int AutomationProposalThreshold { get; set; } = 3;
    public int MaxDraftChars { get; set; } = 4_000;
    public bool HarnessEvolutionEnabled { get; set; }
    public int HarnessEvolutionProposalThreshold { get; set; } = 3;
    public int HarnessEvolutionLookbackHours { get; set; } = 24;
}

public static class LearningProposalKind
{
    public const string SkillDraft = "skill_draft";
    public const string ProfileUpdate = "profile_update";
    public const string AutomationSuggestion = "automation_suggestion";
    public const string HarnessChange = "harness_change";
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
    public const string Critical = "critical";
}

public static class HarnessEvolutionComponents
{
    public const string Memory = "memory";
    public const string Retrieval = "retrieval";
    public const string Tools = "tools";
    public const string Approvals = "approvals";
    public const string Verification = "verification";
    public const string Routing = "routing";
    public const string Prompt = "prompt";
    public const string ModelProfile = "model_profile";
    public const string Pulse = "pulse";
    public const string Security = "security";
    public const string Governance = "governance";
    public const string ContextBudget = "context_budget";
    public const string Channel = "channel";
    public const string Sandbox = "sandbox";
    public const string Unknown = "unknown";
}

public static class HarnessEvolutionApplyModes
{
    public const string ManualOnly = "manual_only";
    public const string ConfigPatch = "config_patch";
    public const string PolicyPatch = "policy_patch";
    public const string SkillUpdate = "skill_update";
    public const string MemoryUpdate = "memory_update";
    public const string Unsupported = "unsupported";
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

public sealed class HarnessEvolutionProposal
{
    public string Component { get; init; } = HarnessEvolutionComponents.Unknown;
    public string? ChangeType { get; init; }
    public string FailureMode { get; init; } = "";
    public string ProposedChange { get; init; } = "";
    public string? PredictedImprovement { get; init; }
    public IReadOnlyList<string> InvariantsToPreserve { get; init; } = [];
    public IReadOnlyList<string> FalsificationTests { get; init; } = [];
    public string? EvaluationPlan { get; init; }
    public string? CanaryPlan { get; init; }
    public string? RollbackPlan { get; init; }
    public IReadOnlyList<string> RelatedHarnessContractIds { get; init; } = [];
    public IReadOnlyList<string> RelatedEvidenceBundleIds { get; init; } = [];
    public IReadOnlyList<string> RelatedGovernanceLedgerIds { get; init; } = [];
    public IReadOnlyList<string> RelatedRegressionReportIds { get; init; } = [];
    public IReadOnlyList<string> SourceRuntimeEventIds { get; init; } = [];
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public string RiskLevel { get; init; } = LearningProposalRiskLevels.Medium;
    public float Confidence { get; init; }
    public string? ProposalFingerprint { get; init; }
    public string ApplyMode { get; init; } = HarnessEvolutionApplyModes.ManualOnly;
    public bool IsAutoApplicable { get; init; }
    public bool RequiresRegression { get; init; }
    public IReadOnlyList<string> RegressionCategories { get; init; } = [];
}

public sealed class HarnessEvolutionProposalCreateRequest
{
    public string? ActorId { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Component { get; init; }
    public string? ChangeType { get; init; }
    public string? FailureMode { get; init; }
    public string? ProposedChange { get; init; }
    public string? PredictedImprovement { get; init; }
    public IReadOnlyList<string> InvariantsToPreserve { get; init; } = [];
    public IReadOnlyList<string> FalsificationTests { get; init; } = [];
    public string? EvaluationPlan { get; init; }
    public string? CanaryPlan { get; init; }
    public string? RollbackPlan { get; init; }
    public IReadOnlyList<string> RelatedHarnessContractIds { get; init; } = [];
    public IReadOnlyList<string> RelatedEvidenceBundleIds { get; init; } = [];
    public IReadOnlyList<string> RelatedGovernanceLedgerIds { get; init; } = [];
    public IReadOnlyList<string> RelatedRegressionReportIds { get; init; } = [];
    public IReadOnlyList<string> SourceRuntimeEventIds { get; init; } = [];
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public string? RiskLevel { get; init; }
    public float? Confidence { get; init; }
    public string? ApplyMode { get; init; }
    public bool IsAutoApplicable { get; init; }
    public bool? RequiresRegression { get; init; }
    public IReadOnlyList<string> RegressionCategories { get; init; } = [];
}

public sealed class HarnessEvolutionDetectionRequest
{
    public int? LookbackHours { get; init; }
    public int? Threshold { get; init; }
    public int? Limit { get; init; }
}

public sealed class HarnessEvolutionDetectionResponse
{
    public IReadOnlyList<LearningProposal> Proposals { get; init; } = [];
    public int GroupsEvaluated { get; init; }
    public int GroupsMeetingThreshold { get; init; }
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
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
    public HarnessEvolutionProposal? HarnessEvolution { get; init; }
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
