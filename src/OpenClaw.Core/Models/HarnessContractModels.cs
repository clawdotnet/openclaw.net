namespace OpenClaw.Core.Models;

public static class HarnessContractStatus
{
    public const string Draft = "draft";
    public const string Proposed = "proposed";
    public const string Approved = "approved";
    public const string Executing = "executing";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string RolledBack = "rolled_back";
    public const string Cancelled = "cancelled";
}

public static class HarnessContractRiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";
}

public static class HarnessContractApprovalRequirements
{
    public const string None = "none";
    public const string Optional = "optional";
    public const string Required = "required";
    public const string AlreadySatisfied = "already_satisfied";
}

public static class HarnessContractResourceKinds
{
    public const string File = "file";
    public const string Directory = "directory";
    public const string MemoryNote = "memory_note";
    public const string Profile = "profile";
    public const string Automation = "automation";
    public const string Skill = "skill";
    public const string ProviderPolicy = "provider_policy";
    public const string Session = "session";
    public const string Endpoint = "endpoint";
    public const string ExternalApi = "external_api";
    public const string Database = "database";
    public const string Unknown = "unknown";
}

public sealed class HarnessContract
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = HarnessContractStatus.Draft;
    public string Goal { get; init; } = "";
    public string? UserRequestSummary { get; init; }
    public string? SourceSessionId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? RiskLevel { get; init; }
    public string ApprovalRequired { get; init; } = HarnessContractApprovalRequirements.None;
    public string? ApprovalReason { get; init; }
    public IReadOnlyList<HarnessContractAction> PlannedActions { get; init; } = [];
    public IReadOnlyList<HarnessContractResourceRef> ReadSet { get; init; } = [];
    public IReadOnlyList<HarnessContractResourceRef> WriteSet { get; init; } = [];
    public IReadOnlyList<HarnessContractToolRequirement> ToolsRequired { get; init; } = [];
    public IReadOnlyList<HarnessContractAssumption> Assumptions { get; init; } = [];
    public IReadOnlyList<HarnessContractConstraint> Constraints { get; init; } = [];
    public IReadOnlyList<HarnessContractVerificationStep> VerificationPlan { get; init; } = [];
    public IReadOnlyList<HarnessContractRollbackStep> RollbackPlan { get; init; } = [];
    public IReadOnlyList<string> SuccessCriteria { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public HarnessContractMetadata? Metadata { get; init; }
}

public sealed class HarnessContractAction
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? ToolName { get; init; }
    public string? ActionType { get; init; }
    public string? RiskLevel { get; init; }
    public bool RequiresApproval { get; init; }
    public IReadOnlyList<HarnessContractResourceRef> ReadSet { get; init; } = [];
    public IReadOnlyList<HarnessContractResourceRef> WriteSet { get; init; } = [];
    public string? ExpectedOutcome { get; init; }
    public string? Status { get; init; }
}

public sealed class HarnessContractToolRequirement
{
    public required string ToolName { get; init; }
    public string? Purpose { get; init; }
    public bool RequiresApproval { get; init; }
    public string? ApprovalScope { get; init; }
}

public sealed class HarnessContractResourceRef
{
    public string Kind { get; init; } = HarnessContractResourceKinds.Unknown;
    public string? Path { get; init; }
    public string? Key { get; init; }
    public string? Id { get; init; }
    public string? Description { get; init; }
    public string? Scope { get; init; }
    public bool IsSensitive { get; init; }
}

public sealed class HarnessContractVerificationStep
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string? Kind { get; init; }
    public string? Command { get; init; }
    public string? ToolName { get; init; }
    public string? CheckName { get; init; }
    public string? ExpectedSignal { get; init; }
    public bool Required { get; init; } = true;
    public string? Status { get; init; }
    public string? ResultSummary { get; init; }
}

public sealed class HarnessContractRollbackStep
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? ToolName { get; init; }
    public string? Target { get; init; }
    public string? Status { get; init; }
}

public sealed class HarnessContractAssumption
{
    public required string Id { get; init; }
    public string Text { get; init; } = "";
    public bool Verified { get; init; }
}

public sealed class HarnessContractConstraint
{
    public required string Id { get; init; }
    public string Text { get; init; } = "";
    public string? Scope { get; init; }
}

public sealed class HarnessContractMetadata
{
    public string? CreatedBy { get; init; }
    public string? Source { get; init; }
    public string? CorrelationId { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}

public sealed class HarnessContractListQuery
{
    public string? Status { get; init; }
    public string? RiskLevel { get; init; }
    public string? SourceSessionId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedToUtc { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class HarnessContractStatusUpdateRequest
{
    public required string Status { get; init; }
}

public sealed class HarnessContractListResponse
{
    public IReadOnlyList<HarnessContract> Items { get; init; } = [];
}

public sealed class HarnessContractDetailResponse
{
    public HarnessContract? Contract { get; init; }
}

public sealed class HarnessContractMutationResponse
{
    public bool Success { get; init; }
    public HarnessContract? Contract { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}
