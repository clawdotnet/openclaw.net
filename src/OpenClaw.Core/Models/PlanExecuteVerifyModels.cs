namespace OpenClaw.Core.Models;

public static class HarnessExecutionModes
{
    public const string Normal = "normal";
    public const string Supervised = "supervised";
    public const string PlanExecuteVerify = "plan-execute-verify";
}

public static class PlanExecuteVerifyContractTriggers
{
    public const string HighRiskTools = "high_risk_tools";
    public const string WriteTools = "write_tools";
    public const string Shell = "shell";
    public const string Browser = "browser";
    public const string ExternalApi = "external_api";
    public const string MultiToolWorkflows = "multi_tool_workflows";
}

public static class PlanExecuteVerifyStatus
{
    public const string NotStarted = "not_started";
    public const string ContractCreated = "contract_created";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Executing = "executing";
    public const string Verifying = "verifying";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Escalated = "escalated";
    public const string RolledBack = "rolled_back";
    public const string Cancelled = "cancelled";
}

public static class PlanExecuteVerifyDecisionKinds
{
    public const string Proceed = "proceed";
    public const string RequireApproval = "require_approval";
    public const string Reject = "reject";
    public const string Escalate = "escalate";
    public const string RevisePlan = "revise_plan";
    public const string Rollback = "rollback";
}

public static class HarnessVerificationStatus
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Warning = "warning";
    public const string Skipped = "skipped";
    public const string Unknown = "unknown";
}

public sealed class PlanExecuteVerifyRun
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = PlanExecuteVerifyStatus.NotStarted;
    public string Decision { get; init; } = PlanExecuteVerifyDecisionKinds.Proceed;
    public string? HarnessContractId { get; init; }
    public string? EvidenceBundleId { get; init; }
    public string? SourceSessionId { get; init; }
    public string? ActorId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string Goal { get; init; } = "";
    public string? ToolName { get; init; }
    public string RiskLevel { get; init; } = HarnessContractRiskLevels.Low;
    public bool ApprovalRequired { get; init; }
    public bool Approved { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public HarnessVerificationResult? Verification { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Recommendations { get; init; } = [];
}

public sealed class PlanExecuteVerifyDecision
{
    public string Decision { get; init; } = PlanExecuteVerifyDecisionKinds.Proceed;
    public bool RequiresPlanExecuteVerify { get; init; }
    public bool RequiresApproval { get; init; }
    public string RiskLevel { get; init; } = HarnessContractRiskLevels.Low;
    public string Summary { get; init; } = "";
    public PlanExecuteVerifyRun? Run { get; init; }
}

public sealed class HarnessVerificationResult
{
    public string Status { get; init; } = HarnessVerificationStatus.Unknown;
    public string Summary { get; init; } = "";
    public IReadOnlyList<HarnessVerificationCheck> Checks { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];
    public IReadOnlyList<string> UntestedAreas { get; init; } = [];
    public IReadOnlyList<string> Recommendations { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

public sealed class HarnessVerificationCheck
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = HarnessVerificationStatus.Unknown;
    public bool Required { get; init; } = true;
    public string Summary { get; init; } = "";
    public string? Details { get; init; }
}

public sealed class PlanExecuteVerifyRunListResponse
{
    public IReadOnlyList<PlanExecuteVerifyRun> Items { get; init; } = [];
}

public sealed class PlanExecuteVerifyRunDetailResponse
{
    public PlanExecuteVerifyRun? Run { get; init; }
}

public sealed class PlanExecuteVerifyRunMutationResponse
{
    public bool Success { get; init; }
    public PlanExecuteVerifyRun? Run { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}
