using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Models;

/// <summary>
/// Represents a conversation session between a user and the agent.
/// Designed for zero-allocation serialization via source generators.
/// </summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatTurn> History { get; init; } = [];
    public SessionState State { get; set; } = SessionState.Active;
    
    /// <summary>Optional model override for this specific session (set via /model command).</summary>
    public string? ModelOverride { get; set; }

    /// <summary>Total input tokens consumed across all turns in this session.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Total output tokens consumed across all turns in this session.</summary>
    public long TotalOutputTokens { get; set; }
}

public enum SessionState : byte
{
    Active,
    Paused,
    Expired
}

public sealed record ChatTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public List<ToolInvocation>? ToolCalls { get; init; }
}

public sealed record ToolInvocation
{
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public string? Result { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// AOT-compatible JSON serialization context for all core models.
/// </summary>
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(ChatTurn))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(List<ToolInvocation>))]
[JsonSerializable(typeof(InboundMessage))]
[JsonSerializable(typeof(OutboundMessage))]
[JsonSerializable(typeof(WsClientEnvelope))]
[JsonSerializable(typeof(WsServerEnvelope))]
[JsonSerializable(typeof(GatewayConfig))]
[JsonSerializable(typeof(RuntimeConfig))]
[JsonSerializable(typeof(GatewayRuntimeState))]
[JsonSerializable(typeof(LlmProviderConfig))]
[JsonSerializable(typeof(MemoryConfig))]
[JsonSerializable(typeof(MemorySqliteConfig))]
[JsonSerializable(typeof(MemoryRecallConfig))]
[JsonSerializable(typeof(MemoryRetentionConfig))]
[JsonSerializable(typeof(SecurityConfig))]
[JsonSerializable(typeof(WebSocketConfig))]
[JsonSerializable(typeof(ToolingConfig))]
[JsonSerializable(typeof(ChannelsConfig))]
[JsonSerializable(typeof(SmsChannelConfig))]
[JsonSerializable(typeof(TwilioSmsConfig))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(ContactStoreState))]
[JsonSerializable(typeof(List<ChatTurn>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginsConfig))]
[JsonSerializable(typeof(PluginLoadConfig))]
[JsonSerializable(typeof(PluginEntryConfig))]
[JsonSerializable(typeof(NativeDynamicPluginsConfig))]
[JsonSerializable(typeof(NativeDynamicPluginManifest))]
[JsonSerializable(typeof(PluginToolRegistration))]
[JsonSerializable(typeof(PluginCompatibilityDiagnostic))]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(BridgeInitResult))]
[JsonSerializable(typeof(BridgeToolResult))]
[JsonSerializable(typeof(ToolContentItem))]
[JsonSerializable(typeof(BridgeNotification))]
[JsonSerializable(typeof(BridgeTransportConfig))]
[JsonSerializable(typeof(BridgeTransportRuntimeConfig))]
[JsonSerializable(typeof(BridgeChannelRegistration))]
[JsonSerializable(typeof(BridgeChannelRegistration[]))]
[JsonSerializable(typeof(BridgeCommandRegistration))]
[JsonSerializable(typeof(BridgeCommandRegistration[]))]
[JsonSerializable(typeof(BridgeProviderRegistration))]
[JsonSerializable(typeof(BridgeProviderRegistration[]))]
[JsonSerializable(typeof(BridgeProviderRequest))]
[JsonSerializable(typeof(BridgeProviderOptions))]
[JsonSerializable(typeof(BridgeReasoningOptions))]
[JsonSerializable(typeof(BridgeResponseFormat))]
[JsonSerializable(typeof(BridgeToolMode))]
[JsonSerializable(typeof(BridgeToolDescriptor))]
[JsonSerializable(typeof(BridgeToolDescriptor[]))]
[JsonSerializable(typeof(BridgeInitRequest))]
[JsonSerializable(typeof(BridgeExecuteRequest))]
[JsonSerializable(typeof(BridgeChannelControlRequest))]
[JsonSerializable(typeof(BridgeChannelSendRequest))]
[JsonSerializable(typeof(BridgeCommandExecuteRequest))]
[JsonSerializable(typeof(BridgeHookBeforeRequest))]
[JsonSerializable(typeof(BridgeHookAfterRequest))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>), TypeInfoPropertyName = "BridgeDictionaryStringJsonElement")]
[JsonSerializable(typeof(Dictionary<string, PluginEntryConfig>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(NativePluginsConfig))]
[JsonSerializable(typeof(WebSearchConfig))]
[JsonSerializable(typeof(WebFetchConfig))]
[JsonSerializable(typeof(GitToolsConfig))]
[JsonSerializable(typeof(CodeExecConfig))]
[JsonSerializable(typeof(ImageGenConfig))]
[JsonSerializable(typeof(PdfReadConfig))]
[JsonSerializable(typeof(CalendarConfig))]
[JsonSerializable(typeof(EmailConfig))]
[JsonSerializable(typeof(DatabaseConfig))]
[JsonSerializable(typeof(SkillsConfig))]
[JsonSerializable(typeof(SkillLoadConfig))]
[JsonSerializable(typeof(SkillEntryConfig))]
[JsonSerializable(typeof(Dictionary<string, SkillEntryConfig>))]
[JsonSerializable(typeof(MetricsSnapshot))]
[JsonSerializable(typeof(SessionBranch))]
[JsonSerializable(typeof(List<SessionBranch>))]
[JsonSerializable(typeof(RetentionSweepRequest))]
[JsonSerializable(typeof(RetentionSweepResult))]
[JsonSerializable(typeof(RetentionStoreStats))]
[JsonSerializable(typeof(RetentionRunStatus))]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(DelegationConfig))]
[JsonSerializable(typeof(Dictionary<string, AgentProfile>))]
[JsonSerializable(typeof(TelegramChannelConfig))]
[JsonSerializable(typeof(CronConfig))]
[JsonSerializable(typeof(CronJobConfig))]
[JsonSerializable(typeof(WebhooksConfig))]
[JsonSerializable(typeof(WebhookEndpointConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OperationStatusResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(OpenAiChatCompletionRequest))]
[JsonSerializable(typeof(OpenAiChatCompletionResponse))]
[JsonSerializable(typeof(OpenAiMessage))]
[JsonSerializable(typeof(OpenAiChoice))]
[JsonSerializable(typeof(OpenAiResponseMessage))]
[JsonSerializable(typeof(OpenAiUsage))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(OpenAiStreamChoice))]
[JsonSerializable(typeof(OpenAiDelta))]
[JsonSerializable(typeof(OpenAiToolCallDelta))]
[JsonSerializable(typeof(OpenAiFunctionCallDelta))]
[JsonSerializable(typeof(OpenAiToolOutputDelta))]
[JsonSerializable(typeof(OpenAiToolResultDelta))]
[JsonSerializable(typeof(OpenAiResponseRequest))]
[JsonSerializable(typeof(OpenAiResponseResponse))]
[JsonSerializable(typeof(OpenAiResponseOutput))]
[JsonSerializable(typeof(OpenAiResponseContent))]
[JsonSerializable(typeof(OpenAiResponseStreamResponse))]
[JsonSerializable(typeof(OpenAiResponseStreamItem))]
[JsonSerializable(typeof(OpenAiResponseCreatedEvent))]
[JsonSerializable(typeof(OpenAiResponseError))]
[JsonSerializable(typeof(OpenAiResponseInProgressEvent))]
[JsonSerializable(typeof(OpenAiResponseCompletedEvent))]
[JsonSerializable(typeof(OpenAiResponseFailedEvent))]
[JsonSerializable(typeof(OpenAiResponseOutputItemAddedEvent))]
[JsonSerializable(typeof(OpenAiResponseOutputItemDoneEvent))]
[JsonSerializable(typeof(OpenAiResponseContentPartAddedEvent))]
[JsonSerializable(typeof(OpenAiResponseContentPartDoneEvent))]
[JsonSerializable(typeof(OpenAiResponseOutputTextDeltaEvent))]
[JsonSerializable(typeof(OpenAiResponseOutputTextDoneEvent))]
[JsonSerializable(typeof(OpenAiResponseFunctionCallArgumentsDeltaEvent))]
[JsonSerializable(typeof(OpenAiResponseFunctionCallArgumentsDoneEvent))]
[JsonSerializable(typeof(OpenAiResponseToolOutputDeltaEvent))]
[JsonSerializable(typeof(OpenAiResponseToolResultEvent))]
[JsonSerializable(typeof(List<OpenAiChoice>))]
[JsonSerializable(typeof(List<OpenAiStreamChoice>))]
[JsonSerializable(typeof(List<OpenAiToolCallDelta>))]
[JsonSerializable(typeof(List<OpenAiMessage>))]
[JsonSerializable(typeof(List<OpenAiResponseOutput>))]
[JsonSerializable(typeof(List<OpenAiResponseContent>))]
[JsonSerializable(typeof(List<OpenAiResponseStreamItem>))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.HomeAssistantConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.HomeAssistantPolicyConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.HomeAssistantEventsConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.HomeAssistantEventRule))]
[JsonSerializable(typeof(List<OpenClaw.Core.Plugins.HomeAssistantEventRule>))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.MqttConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.MqttPolicyConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.MqttEventsConfig))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.MqttSubscriptionConfig))]
[JsonSerializable(typeof(List<OpenClaw.Core.Plugins.MqttSubscriptionConfig>))]
[JsonSerializable(typeof(OpenClaw.Core.Pipeline.ToolApprovalRequest))]
[JsonSerializable(typeof(OpenClaw.Core.Pipeline.ToolApprovalDecisionOutcome))]
[JsonSerializable(typeof(OpenClaw.Core.Abstractions.MemoryNoteHit))]
[JsonSerializable(typeof(List<OpenClaw.Core.Abstractions.MemoryNoteHit>))]
[JsonSerializable(typeof(OpenClaw.Core.Pipeline.RecentSendersFile))]
[JsonSerializable(typeof(OpenClaw.Core.Pipeline.RecentSenderEntry))]
[JsonSerializable(typeof(List<OpenClaw.Core.Pipeline.RecentSenderEntry>))]
[JsonSerializable(typeof(OpenClaw.Core.Security.ChannelAllowlistFile))]
[JsonSerializable(typeof(AdminSettingsSnapshot))]
[JsonSerializable(typeof(AdminSettingsPersistenceInfo))]
[JsonSerializable(typeof(AuthSessionRequest))]
[JsonSerializable(typeof(PairingApproveResponse))]
[JsonSerializable(typeof(PairingRevokeResponse))]
[JsonSerializable(typeof(AllowlistSnapshotResponse))]
[JsonSerializable(typeof(SenderMutationResponse))]
[JsonSerializable(typeof(CountMutationResponse))]
[JsonSerializable(typeof(SkillsReloadResponse))]
[JsonSerializable(typeof(AuthSessionResponse))]
[JsonSerializable(typeof(ApprovalListResponse))]
[JsonSerializable(typeof(ApprovalHistoryQuery))]
[JsonSerializable(typeof(ApprovalHistoryEntry))]
[JsonSerializable(typeof(ApprovalHistoryResponse))]
[JsonSerializable(typeof(ChannelFixGuidanceDto))]
[JsonSerializable(typeof(ChannelReadinessDto))]
[JsonSerializable(typeof(AdminSettingsResponse))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(PagedSessionList))]
[JsonSerializable(typeof(SessionListQuery))]
[JsonSerializable(typeof(SessionBranchListResponse))]
[JsonSerializable(typeof(AdminSessionDetailResponse))]
[JsonSerializable(typeof(AdminSessionsResponse))]
[JsonSerializable(typeof(AdminSummaryResponse))]
[JsonSerializable(typeof(AdminSummaryAuth))]
[JsonSerializable(typeof(AdminSummaryRuntime))]
[JsonSerializable(typeof(AdminSummarySettings))]
[JsonSerializable(typeof(AdminSummaryChannels))]
[JsonSerializable(typeof(AdminSummaryRetention))]
[JsonSerializable(typeof(AdminSummaryPlugins))]
[JsonSerializable(typeof(AdminSummaryUsage))]
[JsonSerializable(typeof(OpenClaw.Core.Observability.ProviderUsageSnapshot))]
[JsonSerializable(typeof(List<OpenClaw.Core.Observability.ProviderUsageSnapshot>))]
[JsonSerializable(typeof(MutationResponse))]
[JsonSerializable(typeof(InputTokenComponentEstimate))]
[JsonSerializable(typeof(ProviderPolicyRule))]
[JsonSerializable(typeof(List<ProviderPolicyRule>))]
[JsonSerializable(typeof(ProviderPolicyListResponse))]
[JsonSerializable(typeof(ProviderRouteHealthSnapshot))]
[JsonSerializable(typeof(List<ProviderRouteHealthSnapshot>))]
[JsonSerializable(typeof(ProviderTurnUsageEntry))]
[JsonSerializable(typeof(List<ProviderTurnUsageEntry>))]
[JsonSerializable(typeof(ProviderAdminResponse))]
[JsonSerializable(typeof(RuntimeEventQuery))]
[JsonSerializable(typeof(RuntimeEventEntry))]
[JsonSerializable(typeof(List<RuntimeEventEntry>))]
[JsonSerializable(typeof(RuntimeEventListResponse))]
[JsonSerializable(typeof(PluginOperatorState))]
[JsonSerializable(typeof(List<PluginOperatorState>))]
[JsonSerializable(typeof(PluginHealthSnapshot))]
[JsonSerializable(typeof(List<PluginHealthSnapshot>))]
[JsonSerializable(typeof(PluginListResponse))]
[JsonSerializable(typeof(PluginMutationRequest))]
[JsonSerializable(typeof(ToolApprovalGrant))]
[JsonSerializable(typeof(List<ToolApprovalGrant>))]
[JsonSerializable(typeof(ApprovalGrantListResponse))]
[JsonSerializable(typeof(OperatorAuditQuery))]
[JsonSerializable(typeof(OperatorAuditEntry))]
[JsonSerializable(typeof(List<OperatorAuditEntry>))]
[JsonSerializable(typeof(OperatorAuditListResponse))]
[JsonSerializable(typeof(SessionMetadataSnapshot))]
[JsonSerializable(typeof(List<SessionMetadataSnapshot>))]
[JsonSerializable(typeof(SessionMetadataUpdateRequest))]
[JsonSerializable(typeof(SessionDiffResponse))]
[JsonSerializable(typeof(SessionTimelineResponse))]
[JsonSerializable(typeof(SessionExportItem))]
[JsonSerializable(typeof(List<SessionExportItem>))]
[JsonSerializable(typeof(SessionExportResponse))]
[JsonSerializable(typeof(WebhookDeadLetterEntry))]
[JsonSerializable(typeof(WebhookDeadLetterRecord))]
[JsonSerializable(typeof(List<WebhookDeadLetterEntry>))]
[JsonSerializable(typeof(WebhookDeadLetterResponse))]
[JsonSerializable(typeof(ActorRateLimitPolicy))]
[JsonSerializable(typeof(List<ActorRateLimitPolicy>))]
[JsonSerializable(typeof(ActorRateLimitStatus))]
[JsonSerializable(typeof(List<ActorRateLimitStatus>))]
[JsonSerializable(typeof(ActorRateLimitResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class CoreJsonContext : JsonSerializerContext;
