namespace OpenClaw.Core.Models;

public static class HarnessParticipantRoles
{
    public const string Manager = "manager";
    public const string Planner = "planner";
    public const string Coder = "coder";
    public const string Reviewer = "reviewer";
    public const string Tester = "tester";
    public const string SecurityReviewer = "security_reviewer";
    public const string OpsVerifier = "ops_verifier";
    public const string DocsWriter = "docs_writer";
    public const string Researcher = "researcher";
    public const string Operator = "operator";
    public const string Custom = "custom";
}

public static class HarnessStateStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Blocked = "blocked";
    public const string Unknown = "unknown";
}

public static class HarnessConflictPolicies
{
    public const string Allow = "allow";
    public const string Warn = "warn";
    public const string Serialize = "serialize";
    public const string Escalate = "escalate";
    public const string Reject = "reject";
}

public static class HarnessConflictTypes
{
    public const string WriteWrite = "write_write";
    public const string ReadWrite = "read_write";
    public const string Assumption = "assumption";
    public const string VerifierObligation = "verifier_obligation";
}

public sealed class SharedHarnessState
{
    public string Id { get; init; } = "";
    public string? SessionId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? HarnessContractId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; init; } = HarnessStateStatuses.Active;
    public string Goal { get; init; } = "";
    public IReadOnlyList<HarnessParticipant> Participants { get; init; } = [];
    public IReadOnlyList<HarnessStateAction> Actions { get; init; } = [];
    public IReadOnlyList<HarnessResourceRef> SharedReadSet { get; init; } = [];
    public IReadOnlyList<HarnessResourceRef> SharedWriteSet { get; init; } = [];
    public IReadOnlyList<HarnessAssumption> Assumptions { get; init; } = [];
    public IReadOnlyList<HarnessVersionDependency> VersionDependencies { get; init; } = [];
    public IReadOnlyList<HarnessVerifierObligation> VerifierObligations { get; init; } = [];
    public IReadOnlyList<HarnessConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> EvidenceBundleIds { get; init; } = [];
    public IReadOnlyList<string> GovernanceLedgerIds { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed class HarnessParticipant
{
    public string Id { get; init; } = "";
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public string Role { get; init; } = HarnessParticipantRoles.Custom;
    public string? DisplayName { get; init; }
    public string? ModelProfileId { get; init; }
    public string? ToolPreset { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string Status { get; init; } = HarnessStateStatuses.Active;
    public string? ParentParticipantId { get; init; }
    public string? Notes { get; init; }
}

public sealed class HarnessStateAction
{
    public string Id { get; init; } = "";
    public string? ParticipantId { get; init; }
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public string Status { get; init; } = HarnessStateStatuses.Active;
    public string? ToolName { get; init; }
    public IReadOnlyList<HarnessResourceRef> ReadSet { get; init; } = [];
    public IReadOnlyList<HarnessResourceRef> WriteSet { get; init; } = [];
    public IReadOnlyList<HarnessAssumption> Assumptions { get; init; } = [];
    public IReadOnlyList<HarnessVersionDependency> VersionDependencies { get; init; } = [];
    public IReadOnlyList<HarnessVerifierObligation> VerifierObligations { get; init; } = [];
    public string? EvidenceBundleId { get; init; }
    public string? HarnessContractId { get; init; }
    public string? RiskLevel { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

public sealed class HarnessReadWriteSet
{
    public IReadOnlyList<HarnessResourceRef> ReadSet { get; init; } = [];
    public IReadOnlyList<HarnessResourceRef> WriteSet { get; init; } = [];
}

public sealed class HarnessResourceRef
{
    public string Kind { get; init; } = HarnessContractResourceKinds.Unknown;
    public string? Path { get; init; }
    public string? Key { get; init; }
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Description { get; init; }
    public string? Scope { get; init; }
    public string? Version { get; init; }
    public bool IsSensitive { get; init; }
}

public sealed class HarnessAssumption
{
    public string Id { get; init; } = "";
    public string? Key { get; init; }
    public string? Value { get; init; }
    public string Text { get; init; } = "";
    public bool Verified { get; init; }
    public string? EvidenceBundleId { get; init; }
}

public sealed class HarnessVersionDependency
{
    public string Id { get; init; } = "";
    public HarnessResourceRef? Resource { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
}

public sealed class HarnessVerifierObligation
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Verifier { get; init; }
    public bool Required { get; init; } = true;
    public HarnessResourceRef? Resource { get; init; }
    public string Status { get; init; } = HarnessStateStatuses.Unknown;
    public string? Summary { get; init; }
    public string? EvidenceBundleId { get; init; }
}

public sealed class HarnessConflict
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Participants { get; init; } = [];
    public IReadOnlyList<string> Actions { get; init; } = [];
    public IReadOnlyList<HarnessResourceRef> Resources { get; init; } = [];
    public string Policy { get; init; } = HarnessConflictPolicies.Warn;
    public string Severity { get; init; } = HarnessContractRiskLevels.Medium;
    public string Status { get; init; } = HarnessStateStatuses.Active;
    public string? Recommendation { get; init; }
}

public sealed class SharedHarnessStateListQuery
{
    public string? SessionId { get; init; }
    public string? ParentSessionId { get; init; }
    public string? HarnessContractId { get; init; }
    public string? Status { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedToUtc { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class SharedHarnessStateListResponse
{
    public IReadOnlyList<SharedHarnessState> Items { get; init; } = [];
}

public sealed class SharedHarnessStateDetailResponse
{
    public SharedHarnessState? State { get; init; }
}

public sealed class SharedHarnessStateMutationResponse
{
    public bool Success { get; init; }
    public SharedHarnessState? State { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}
