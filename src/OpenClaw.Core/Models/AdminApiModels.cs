namespace OpenClaw.Core.Models;

public sealed class AuthSessionRequest
{
    public bool Remember { get; init; }
}

public sealed class AuthSessionResponse
{
    public required string AuthMode { get; init; }
    public string? CsrfToken { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public bool Persistent { get; init; }
}

public sealed class ApprovalListResponse
{
    public IReadOnlyList<OpenClaw.Core.Pipeline.ToolApprovalRequest> Items { get; init; } = [];
}

public sealed class ApprovalHistoryQuery
{
    public int Limit { get; init; } = 50;
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? ToolName { get; init; }
}

public sealed class ApprovalHistoryEntry
{
    public required string EventType { get; init; }
    public required string ApprovalId { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsPreview { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public DateTimeOffset? DecisionAtUtc { get; init; }
    public string? ActorChannelId { get; init; }
    public string? ActorSenderId { get; init; }
    public string? DecisionSource { get; init; }
    public bool? Approved { get; init; }
}

public sealed class ApprovalHistoryResponse
{
    public IReadOnlyList<ApprovalHistoryEntry> Items { get; init; } = [];
}

public sealed class PairingApproveResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

public sealed class PairingRevokeResponse
{
    public bool Success { get; init; }
}

public sealed class AllowlistSnapshotResponse
{
    public required string ChannelId { get; init; }
    public required string Semantics { get; init; }
    public required OpenClaw.Core.Security.ChannelAllowlistFile Config { get; init; }
    public OpenClaw.Core.Security.ChannelAllowlistFile? Dynamic { get; init; }
    public required OpenClaw.Core.Security.ChannelAllowlistFile Effective { get; init; }
}

public sealed class SenderMutationResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? SenderId { get; init; }
}

public sealed class CountMutationResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int Count { get; init; }
}

public sealed class SkillsReloadResponse
{
    public int Reloaded { get; init; }
    public IReadOnlyList<string> Skills { get; init; } = [];
}

public sealed class ChannelFixGuidanceDto
{
    public required string Label { get; init; }
    public required string Href { get; init; }
    public required string Reference { get; init; }
}

public sealed class ChannelReadinessDto
{
    public required string ChannelId { get; init; }
    public required string DisplayName { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ChannelFixGuidanceDto> FixGuidance { get; init; } = [];
}

public sealed class AdminSettingsResponse
{
    public required AdminSettingsSnapshot Settings { get; init; }
    public required AdminSettingsPersistenceInfo Persistence { get; init; }
    public string Message { get; init; } = "";
    public bool RestartRequired { get; init; }
    public IReadOnlyList<string> RestartRequiredFields { get; init; } = [];
    public IReadOnlyList<string> ImmediateFieldKeys { get; init; } = [];
    public IReadOnlyList<string> RestartFieldKeys { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ChannelReadinessDto> ChannelReadiness { get; init; } = [];
}

public sealed class SessionBranchListResponse
{
    public IReadOnlyList<SessionBranch> Items { get; init; } = [];
}

public sealed class AdminSessionDetailResponse
{
    public Session? Session { get; init; }
    public bool IsActive { get; init; }
    public int BranchCount { get; init; }
    public SessionMetadataSnapshot? Metadata { get; init; }
}

public sealed class AdminSessionsResponse
{
    public required SessionListQuery Filters { get; init; }
    public IReadOnlyList<SessionSummary> Active { get; init; } = [];
    public required PagedSessionList Persisted { get; init; }
}

public sealed class AdminSummaryResponse
{
    public required AdminSummaryAuth Auth { get; init; }
    public required AdminSummaryRuntime Runtime { get; init; }
    public required AdminSummarySettings Settings { get; init; }
    public required AdminSummaryChannels Channels { get; init; }
    public required AdminSummaryRetention Retention { get; init; }
    public required AdminSummaryPlugins Plugins { get; init; }
    public required AdminSummaryUsage Usage { get; init; }
}

public sealed class AdminSummaryAuth
{
    public required string Mode { get; init; }
    public bool BrowserSessionActive { get; init; }
}

public sealed class AdminSummaryRuntime
{
    public required string RequestedMode { get; init; }
    public required string EffectiveMode { get; init; }
    public required string Orchestrator { get; init; }
    public bool DynamicCodeSupported { get; init; }
    public int ActiveSessions { get; init; }
    public int PendingApprovals { get; init; }
    public int ActiveApprovalGrants { get; init; }
    public int LiveSkillCount { get; init; }
    public IReadOnlyList<string> LiveSkillNames { get; init; } = [];
}

public sealed class AdminSummarySettings
{
    public required AdminSettingsPersistenceInfo Persistence { get; init; }
    public bool OverridesActive { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class AdminSummaryChannels
{
    public required string AllowlistSemantics { get; init; }
    public IReadOnlyList<ChannelReadinessDto> Readiness { get; init; } = [];
}

public sealed class AdminSummaryRetention
{
    public bool Enabled { get; init; }
    public required RetentionRunStatus Status { get; init; }
}

public sealed class AdminSummaryPlugins
{
    public int Loaded { get; init; }
    public int BlockedByMode { get; init; }
    public IReadOnlyList<OpenClaw.Core.Plugins.PluginLoadReport> Reports { get; init; } = [];
    public IReadOnlyList<PluginHealthSnapshot> Health { get; init; } = [];
}

public sealed class AdminSummaryUsage
{
    public IReadOnlyList<OpenClaw.Core.Observability.ProviderUsageSnapshot> Providers { get; init; } = [];
    public IReadOnlyList<ProviderRouteHealthSnapshot> Routes { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns { get; init; } = [];
}

public sealed class RetentionStatusResponse
{
    public required MemoryRetentionConfig Retention { get; init; }
    public required RetentionRunStatus Status { get; init; }
}

public sealed class RetentionSweepResponse
{
    public bool Success { get; init; }
    public bool DryRun { get; init; }
    public RetentionSweepResult? Result { get; init; }
}

public sealed class RetentionSweepErrorResponse
{
    public bool Success { get; init; }
    public required string Error { get; init; }
}

public sealed class BranchRestoreResponse
{
    public bool Success { get; init; }
    public required string SessionId { get; init; }
    public required string BranchId { get; init; }
    public int TurnCount { get; init; }
}
