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
    public UserProfile? ProfileUpdate { get; init; }
    public AutomationDefinition? AutomationDraft { get; init; }
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public float Confidence { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; init; }
    public string? ReviewNotes { get; init; }
}
