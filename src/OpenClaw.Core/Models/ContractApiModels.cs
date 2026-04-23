namespace OpenClaw.Core.Models;

/// <summary>
/// Request to create a contract and attach it to a session.
/// </summary>
public sealed class ContractCreateRequest
{
    /// <summary>Session to attach the contract to. If null, a new session context is implied.</summary>
    public string? SessionId { get; init; }

    public string? Name { get; init; }
    public string? RequiredRuntimeMode { get; init; }
    public string[]? RequestedTools { get; init; }
    public ScopedCapability[]? ScopedCapabilities { get; init; }
    public decimal MaxCostUsd { get; init; }
    public decimal SoftCostWarningUsd { get; init; }
    public long MaxTokens { get; init; }
    public int MaxToolCalls { get; init; }
    public int MaxRuntimeSeconds { get; init; }
    public string? CreatedBy { get; init; }
    public VerificationPolicy? Verification { get; init; }
}

/// <summary>
/// Response after creating a contract.
/// </summary>
public sealed class ContractCreateResponse
{
    public required ContractPolicy Policy { get; init; }
    public required ContractValidationResult Validation { get; init; }
}

/// <summary>
/// Request for pre-flight validation without creating a contract.
/// </summary>
public sealed class ContractValidateRequest
{
    public string? RequiredRuntimeMode { get; init; }
    public string[]? RequestedTools { get; init; }
    public ScopedCapability[]? ScopedCapabilities { get; init; }
    public decimal MaxCostUsd { get; init; }
    public decimal SoftCostWarningUsd { get; init; }
    public long MaxTokens { get; init; }
    public int MaxToolCalls { get; init; }
    public int MaxRuntimeSeconds { get; init; }
    public VerificationPolicy? Verification { get; init; }
}

/// <summary>
/// Status of a single contract including its execution snapshot.
/// </summary>
public sealed class ContractStatusResponse
{
    public required ContractPolicy Policy { get; init; }
    public ContractExecutionSnapshot? Snapshot { get; init; }
}

/// <summary>
/// List of contracts.
/// </summary>
public sealed class ContractListResponse
{
    public IReadOnlyList<ContractStatusResponse> Items { get; init; } = [];
}
