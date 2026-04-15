using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using System.Text.Json;

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
    public string? ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public bool IsDefaultRoute { get; init; }
    public bool IsDynamic { get; init; }
    public string? OwnerId { get; init; }
    public string[] Tags { get; init; } = [];
    public string[] ValidationIssues { get; init; } = [];
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
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
}

public sealed class ProviderAdminResponse
{
    public IReadOnlyList<ProviderRouteHealthSnapshot> Routes { get; init; } = [];
    public ModelProfilesStatusResponse? ModelProfiles { get; init; }
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

public sealed class ChannelAuthStatusResponse
{
    public ChannelAuthStatusItem[] Items { get; init; } = [];
}

public sealed class ChannelAuthStatusItem
{
    public required string ChannelId { get; init; }
    public required string State { get; init; }
    public string? Data { get; init; }
    public string? AccountId { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class WhatsAppSetupRequest
{
    public bool Enabled { get; init; }
    public string Type { get; init; } = "official";
    public string DmPolicy { get; init; } = "pairing";
    public string WebhookPath { get; init; } = "/whatsapp/inbound";
    public string? WebhookPublicBaseUrl { get; init; }
    public string WebhookVerifyToken { get; init; } = "openclaw-verify";
    public string WebhookVerifyTokenRef { get; init; } = "env:WHATSAPP_VERIFY_TOKEN";
    public bool ValidateSignature { get; init; }
    public string? WebhookAppSecret { get; init; }
    public string WebhookAppSecretRef { get; init; } = "env:WHATSAPP_APP_SECRET";
    public string? CloudApiToken { get; init; }
    public string CloudApiTokenRef { get; init; } = "env:WHATSAPP_CLOUD_API_TOKEN";
    public string? PhoneNumberId { get; init; }
    public string? BusinessAccountId { get; init; }
    public string? BridgeUrl { get; init; }
    public string? BridgeToken { get; init; }
    public string BridgeTokenRef { get; init; } = "env:WHATSAPP_BRIDGE_TOKEN";
    public bool BridgeSuppressSendExceptions { get; init; }
    public string? PluginId { get; init; }
    public string? PluginConfigJson { get; init; }
    public WhatsAppFirstPartyWorkerConfig? FirstPartyWorker { get; init; }
    public string? FirstPartyWorkerConfigJson { get; init; }
}

public sealed class WhatsAppSetupResponse
{
    public required string ActiveBackend { get; init; }
    public required string ConfiguredType { get; init; }
    public string Message { get; init; } = "";
    public bool RestartRequired { get; init; }
    public bool Enabled { get; init; }
    public string DmPolicy { get; init; } = "pairing";
    public string WebhookPath { get; init; } = "/whatsapp/inbound";
    public string? WebhookPublicBaseUrl { get; init; }
    public string WebhookVerifyToken { get; init; } = "openclaw-verify";
    public bool WebhookVerifyTokenConfigured { get; init; }
    public string WebhookVerifyTokenRef { get; init; } = "env:WHATSAPP_VERIFY_TOKEN";
    public bool ValidateSignature { get; init; }
    public string? WebhookAppSecret { get; init; }
    public bool WebhookAppSecretConfigured { get; init; }
    public string WebhookAppSecretRef { get; init; } = "env:WHATSAPP_APP_SECRET";
    public string? CloudApiToken { get; init; }
    public bool CloudApiTokenConfigured { get; init; }
    public string CloudApiTokenRef { get; init; } = "env:WHATSAPP_CLOUD_API_TOKEN";
    public string? PhoneNumberId { get; init; }
    public string? BusinessAccountId { get; init; }
    public string? BridgeUrl { get; init; }
    public string? BridgeToken { get; init; }
    public bool BridgeTokenConfigured { get; init; }
    public string BridgeTokenRef { get; init; } = "env:WHATSAPP_BRIDGE_TOKEN";
    public bool BridgeSuppressSendExceptions { get; init; }
    public WhatsAppFirstPartyWorkerConfig? FirstPartyWorker { get; init; }
    public string? FirstPartyWorkerConfigJson { get; init; }
    public string? FirstPartyWorkerConfigSchemaJson { get; init; }
    public bool PluginDetected { get; init; }
    public string? PluginId { get; init; }
    public string? PluginConfigJson { get; init; }
    public string? PluginConfigSchemaJson { get; init; }
    public string? PluginUiHintsJson { get; init; }
    public string? PluginWarning { get; init; }
    public bool RestartSupported { get; init; }
    public string? RestartHint { get; init; }
    public string? DerivedWebhookUrl { get; init; }
    public ChannelReadinessDto? Readiness { get; init; }
    public ChannelAuthStatusItem[] AuthStates { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string[] ValidationErrors { get; init; } = [];
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

public static class MemoryNoteClass
{
    public const string General = "general";
    public const string ProjectFact = "project_fact";
    public const string OperationalRunbook = "operational_runbook";
    public const string ApprovedSkill = "approved_skill";
    public const string ApprovedAutomation = "approved_automation";
}

public sealed class MemoryNoteItem
{
    public required string Key { get; init; }
    public required string DisplayKey { get; init; }
    public string MemoryClass { get; init; } = MemoryNoteClass.General;
    public string? ProjectId { get; init; }
    public string Preview { get; init; } = "";
    public string? Content { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryNoteListResponse
{
    public string? Prefix { get; init; }
    public string? Query { get; init; }
    public string? MemoryClass { get; init; }
    public string? ProjectId { get; init; }
    public IReadOnlyList<MemoryNoteItem> Items { get; init; } = [];
}

public sealed class MemoryNoteDetailResponse
{
    public MemoryNoteItem? Note { get; init; }
}

public sealed class MemoryNoteUpsertRequest
{
    public string? Key { get; init; }
    public string? MemoryClass { get; init; }
    public string? ProjectId { get; init; }
    public string Content { get; init; } = "";
}

public sealed class MemoryConsoleExportBundle
{
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<MemoryNoteItem> Notes { get; init; } = [];
    public IReadOnlyList<UserProfile> Profiles { get; init; } = [];
    public IReadOnlyList<LearningProposal> Proposals { get; init; } = [];
    public IReadOnlyList<AutomationDefinition> Automations { get; init; } = [];
}

public sealed class MemoryConsoleImportResponse
{
    public bool Success { get; init; }
    public int NotesImported { get; init; }
    public int ProfilesImported { get; init; }
    public int ProposalsImported { get; init; }
    public int AutomationsImported { get; init; }
    public string Message { get; init; } = "";
}

public sealed class LearningProposalProvenance
{
    public string? ActorId { get; init; }
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public float Confidence { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? ReviewedAtUtc { get; init; }
}

public sealed class ProfileDiffEntry
{
    public required string Path { get; init; }
    public required string ChangeType { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}

public sealed class LearningProposalDetailResponse
{
    public LearningProposal? Proposal { get; init; }
    public UserProfile? BaselineProfile { get; init; }
    public UserProfile? CurrentProfile { get; init; }
    public IReadOnlyList<ProfileDiffEntry> ProfileDiff { get; init; } = [];
    public LearningProposalProvenance? Provenance { get; init; }
    public bool CanRollback { get; init; }
}

public sealed class ProfileExportBundle
{
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<UserProfile> Profiles { get; init; } = [];
    public IReadOnlyList<LearningProposal> Proposals { get; init; } = [];
}

public sealed class ProfileImportResponse
{
    public bool Success { get; init; }
    public int ProfilesImported { get; init; }
    public int ProposalsImported { get; init; }
    public string Message { get; init; } = "";
}

public sealed class SessionMetadataSnapshot
{
    public required string SessionId { get; init; }
    public bool Starred { get; init; }
    public string[] Tags { get; init; } = [];
    public string? ActivePresetId { get; init; }
    public IReadOnlyList<SessionTodoItem> TodoItems { get; init; } = [];
}

public sealed class SessionMetadataUpdateRequest
{
    public bool? Starred { get; init; }
    public string[]? Tags { get; init; }
    public string? ActivePresetId { get; init; }
    public IReadOnlyList<SessionTodoItem>? TodoItems { get; init; }
}

public sealed class SessionTodoItem
{
    public required string Id { get; init; }
    public string Text { get; init; } = "";
    public bool Completed { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
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

public sealed class SecurityPostureResponse
{
    public bool PublicBind { get; init; }
    public bool AuthTokenConfigured { get; init; }
    public bool BrowserSessionCookieSecureEffective { get; init; }
    public bool BrowserSessionsEnabled { get; init; }
    public bool TrustForwardedHeaders { get; init; }
    public bool RequireRequesterMatchForHttpToolApproval { get; init; }
    public bool ToolApprovalRequired { get; init; }
    public string AutonomyMode { get; init; } = "full";
    public bool PluginBridgeEnabled { get; init; }
    public string PluginBridgeTransportMode { get; init; } = "stdio";
    public string PluginBridgeSecurityMode { get; init; } = "legacy";
    public bool SandboxConfigured { get; init; }
    public bool AllowsRawSecretRefsOnPublicBind { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
    public IReadOnlyList<string> Recommendations { get; init; } = [];
}

public sealed class ApprovalSimulationRequest
{
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public string? AutonomyMode { get; init; }
    public bool? RequireToolApproval { get; init; }
    public string[]? ApprovalRequiredTools { get; init; }
}

public sealed class ApprovalSimulationResponse
{
    public required string Decision { get; init; }
    public required string Reason { get; init; }
    public string ToolName { get; init; } = "";
    public string AutonomyMode { get; init; } = "full";
    public bool RequireToolApproval { get; init; }
    public IReadOnlyList<string> ApprovalRequiredTools { get; init; } = [];
}

public sealed class IncidentBundleResponse
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required SecurityPostureResponse Posture { get; init; }
    public required OpenClaw.Core.Observability.MetricsSnapshot Metrics { get; init; }
    public required RetentionRunStatus Retention { get; init; }
    public IReadOnlyList<ApprovalHistoryEntry> ApprovalHistory { get; init; } = [];
    public IReadOnlyList<ProviderPolicyRule> ProviderPolicies { get; init; } = [];
    public IReadOnlyList<ProviderRouteHealthSnapshot> ProviderRoutes { get; init; } = [];
    public IReadOnlyList<ProviderUsageSnapshot> ProviderUsage { get; init; } = [];
    public IReadOnlyList<RuntimeEventEntry> RuntimeEvents { get; init; } = [];
    public IReadOnlyList<WebhookDeadLetterEntry> WebhookDeadLetters { get; init; } = [];
    public IReadOnlyList<PluginHealthSnapshot> PluginHealth { get; init; } = [];
}
