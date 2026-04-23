namespace OpenClaw.Core.Models;

public static class VerificationKinds
{
    public const string FileExists = "file_exists";
    public const string FileContains = "file_contains";
    public const string HttpStatus = "http_status";
    public const string HttpBodyContains = "http_body_contains";
    public const string OperatorConfirm = "operator_confirm";
}

public sealed class VerificationPolicy
{
    public VerificationCheckDefinition[] Checks { get; init; } = [];
}

public sealed class VerificationCheckDefinition
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Path { get; init; }
    public string? Url { get; init; }
    public string? Contains { get; init; }
    public int? ExpectedStatusCode { get; init; }
    public string? Prompt { get; init; }
}

public sealed class VerificationCheckResult
{
    public string CheckId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Status { get; init; } = AutomationVerificationStatuses.NotRun;
    public string Summary { get; init; } = "";
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Optional governance policy that can be attached to a session to enforce
/// pre-flight capability validation, USD cost budgets, and scoped tool access.
/// </summary>
public sealed class ContractPolicy
{
    /// <summary>Unique contract identifier (e.g. "ctr_" + guid prefix).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label for this contract.</summary>
    public string? Name { get; init; }

    /// <summary>Required runtime mode (null = any, "aot", "jit"). Validated against effective mode.</summary>
    public string? RequiredRuntimeMode { get; init; }

    /// <summary>Tools this contract expects to use. Validated against registered tools at creation.</summary>
    public string[] RequestedTools { get; init; } = [];

    /// <summary>Path-scoped tool restrictions. Enforced at tool-call time via hook.</summary>
    public ScopedCapability[] ScopedCapabilities { get; init; } = [];

    /// <summary>Maximum USD spend for the session. 0 = unlimited.</summary>
    public decimal MaxCostUsd { get; init; }

    /// <summary>Soft USD warning threshold. 0 = no warning. Must be &lt;= MaxCostUsd when both set.</summary>
    public decimal SoftCostWarningUsd { get; init; }

    /// <summary>Maximum total tokens (input + output). 0 = unlimited.</summary>
    public long MaxTokens { get; init; }

    /// <summary>Maximum number of tool calls. 0 = unlimited.</summary>
    public int MaxToolCalls { get; init; }

    /// <summary>Maximum runtime in seconds. 0 = unlimited.</summary>
    public int MaxRuntimeSeconds { get; init; }

    /// <summary>Who created this contract (operator, API caller, etc.).</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Optional post-run verification checks for truthful completion reporting.</summary>
    public VerificationPolicy? Verification { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Path-scoped restriction for a specific tool. When present, the tool can only
/// access paths under <see cref="AllowedPaths"/>.
/// </summary>
public sealed class ScopedCapability
{
    /// <summary>Tool name this scope applies to (e.g. "file_read", "file_write").</summary>
    public required string ToolName { get; init; }

    /// <summary>Allowed filesystem roots. The tool is denied access to paths outside these.</summary>
    public string[] AllowedPaths { get; init; } = [];
}

/// <summary>
/// Result of pre-flight contract validation.
/// </summary>
public sealed class ContractValidationResult
{
    public bool IsValid { get; init; }
    public string[] GrantedTools { get; init; } = [];
    public string[] DeniedTools { get; init; } = [];
    public string[] Errors { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string? EffectiveRuntimeMode { get; init; }
}

/// <summary>
/// Point-in-time snapshot of a contract-governed session's execution metrics.
/// </summary>
public sealed class ContractExecutionSnapshot
{
    public required string ContractId { get; init; }
    public required string SessionId { get; init; }

    /// <summary>active | completed | budget_exceeded | cancelled</summary>
    public string Status { get; init; } = "active";

    public decimal AccumulatedCostUsd { get; init; }
    public long AccumulatedTokens { get; init; }
    public int ToolCallCount { get; init; }
    public double ElapsedSeconds { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string LifecycleState { get; init; } = AutomationLifecycleStates.Running;
    public string VerificationStatus { get; init; } = AutomationVerificationStatuses.NotRun;
    public string? VerificationSummary { get; init; }
    public DateTimeOffset? VerificationCompletedAtUtc { get; init; }
    public IReadOnlyList<VerificationCheckResult> VerificationChecks { get; init; } = [];
}
