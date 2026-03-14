using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Core.Models;

public sealed class MutationResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
    public bool RestartRequired { get; init; }
}

public sealed class InputTokenComponentEstimate
{
    public long SystemPrompt { get; init; }
    public long Skills { get; init; }
    public long History { get; init; }
    public long ToolOutputs { get; init; }
    public long UserInput { get; init; }
}

public sealed class ProviderPolicyRule
{
    public required string Id { get; init; }
    public int Priority { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public string[] FallbackModels { get; init; } = [];
    public int MaxInputTokens { get; init; }
    public int MaxOutputTokens { get; init; }
    public int MaxTotalTokens { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ProviderPolicyListResponse
{
    public IReadOnlyList<ProviderPolicyRule> Items { get; init; } = [];
}

public sealed class ProviderRouteHealthSnapshot
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public bool IsDefaultRoute { get; init; }
    public bool IsDynamic { get; init; }
    public string? OwnerId { get; init; }
    public string CircuitState { get; init; } = "Closed";
    public long Requests { get; init; }
    public long Retries { get; init; }
    public long Errors { get; init; }
    public DateTimeOffset? LastErrorAtUtc { get; init; }
    public string? LastError { get; init; }
}

public sealed class ProviderTurnUsageEntry
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
}

public sealed class ProviderAdminResponse
{
    public IReadOnlyList<ProviderRouteHealthSnapshot> Routes { get; init; } = [];
    public IReadOnlyList<ProviderUsageSnapshot> Usage { get; init; } = [];
    public IReadOnlyList<ProviderPolicyRule> Policies { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns { get; init; } = [];
}

public sealed class RuntimeEventQuery
{
    public int Limit { get; init; } = 100;
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? Component { get; init; }
    public string? Action { get; init; }
}

public sealed class RuntimeEventEntry
{
    public required string Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? CorrelationId { get; init; }
    public required string Component { get; init; }
    public required string Action { get; init; }
    public required string Severity { get; init; }
    public string Summary { get; init; } = "";
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class RuntimeEventListResponse
{
    public IReadOnlyList<RuntimeEventEntry> Items { get; init; } = [];
}

public sealed class PluginOperatorState
{
    public required string PluginId { get; init; }
    public bool Disabled { get; init; }
    public bool Quarantined { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PluginHealthSnapshot
{
    public required string PluginId { get; init; }
    public required string Origin { get; init; }
    public bool Loaded { get; init; }
    public bool BlockedByRuntimeMode { get; init; }
    public bool Disabled { get; init; }
    public bool Quarantined { get; init; }
    public string? PendingReason { get; init; }
    public string? EffectiveRuntimeMode { get; init; }
    public string[] RequestedCapabilities { get; init; } = [];
    public string? LastError { get; init; }
    public DateTimeOffset? LastActivityAtUtc { get; init; }
    public int RestartCount { get; init; }
    public int ToolCount { get; init; }
    public int ChannelCount { get; init; }
    public int CommandCount { get; init; }
    public int ProviderCount { get; init; }
    public IReadOnlyList<PluginCompatibilityDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class PluginListResponse
{
    public IReadOnlyList<PluginHealthSnapshot> Items { get; init; } = [];
}

public sealed class PluginMutationRequest
{
    public string? Reason { get; init; }
}

public sealed class ToolApprovalGrant
{
    public required string Id { get; init; }
    public required string Scope { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public required string ToolName { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public required string GrantedBy { get; init; }
    public required string GrantSource { get; init; }
    public int RemainingUses { get; init; } = 1;
}

public sealed class ApprovalGrantListResponse
{
    public IReadOnlyList<ToolApprovalGrant> Items { get; init; } = [];
}

public sealed class OperatorAuditQuery
{
    public int Limit { get; init; } = 100;
    public string? ActorId { get; init; }
    public string? ActionType { get; init; }
    public string? TargetId { get; init; }
}

public sealed class OperatorAuditEntry
{
    public required string Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ActorId { get; init; }
    public required string AuthMode { get; init; }
    public required string ActionType { get; init; }
    public required string TargetId { get; init; }
    public required string Summary { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public bool Success { get; init; }
}

public sealed class OperatorAuditListResponse
{
    public IReadOnlyList<OperatorAuditEntry> Items { get; init; } = [];
}

public sealed class SessionMetadataSnapshot
{
    public required string SessionId { get; init; }
    public bool Starred { get; init; }
    public string[] Tags { get; init; } = [];
}

public sealed class SessionMetadataUpdateRequest
{
    public bool? Starred { get; init; }
    public string[]? Tags { get; init; }
}

public sealed class SessionDiffResponse
{
    public required string SessionId { get; init; }
    public required string BranchId { get; init; }
    public string? BranchName { get; init; }
    public int SharedPrefixTurns { get; init; }
    public int CurrentTurnCount { get; init; }
    public int BranchTurnCount { get; init; }
    public IReadOnlyList<string> CurrentOnlyTurnSummaries { get; init; } = [];
    public IReadOnlyList<string> BranchOnlyTurnSummaries { get; init; } = [];
    public SessionMetadataSnapshot? Metadata { get; init; }
}

public sealed class SessionTimelineResponse
{
    public required string SessionId { get; init; }
    public IReadOnlyList<RuntimeEventEntry> Events { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> ProviderTurns { get; init; } = [];
}

public sealed class SessionExportItem
{
    public required Session Session { get; init; }
    public SessionMetadataSnapshot? Metadata { get; init; }
}

public sealed class SessionExportResponse
{
    public required SessionListQuery Filters { get; init; }
    public IReadOnlyList<SessionExportItem> Items { get; init; } = [];
}

public sealed class WebhookDeadLetterEntry
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string DeliveryKey { get; init; }
    public string? EndpointName { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Error { get; init; } = "";
    public string PayloadPreview { get; init; } = "";
    public bool Discarded { get; init; }
    public DateTimeOffset? ReplayedAtUtc { get; init; }
}

public sealed class WebhookDeadLetterRecord
{
    public required WebhookDeadLetterEntry Entry { get; init; }
    public InboundMessage? ReplayMessage { get; init; }
}

public sealed class WebhookDeadLetterResponse
{
    public IReadOnlyList<WebhookDeadLetterEntry> Items { get; init; } = [];
}

public sealed class ActorRateLimitPolicy
{
    public required string Id { get; init; }
    public required string ActorType { get; init; }
    public required string EndpointScope { get; init; }
    public string? MatchValue { get; init; }
    public int BurstLimit { get; init; }
    public int BurstWindowSeconds { get; init; }
    public int SustainedLimit { get; init; }
    public int SustainedWindowSeconds { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ActorRateLimitStatus
{
    public required string ActorType { get; init; }
    public required string EndpointScope { get; init; }
    public required string ActorKey { get; init; }
    public int BurstCount { get; init; }
    public int SustainedCount { get; init; }
    public DateTimeOffset BurstWindowStartedAtUtc { get; init; }
    public DateTimeOffset SustainedWindowStartedAtUtc { get; init; }
}

public sealed class ActorRateLimitResponse
{
    public IReadOnlyList<ActorRateLimitPolicy> Policies { get; init; } = [];
    public IReadOnlyList<ActorRateLimitStatus> Active { get; init; } = [];
}
