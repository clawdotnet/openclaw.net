namespace OpenClaw.Core.Models;

public static class EvidenceConfidenceLevels
{
    public const string Unknown = "unknown";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public static class EvidenceItemKinds
{
    public const string ToolCall = "tool_call";
    public const string TestResult = "test_result";
    public const string BuildResult = "build_result";
    public const string StaticAnalysis = "static_analysis";
    public const string SecurityCheck = "security_check";
    public const string Approval = "approval";
    public const string HumanReview = "human_review";
    public const string RuntimeEvent = "runtime_event";
    public const string AuditEvent = "audit_event";
    public const string DoctorReport = "doctor_report";
    public const string PostureCheck = "posture_check";
    public const string ModelResponse = "model_response";
    public const string MemoryLookup = "memory_lookup";
    public const string VerificationResult = "verification_result";
    public const string Note = "note";
    public const string Unknown = "unknown";
}

public static class EvidenceCheckStatuses
{
    public const string NotRun = "not_run";
    public const string Running = "running";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Warning = "warning";
    public const string Unknown = "unknown";
}

public static class EvidenceRiskLevels
{
    public const string Unknown = "unknown";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";
}

public sealed class EvidenceBundle
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? SourceSessionId { get; init; }
    public string? HarnessContractId { get; init; }
    public string? LearningProposalId { get; init; }
    public string? ToolCallId { get; init; }
    public string? AutomationRunId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string Confidence { get; init; } = EvidenceConfidenceLevels.Unknown;
    public IReadOnlyList<EvidenceItem> Items { get; init; } = [];
    public IReadOnlyList<EvidenceCheck> Checks { get; init; } = [];
    public IReadOnlyList<EvidenceRisk> Risks { get; init; } = [];
    public IReadOnlyList<EvidenceAssumption> Assumptions { get; init; } = [];
    public IReadOnlyList<EvidenceUntestedArea> UntestedAreas { get; init; } = [];
    public IReadOnlyList<EvidenceHumanReview> HumanReviews { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public EvidenceBundleMetadata? Metadata { get; init; }
}

public sealed class EvidenceItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = EvidenceItemKinds.Unknown;
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public EvidenceSource? Source { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? RuntimeEventId { get; init; }
    public string? AuditEventId { get; init; }
    public string? Status { get; init; }
    public string? InputSummary { get; init; }
    public string? OutputSummary { get; init; }
    public string? ErrorSummary { get; init; }
    public string? RedactedPayload { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed class EvidenceCheck
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Kind { get; init; }
    public bool Required { get; init; } = true;
    public string Status { get; init; } = EvidenceCheckStatuses.Unknown;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string Summary { get; init; } = "";
    public string? Details { get; init; }
    public string? Command { get; init; }
    public int? ExitCode { get; init; }
    public string? Error { get; init; }
}

public sealed class EvidenceRisk
{
    public string RiskLevel { get; init; } = EvidenceRiskLevels.Unknown;
    public string Description { get; init; } = "";
    public string? Mitigation { get; init; }
    public bool Accepted { get; init; }
    public string? AcceptedBy { get; init; }
    public DateTimeOffset? AcceptedAtUtc { get; init; }
}

public sealed class EvidenceAssumption
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Verified { get; init; }
    public string? EvidenceItemId { get; init; }
}

public sealed class EvidenceUntestedArea
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Reason { get; init; }
    public string? RiskLevel { get; init; }
}

public sealed class EvidenceHumanReview
{
    public string? Reviewer { get; init; }
    public string? Decision { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset ReviewedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class EvidenceSource
{
    public string? Kind { get; init; }
    public string? Id { get; init; }
    public string? Path { get; init; }
    public string? Uri { get; init; }
    public string? Description { get; init; }
}

public sealed class EvidenceBundleMetadata
{
    public string? CreatedBy { get; init; }
    public string? Source { get; init; }
    public string? CorrelationId { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}

public sealed class EvidenceBundleListQuery
{
    public string? SourceSessionId { get; init; }
    public string? HarnessContractId { get; init; }
    public string? LearningProposalId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? Confidence { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset? CreatedFromUtc { get; init; }
    public DateTimeOffset? CreatedToUtc { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class EvidenceBundleListResponse
{
    public IReadOnlyList<EvidenceBundle> Items { get; init; } = [];
}

public sealed class EvidenceBundleDetailResponse
{
    public EvidenceBundle? Bundle { get; init; }
}

public sealed class EvidenceBundleMutationResponse
{
    public bool Success { get; init; }
    public EvidenceBundle? Bundle { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}
