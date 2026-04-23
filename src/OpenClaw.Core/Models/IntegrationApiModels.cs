namespace OpenClaw.Core.Models;

public sealed class IntegrationStatusResponse
{
    public required HealthResponse Health { get; init; }
    public required GatewayRuntimeState Runtime { get; init; }
    public required OpenClaw.Core.Observability.MetricsSnapshot Metrics { get; init; }
    public int ActiveSessions { get; init; }
    public int PendingApprovals { get; init; }
    public int ActiveApprovalGrants { get; init; }
}

public sealed class IntegrationSessionsResponse
{
    public required SessionListQuery Filters { get; init; }
    public IReadOnlyList<SessionSummary> Active { get; init; } = [];
    public required PagedSessionList Persisted { get; init; }
}

public sealed class IntegrationSessionDetailResponse
{
    public Session? Session { get; init; }
    public bool IsActive { get; init; }
    public int BranchCount { get; init; }
    public SessionMetadataSnapshot? Metadata { get; init; }
}

public sealed class IntegrationMessageRequest
{
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public required string Text { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
}

public sealed class IntegrationMessageResponse
{
    public bool Accepted { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string SessionId { get; init; }
    public string? MessageId { get; init; }
}

public sealed class IntegrationProfileUpdateRequest
{
    public required UserProfile Profile { get; init; }
}

public sealed class IntegrationTextToSpeechRequest
{
    public required string Text { get; init; }
    public string? Provider { get; init; }
    public string? VoiceName { get; init; }
    public string? VoiceId { get; init; }
    public string? Model { get; init; }
}

public sealed class IntegrationTextToSpeechResponse
{
    public required string Provider { get; init; }
    public required string AssetId { get; init; }
    public required string MediaType { get; init; }
    public required string DataUrl { get; init; }
    public string? Marker { get; init; }
}

public sealed class AutomationRunRequest
{
    public bool DryRun { get; init; }
}

public sealed class LearningProposalReviewRequest
{
    public string? Reason { get; init; }
}

public sealed class IntegrationRuntimeEventsResponse
{
    public required RuntimeEventQuery Query { get; init; }
    public IReadOnlyList<RuntimeEventEntry> Items { get; init; } = [];
}

public sealed class IntegrationApprovalsResponse
{
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public IReadOnlyList<OpenClaw.Core.Pipeline.ToolApprovalRequest> Items { get; init; } = [];
}

public sealed class IntegrationApprovalHistoryResponse
{
    public required ApprovalHistoryQuery Query { get; init; }
    public IReadOnlyList<ApprovalHistoryEntry> Items { get; init; } = [];
}

public sealed class IntegrationProvidersResponse
{
    public ModelProfilesStatusResponse? ModelProfiles { get; init; }
    public IReadOnlyList<ProviderRouteHealthSnapshot> Routes { get; init; } = [];
    public IReadOnlyList<OpenClaw.Core.Observability.ProviderUsageSnapshot> Usage { get; init; } = [];
    public IReadOnlyList<ProviderPolicyRule> Policies { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns { get; init; } = [];
}

public sealed class IntegrationPluginsResponse
{
    public IReadOnlyList<PluginHealthSnapshot> Items { get; init; } = [];
}

public sealed class IntegrationCompatibilityCatalogResponse
{
    public required CompatibilityCatalogResponse Catalog { get; init; }
}

public sealed class IntegrationOperatorAuditResponse
{
    public required OperatorAuditQuery Query { get; init; }
    public IReadOnlyList<OperatorAuditEntry> Items { get; init; } = [];
}

public sealed class IntegrationSessionTimelineResponse
{
    public required string SessionId { get; init; }
    public IReadOnlyList<RuntimeEventEntry> Events { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> ProviderTurns { get; init; } = [];
}

public sealed class IntegrationDashboardResponse
{
    public required IntegrationStatusResponse Status { get; init; }
    public required IntegrationApprovalsResponse Approvals { get; init; }
    public required IntegrationApprovalHistoryResponse ApprovalHistory { get; init; }
    public required IntegrationProvidersResponse Providers { get; init; }
    public required IntegrationPluginsResponse Plugins { get; init; }
    public required IntegrationRuntimeEventsResponse Events { get; init; }
    public required OperatorDashboardSnapshot Operator { get; init; }
}

public sealed class IntegrationSessionSearchResponse
{
    public required SessionSearchResult Result { get; init; }
}

public sealed class IntegrationProfilesResponse
{
    public IReadOnlyList<UserProfile> Items { get; init; } = [];
}

public sealed class IntegrationProfileResponse
{
    public UserProfile? Profile { get; init; }
}

public sealed class IntegrationAutomationsResponse
{
    public IReadOnlyList<AutomationDefinition> Items { get; init; } = [];
}

public sealed class IntegrationAutomationDetailResponse
{
    public AutomationDefinition? Automation { get; init; }
    public AutomationRunState? RunState { get; init; }
}

public sealed class IntegrationAutomationRunsResponse
{
    public required string AutomationId { get; init; }
    public AutomationRunState? RunState { get; init; }
    public IReadOnlyList<AutomationRunRecord> Items { get; init; } = [];
}

public sealed class IntegrationAutomationRunDetailResponse
{
    public required string AutomationId { get; init; }
    public AutomationDefinition? Automation { get; init; }
    public AutomationRunState? RunState { get; init; }
    public AutomationRunRecord? Run { get; init; }
}

public sealed class LearningProposalListResponse
{
    public IReadOnlyList<LearningProposal> Items { get; init; } = [];
}

public sealed class IntegrationToolPresetsResponse
{
    public IReadOnlyList<ResolvedToolPreset> Items { get; init; } = [];
}
