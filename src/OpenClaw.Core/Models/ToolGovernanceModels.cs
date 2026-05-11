using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<GovernanceAction>))]
public enum GovernanceAction
{
    Allow,
    Deny,
    RequireApproval,
    Redact,
    AuditOnly
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolGovernanceRiskLevel>))]
public enum ToolGovernanceRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class ToolGovernanceConfig
{
    public bool Enabled { get; set; } = false;
    public string Provider { get; set; } = ToolGovernanceProviders.HttpSidecar;
    public string? SidecarBaseUrl { get; set; }
    public string DecisionEndpoint { get; set; } = "/api/v1/execute";
    public string? ResultEndpoint { get; set; }
    public int TimeoutMs { get; set; } = 300;
    public bool AuditResults { get; set; } = true;
    public bool FailClosed { get; set; } = true;
    public bool FailOpenReadOnlyLowRisk { get; set; } = false;
    public bool RequireGovernanceForHighRiskTools { get; set; } = true;
}

public static class ToolGovernanceProviders
{
    public const string None = "none";
    public const string HttpSidecar = "http_sidecar";
}

public sealed record GovernanceDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public double? TrustScore { get; init; }
    public string? PolicyId { get; init; }
    public string? RuleId { get; init; }
    public GovernanceAction Action { get; init; } = GovernanceAction.Allow;
    public double? EvaluationMs { get; init; }
    public string? RedactedArgumentsJson { get; init; }
    public string? ReplacementArgumentsJson { get; init; }

    public static GovernanceDecision Allow(string? reason = null) => new()
    {
        Allowed = true,
        Action = GovernanceAction.Allow,
        Reason = reason
    };
}

public sealed record ToolGovernanceDescriptor
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "plugin";
    public ToolGovernanceRiskLevel RiskLevel { get; init; } = ToolGovernanceRiskLevel.Medium;
    public bool RequiresApproval { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool CanAccessNetwork { get; init; }
    public bool CanAccessFileSystem { get; init; }
    public bool CanExecuteCode { get; init; }
    public bool CanSendDataExternally { get; init; }
    public string[] Capabilities { get; init; } = [];
}

public sealed record ToolGovernanceContext
{
    public required string AgentId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string CorrelationId { get; init; }
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required ToolActionDescriptor ActionDescriptor { get; init; }
    public required ToolGovernanceDescriptor Descriptor { get; init; }
    public bool IsStreaming { get; init; }
}

public sealed record ToolGovernanceExecutionResult
{
    public required string ResultStatus { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public bool Failed { get; init; }
    public bool TimedOut { get; init; }
    public double DurationMs { get; init; }
    public int ResultBytes { get; init; }
}

public sealed record ToolGovernanceSidecarRequest
{
    public required string AgentId { get; init; }
    public required string ConversationId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string UserId { get; init; }
    public required string TraceId { get; init; }
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ToolCategory { get; init; }
    public required string RiskLevel { get; init; }
    public required string ArgumentsJson { get; init; }
    public required ToolActionDescriptor ActionDescriptor { get; init; }
    public required ToolGovernanceDescriptor Descriptor { get; init; }
}

public sealed record ToolGovernanceSidecarResponse
{
    public bool? Allowed { get; init; }
    public string? Reason { get; init; }
    public double? TrustScore { get; init; }
    public string? PolicyId { get; init; }
    public string? RuleId { get; init; }
    public string? Action { get; init; }
    public double? EvaluationMs { get; init; }
    public string? RedactedArgumentsJson { get; init; }
    public string? ReplacementArgumentsJson { get; init; }
}

public sealed record ToolGovernanceSidecarResultRequest
{
    public required string AgentId { get; init; }
    public required string ConversationId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string UserId { get; init; }
    public required string TraceId { get; init; }
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required ToolGovernanceDescriptor Descriptor { get; init; }
    public required GovernanceDecision Decision { get; init; }
    public required ToolGovernanceExecutionResult Result { get; init; }
}
