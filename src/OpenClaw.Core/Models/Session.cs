using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalCacheReadTokens;
    private long _totalCacheWriteTokens;

    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatTurn> History { get; init; } = [];
    public SessionState State { get; set; } = SessionState.Active;
    
    /// <summary>Optional model override for this specific session (set via /model command).</summary>
    public string? ModelOverride { get; set; }

    /// <summary>Optional named model profile selected for this session or route.</summary>
    public string? ModelProfileId { get; set; }

    /// <summary>Optional route/session profile preferences used by profile-aware model selection.</summary>
    public string[] PreferredModelTags { get; set; } = [];

    /// <summary>Optional route/session fallback profile order used when the selected profile lacks required capabilities.</summary>
    public string[] FallbackModelProfileIds { get; set; } = [];

    /// <summary>Optional route/session capability requirements used during profile-aware model selection.</summary>
    public ModelSelectionRequirements ModelRequirements { get; set; } = new();

    /// <summary>Optional route-scoped system prompt appended by gateway routing before runtime execution.</summary>
    public string? SystemPromptOverride { get; set; }

    /// <summary>Optional route-scoped tool preset that overrides the default preset resolution.</summary>
    public string? RoutePresetId { get; set; }

    /// <summary>Optional route-scoped tool allowlist applied in addition to preset filtering.</summary>
    public string[] RouteAllowedTools { get; set; } = [];

    /// <summary>Reasoning effort level for extended thinking (null/off, low, medium, high). Set via /think command.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>When true, shows tool calls and token counts in responses. Set via /verbose command.</summary>
    public bool VerboseMode { get; set; }

    /// <summary>Total input tokens consumed across all turns in this session.</summary>
    public long TotalInputTokens
    {
        get => Interlocked.Read(ref _totalInputTokens);
        set => Interlocked.Exchange(ref _totalInputTokens, value);
    }

    /// <summary>Total output tokens consumed across all turns in this session.</summary>
    public long TotalOutputTokens
    {
        get => Interlocked.Read(ref _totalOutputTokens);
        set => Interlocked.Exchange(ref _totalOutputTokens, value);
    }

    /// <summary>Total input tokens served from upstream prompt cache across all turns.</summary>
    public long TotalCacheReadTokens
    {
        get => Interlocked.Read(ref _totalCacheReadTokens);
        set => Interlocked.Exchange(ref _totalCacheReadTokens, value);
    }

    /// <summary>Total input tokens written into upstream prompt cache across all turns.</summary>
    public long TotalCacheWriteTokens
    {
        get => Interlocked.Read(ref _totalCacheWriteTokens);
        set => Interlocked.Exchange(ref _totalCacheWriteTokens, value);
    }

    /// <summary>Optional contract policy governing this session's execution limits.</summary>
    public ContractPolicy? ContractPolicy { get; set; }

    /// <summary>Structured metadata for sessions created by delegate_agent.</summary>
    public SessionDelegationMetadata? Delegation { get; set; }

    /// <summary>Summaries of delegated child sessions spawned from this session.</summary>
    public List<SessionDelegationChildSummary> DelegatedSessions { get; init; } = [];

    /// <summary>Timestamp when the current contract was attached to this session.</summary>
    public DateTimeOffset? ContractAttachedAtUtc { get; set; }

    /// <summary>Session token counters at the time the current contract was attached.</summary>
    public long ContractBaselineInputTokens { get; set; }
    public long ContractBaselineOutputTokens { get; set; }

    /// <summary>Session tool-call count at the time the current contract was attached.</summary>
    public int ContractBaselineToolCalls { get; set; }

    /// <summary>Accumulated USD cost incurred since the current contract was attached.</summary>
    public decimal ContractAccumulatedCostUsd { get; set; }

    public void AddTokenUsage(long inputTokens, long outputTokens)
    {
        if (inputTokens != 0)
            Interlocked.Add(ref _totalInputTokens, inputTokens);
        if (outputTokens != 0)
            Interlocked.Add(ref _totalOutputTokens, outputTokens);
    }

    public void AddCacheUsage(long cacheReadTokens, long cacheWriteTokens)
    {
        if (cacheReadTokens != 0)
            Interlocked.Add(ref _totalCacheReadTokens, cacheReadTokens);
        if (cacheWriteTokens != 0)
            Interlocked.Add(ref _totalCacheWriteTokens, cacheWriteTokens);
    }

    public long GetTotalTokens()
        => TotalInputTokens + TotalOutputTokens;
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
    public string? ResultStatus { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextStep { get; init; }
}

public sealed class SessionDelegationMetadata
{
    public string? ParentSessionId { get; set; }
    public string? ParentChannelId { get; set; }
    public string? ParentSenderId { get; set; }
    public string Profile { get; set; } = "";
    public string RequestedTask { get; set; } = "";
    public string[] AllowedTools { get; set; } = [];
    public int Depth { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public string? FinalResponsePreview { get; set; }
    public IReadOnlyList<SessionDelegationToolUsage> ToolUsage { get; set; } = [];
    public IReadOnlyList<SessionDelegationChangeSummary> ProposedChanges { get; set; } = [];
}

public sealed class SessionDelegationToolUsage
{
    public required string ToolName { get; init; }
    public string Action { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool IsMutation { get; init; }
    public int Count { get; init; }
}

public sealed class SessionDelegationChangeSummary
{
    public required string ToolName { get; init; }
    public string Action { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed class SessionDelegationChildSummary
{
    public required string SessionId { get; init; }
    public string Profile { get; set; } = "";
    public string TaskPreview { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public IReadOnlyList<SessionDelegationToolUsage> ToolUsage { get; set; } = [];
    public IReadOnlyList<SessionDelegationChangeSummary> ProposedChanges { get; set; } = [];
    public string? FinalResponsePreview { get; set; }
}

/// <summary>
/// AOT-compatible JSON serialization context for all core models.
/// </summary>
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(ChatTurn))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(List<ToolInvocation>))]
[JsonSerializable(typeof(SessionDelegationMetadata))]
[JsonSerializable(typeof(SessionDelegationToolUsage))]
[JsonSerializable(typeof(List<SessionDelegationToolUsage>))]
[JsonSerializable(typeof(SessionDelegationChangeSummary))]
[JsonSerializable(typeof(List<SessionDelegationChangeSummary>))]
[JsonSerializable(typeof(SessionDelegationChildSummary))]
[JsonSerializable(typeof(List<SessionDelegationChildSummary>))]
[JsonSerializable(typeof(InboundMessage))]
[JsonSerializable(typeof(OutboundMessage))]
[JsonSerializable(typeof(WsClientEnvelope))]
[JsonSerializable(typeof(WsServerEnvelope))]
[JsonSerializable(typeof(GatewayConfig))]
[JsonSerializable(typeof(RuntimeConfig))]
[JsonSerializable(typeof(GatewayRuntimeState))]
[JsonSerializable(typeof(LlmProviderConfig))]
[JsonSerializable(typeof(PromptCachingConfig))]
[JsonSerializable(typeof(DiagnosticsConfig))]
[JsonSerializable(typeof(PromptCacheTraceConfig))]
[JsonSerializable(typeof(ModelsConfig))]
[JsonSerializable(typeof(ModelProfileConfig))]
[JsonSerializable(typeof(List<ModelProfileConfig>))]
[JsonSerializable(typeof(ModelCapabilities))]
[JsonSerializable(typeof(ModelSelectionRequirements))]
[JsonSerializable(typeof(ModelProfile))]
[JsonSerializable(typeof(List<ModelProfile>))]
[JsonSerializable(typeof(ModelProfileStatus))]
[JsonSerializable(typeof(List<ModelProfileStatus>))]
[JsonSerializable(typeof(ModelProfilesStatusResponse))]
[JsonSerializable(typeof(ModelSelectionDoctorResponse))]
[JsonSerializable(typeof(ModelSelectionDescriptor))]
[JsonSerializable(typeof(ModelEvaluationRequest))]
[JsonSerializable(typeof(ModelEvaluationScenarioResult))]
[JsonSerializable(typeof(List<ModelEvaluationScenarioResult>))]
[JsonSerializable(typeof(ModelEvaluationProfileReport))]
[JsonSerializable(typeof(List<ModelEvaluationProfileReport>))]
[JsonSerializable(typeof(ModelEvaluationReport))]
[JsonSerializable(typeof(TokenCostRateConfig))]
[JsonSerializable(typeof(Dictionary<string, TokenCostRateConfig>))]
[JsonSerializable(typeof(MemoryConfig))]
[JsonSerializable(typeof(MemorySqliteConfig))]
[JsonSerializable(typeof(MemoryRecallConfig))]
[JsonSerializable(typeof(MemoryRetentionConfig))]
[JsonSerializable(typeof(SecurityConfig))]
[JsonSerializable(typeof(WebSocketConfig))]
[JsonSerializable(typeof(ToolingConfig))]
[JsonSerializable(typeof(ToolsetConfig))]
[JsonSerializable(typeof(Dictionary<string, ToolsetConfig>))]
[JsonSerializable(typeof(ToolPresetConfig))]
[JsonSerializable(typeof(Dictionary<string, ToolPresetConfig>))]
[JsonSerializable(typeof(ResolvedToolPreset))]
[JsonSerializable(typeof(List<ResolvedToolPreset>))]
[JsonSerializable(typeof(ToolActionDescriptor))]
[JsonSerializable(typeof(SandboxConfig))]
[JsonSerializable(typeof(SandboxToolConfig))]
[JsonSerializable(typeof(SandboxExecutionRequest))]
[JsonSerializable(typeof(SandboxResult))]
[JsonSerializable(typeof(ExecutionConfig))]
[JsonSerializable(typeof(ExecutionBackendProfileConfig))]
[JsonSerializable(typeof(Dictionary<string, ExecutionBackendProfileConfig>))]
[JsonSerializable(typeof(ExecutionToolRouteConfig))]
[JsonSerializable(typeof(Dictionary<string, ExecutionToolRouteConfig>))]
[JsonSerializable(typeof(ExecutionRequest))]
[JsonSerializable(typeof(ExecutionResult))]
[JsonSerializable(typeof(ExecutionBackendCapabilities))]
[JsonSerializable(typeof(ExecutionProcessStartRequest))]
[JsonSerializable(typeof(ExecutionProcessHandle))]
[JsonSerializable(typeof(List<ExecutionProcessHandle>))]
[JsonSerializable(typeof(ExecutionProcessStatus))]
[JsonSerializable(typeof(List<ExecutionProcessStatus>))]
[JsonSerializable(typeof(ExecutionProcessLogRequest))]
[JsonSerializable(typeof(ExecutionProcessLogResult))]
[JsonSerializable(typeof(ExecutionProcessInputRequest))]
[JsonSerializable(typeof(MultimodalConfig))]
[JsonSerializable(typeof(CodingBackendsConfig))]
[JsonSerializable(typeof(CodingCliBackendConfig))]
[JsonSerializable(typeof(BackendCredentialSourceConfig))]
[JsonSerializable(typeof(ConnectedAccount))]
[JsonSerializable(typeof(List<ConnectedAccount>))]
[JsonSerializable(typeof(ConnectedAccountSecretRef))]
[JsonSerializable(typeof(ConnectedAccountSecretPayload))]
[JsonSerializable(typeof(ResolvedBackendCredential))]
[JsonSerializable(typeof(BackendDefinition))]
[JsonSerializable(typeof(List<BackendDefinition>))]
[JsonSerializable(typeof(BackendCapabilities))]
[JsonSerializable(typeof(BackendAccessPolicy))]
[JsonSerializable(typeof(BackendSessionHandle))]
[JsonSerializable(typeof(BackendSessionRecord))]
[JsonSerializable(typeof(List<BackendSessionRecord>))]
[JsonSerializable(typeof(StartBackendSessionRequest))]
[JsonSerializable(typeof(BackendInput))]
[JsonSerializable(typeof(BackendProbeRequest))]
[JsonSerializable(typeof(BackendProbeResult))]
[JsonSerializable(typeof(BackendEvent))]
[JsonSerializable(typeof(List<BackendEvent>))]
[JsonSerializable(typeof(BackendAssistantMessageEvent))]
[JsonSerializable(typeof(BackendStdoutOutputEvent))]
[JsonSerializable(typeof(BackendStderrOutputEvent))]
[JsonSerializable(typeof(BackendToolCallRequestedEvent))]
[JsonSerializable(typeof(BackendShellCommandProposedEvent))]
[JsonSerializable(typeof(BackendShellCommandExecutedEvent))]
[JsonSerializable(typeof(BackendPatchProposedEvent))]
[JsonSerializable(typeof(BackendPatchAppliedEvent))]
[JsonSerializable(typeof(BackendFileReadEvent))]
[JsonSerializable(typeof(BackendFileWriteEvent))]
[JsonSerializable(typeof(BackendErrorEvent))]
[JsonSerializable(typeof(BackendSessionCompletedEvent))]
[JsonSerializable(typeof(TextToSpeechConfig))]
[JsonSerializable(typeof(GeminiLiveConfig))]
[JsonSerializable(typeof(ElevenLabsConfig))]
[JsonSerializable(typeof(StoredMediaAsset))]
[JsonSerializable(typeof(LiveSessionOpenRequest))]
[JsonSerializable(typeof(LiveSessionOpened))]
[JsonSerializable(typeof(LiveClientEnvelope))]
[JsonSerializable(typeof(LiveServerEnvelope))]
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
[JsonSerializable(typeof(AutomationsConfig))]
[JsonSerializable(typeof(AutomationRetryPolicy))]
[JsonSerializable(typeof(AutomationDefinition))]
[JsonSerializable(typeof(List<AutomationDefinition>))]
[JsonSerializable(typeof(AutomationRunState))]
[JsonSerializable(typeof(AutomationRunRecord))]
[JsonSerializable(typeof(List<AutomationRunRecord>))]
[JsonSerializable(typeof(AutomationTemplate))]
[JsonSerializable(typeof(List<AutomationTemplate>))]
[JsonSerializable(typeof(AutomationTemplateListResponse))]
[JsonSerializable(typeof(AutomationValidationIssue))]
[JsonSerializable(typeof(List<AutomationValidationIssue>))]
[JsonSerializable(typeof(AutomationPreview))]
[JsonSerializable(typeof(ProfilesConfig))]
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(List<UserProfile>))]
[JsonSerializable(typeof(UserProfileFact))]
[JsonSerializable(typeof(List<UserProfileFact>))]
[JsonSerializable(typeof(SessionSearchQuery))]
[JsonSerializable(typeof(SessionSearchHit))]
[JsonSerializable(typeof(List<SessionSearchHit>))]
[JsonSerializable(typeof(SessionSearchResult))]
[JsonSerializable(typeof(LearningConfig))]
[JsonSerializable(typeof(LearningProposal))]
[JsonSerializable(typeof(List<LearningProposal>))]
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
[JsonSerializable(typeof(OpenClaw.Core.Plugins.NotionConfig))]
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
[JsonSerializable(typeof(WhatsAppFirstPartyWorkerConfig))]
[JsonSerializable(typeof(WhatsAppWorkerAccountConfig))]
[JsonSerializable(typeof(List<WhatsAppWorkerAccountConfig>))]
[JsonSerializable(typeof(AuthSessionRequest))]
[JsonSerializable(typeof(OperatorTokenExchangeRequest))]
[JsonSerializable(typeof(PairingApproveResponse))]
[JsonSerializable(typeof(PairingRevokeResponse))]
[JsonSerializable(typeof(AllowlistSnapshotResponse))]
[JsonSerializable(typeof(SenderMutationResponse))]
[JsonSerializable(typeof(CountMutationResponse))]
[JsonSerializable(typeof(SkillsReloadResponse))]
[JsonSerializable(typeof(AuthSessionResponse))]
[JsonSerializable(typeof(OperatorTokenExchangeResponse))]
[JsonSerializable(typeof(ApprovalListResponse))]
[JsonSerializable(typeof(ApprovalHistoryQuery))]
[JsonSerializable(typeof(ApprovalHistoryEntry))]
[JsonSerializable(typeof(ApprovalHistoryResponse))]
[JsonSerializable(typeof(ChannelFixGuidanceDto))]
[JsonSerializable(typeof(ChannelReadinessDto))]
[JsonSerializable(typeof(AdminSettingsResponse))]
[JsonSerializable(typeof(HeartbeatConfigDto))]
[JsonSerializable(typeof(HeartbeatTaskDto))]
[JsonSerializable(typeof(HeartbeatConditionDto))]
[JsonSerializable(typeof(List<HeartbeatConditionDto>))]
[JsonSerializable(typeof(List<HeartbeatTaskDto>))]
[JsonSerializable(typeof(HeartbeatTemplateDto))]
[JsonSerializable(typeof(List<HeartbeatTemplateDto>))]
[JsonSerializable(typeof(HeartbeatSuggestionDto))]
[JsonSerializable(typeof(List<HeartbeatSuggestionDto>))]
[JsonSerializable(typeof(HeartbeatValidationIssueDto))]
[JsonSerializable(typeof(List<HeartbeatValidationIssueDto>))]
[JsonSerializable(typeof(HeartbeatCostEstimateDto))]
[JsonSerializable(typeof(HeartbeatRunStatusDto))]
[JsonSerializable(typeof(HeartbeatPreviewResponse))]
[JsonSerializable(typeof(HeartbeatStatusResponse))]
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
[JsonSerializable(typeof(OperatorDashboardSnapshot))]
[JsonSerializable(typeof(DashboardNamedMetric))]
[JsonSerializable(typeof(List<DashboardNamedMetric>))]
[JsonSerializable(typeof(DashboardSessionSummary))]
[JsonSerializable(typeof(DashboardApprovalSummary))]
[JsonSerializable(typeof(DashboardMemorySummary))]
[JsonSerializable(typeof(DashboardAutomationItem))]
[JsonSerializable(typeof(List<DashboardAutomationItem>))]
[JsonSerializable(typeof(DashboardAutomationSummary))]
[JsonSerializable(typeof(DashboardLearningSummary))]
[JsonSerializable(typeof(DashboardDelegationSummary))]
[JsonSerializable(typeof(DashboardChannelSummary))]
[JsonSerializable(typeof(DashboardPluginSummary))]
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
[JsonSerializable(typeof(IntegrationStatusResponse))]
[JsonSerializable(typeof(IntegrationSessionsResponse))]
[JsonSerializable(typeof(IntegrationSessionDetailResponse))]
[JsonSerializable(typeof(IntegrationSessionTimelineResponse))]
[JsonSerializable(typeof(IntegrationMessageRequest))]
[JsonSerializable(typeof(IntegrationMessageResponse))]
[JsonSerializable(typeof(ConnectedAccountCreateRequest))]
[JsonSerializable(typeof(BackendCredentialResolutionRequest))]
[JsonSerializable(typeof(BackendCredentialResolutionResponse))]
[JsonSerializable(typeof(IntegrationAccountsResponse))]
[JsonSerializable(typeof(IntegrationConnectedAccountResponse))]
[JsonSerializable(typeof(IntegrationBackendsResponse))]
[JsonSerializable(typeof(IntegrationBackendResponse))]
[JsonSerializable(typeof(IntegrationBackendSessionResponse))]
[JsonSerializable(typeof(IntegrationBackendEventsResponse))]
[JsonSerializable(typeof(IntegrationProfileUpdateRequest))]
[JsonSerializable(typeof(IntegrationTextToSpeechRequest))]
[JsonSerializable(typeof(IntegrationTextToSpeechResponse))]
[JsonSerializable(typeof(AutomationRunRequest))]
[JsonSerializable(typeof(LearningProposalReviewRequest))]
[JsonSerializable(typeof(IntegrationRuntimeEventsResponse))]
[JsonSerializable(typeof(IntegrationApprovalsResponse))]
[JsonSerializable(typeof(IntegrationApprovalHistoryResponse))]
[JsonSerializable(typeof(IntegrationProvidersResponse))]
[JsonSerializable(typeof(IntegrationPluginsResponse))]
[JsonSerializable(typeof(IntegrationCompatibilityCatalogResponse))]
[JsonSerializable(typeof(IntegrationOperatorAuditResponse))]
[JsonSerializable(typeof(IntegrationDashboardResponse))]
[JsonSerializable(typeof(IntegrationSessionSearchResponse))]
[JsonSerializable(typeof(IntegrationProfilesResponse))]
[JsonSerializable(typeof(IntegrationProfileResponse))]
[JsonSerializable(typeof(IntegrationAutomationsResponse))]
[JsonSerializable(typeof(IntegrationAutomationDetailResponse))]
[JsonSerializable(typeof(IntegrationAutomationRunsResponse))]
[JsonSerializable(typeof(IntegrationAutomationRunDetailResponse))]
[JsonSerializable(typeof(LearningProposalListResponse))]
[JsonSerializable(typeof(IntegrationToolPresetsResponse))]
[JsonSerializable(typeof(RuntimeEventQuery))]
[JsonSerializable(typeof(RuntimeEventEntry))]
[JsonSerializable(typeof(List<RuntimeEventEntry>))]
[JsonSerializable(typeof(RuntimeEventListResponse))]
[JsonSerializable(typeof(PluginOperatorState))]
[JsonSerializable(typeof(List<PluginOperatorState>))]
[JsonSerializable(typeof(PluginHealthSnapshot))]
[JsonSerializable(typeof(List<PluginHealthSnapshot>))]
[JsonSerializable(typeof(PluginListResponse))]
[JsonSerializable(typeof(CompatibilityCatalogEntry))]
[JsonSerializable(typeof(List<CompatibilityCatalogEntry>))]
[JsonSerializable(typeof(CompatibilityCatalogResponse))]
[JsonSerializable(typeof(SkillHealthSnapshot))]
[JsonSerializable(typeof(List<SkillHealthSnapshot>))]
[JsonSerializable(typeof(SkillListResponse))]
[JsonSerializable(typeof(PluginMutationRequest))]
[JsonSerializable(typeof(ToolApprovalGrant))]
[JsonSerializable(typeof(List<ToolApprovalGrant>))]
[JsonSerializable(typeof(ApprovalGrantListResponse))]
[JsonSerializable(typeof(OperatorAuditQuery))]
[JsonSerializable(typeof(OperatorAuditEntry))]
[JsonSerializable(typeof(List<OperatorAuditEntry>))]
[JsonSerializable(typeof(OperatorAuditListResponse))]
[JsonSerializable(typeof(OperatorIdentitySnapshot))]
[JsonSerializable(typeof(OperatorAccountSummary))]
[JsonSerializable(typeof(List<OperatorAccountSummary>))]
[JsonSerializable(typeof(OperatorAccountTokenSummary))]
[JsonSerializable(typeof(List<OperatorAccountTokenSummary>))]
[JsonSerializable(typeof(OperatorAccountListResponse))]
[JsonSerializable(typeof(OperatorAccountDetailResponse))]
[JsonSerializable(typeof(OperatorAccountCreateRequest))]
[JsonSerializable(typeof(OperatorAccountUpdateRequest))]
[JsonSerializable(typeof(OperatorAccountTokenCreateRequest))]
[JsonSerializable(typeof(OperatorAccountTokenCreateResponse))]
[JsonSerializable(typeof(OrganizationPolicySnapshot))]
[JsonSerializable(typeof(OrganizationPolicyResponse))]
[JsonSerializable(typeof(SetupArtifactStatusItem))]
[JsonSerializable(typeof(List<SetupArtifactStatusItem>))]
[JsonSerializable(typeof(BrowserToolCapabilitySummary))]
[JsonSerializable(typeof(SetupStatusResponse))]
[JsonSerializable(typeof(SetupVerificationCheck))]
[JsonSerializable(typeof(List<SetupVerificationCheck>))]
[JsonSerializable(typeof(SetupVerificationResponse))]
[JsonSerializable(typeof(SetupVerificationSnapshot))]
[JsonSerializable(typeof(UpgradeRollbackSnapshotArtifact))]
[JsonSerializable(typeof(List<UpgradeRollbackSnapshotArtifact>))]
[JsonSerializable(typeof(UpgradeRollbackSnapshot))]
[JsonSerializable(typeof(DoctorCheckItem))]
[JsonSerializable(typeof(List<DoctorCheckItem>))]
[JsonSerializable(typeof(DoctorReportResponse))]
[JsonSerializable(typeof(ObservabilityMetricPoint))]
[JsonSerializable(typeof(List<ObservabilityMetricPoint>))]
[JsonSerializable(typeof(ObservabilitySummaryCard))]
[JsonSerializable(typeof(List<ObservabilitySummaryCard>))]
[JsonSerializable(typeof(ObservabilitySummaryResponse))]
[JsonSerializable(typeof(ObservabilitySeriesResponse))]
[JsonSerializable(typeof(AuditExportManifest))]
[JsonSerializable(typeof(UpstreamMigrationCompatibilityItem))]
[JsonSerializable(typeof(List<UpstreamMigrationCompatibilityItem>))]
[JsonSerializable(typeof(UpstreamMigrationSkillItem))]
[JsonSerializable(typeof(List<UpstreamMigrationSkillItem>))]
[JsonSerializable(typeof(UpstreamMigrationPluginItem))]
[JsonSerializable(typeof(List<UpstreamMigrationPluginItem>))]
[JsonSerializable(typeof(UpstreamMigrationReport))]
[JsonSerializable(typeof(MemoryNoteItem))]
[JsonSerializable(typeof(List<MemoryNoteItem>))]
[JsonSerializable(typeof(MemoryNoteListResponse))]
[JsonSerializable(typeof(MemoryNoteDetailResponse))]
[JsonSerializable(typeof(MemoryNoteUpsertRequest))]
[JsonSerializable(typeof(MemoryConsoleExportBundle))]
[JsonSerializable(typeof(MemoryConsoleImportResponse))]
[JsonSerializable(typeof(ManagedSkillBundleItem))]
[JsonSerializable(typeof(List<ManagedSkillBundleItem>))]
[JsonSerializable(typeof(AgentBundleExportBundle))]
[JsonSerializable(typeof(AgentBundleImportResponse))]
[JsonSerializable(typeof(LearningProposalProvenance))]
[JsonSerializable(typeof(ProfileDiffEntry))]
[JsonSerializable(typeof(List<ProfileDiffEntry>))]
[JsonSerializable(typeof(LearningProposalDetailResponse))]
[JsonSerializable(typeof(ProfileExportBundle))]
[JsonSerializable(typeof(ProfileImportResponse))]
[JsonSerializable(typeof(SessionMetadataSnapshot))]
[JsonSerializable(typeof(List<SessionMetadataSnapshot>))]
[JsonSerializable(typeof(SessionTodoItem))]
[JsonSerializable(typeof(List<SessionTodoItem>))]
[JsonSerializable(typeof(SessionMetadataUpdateRequest))]
[JsonSerializable(typeof(SessionPromotionRequest))]
[JsonSerializable(typeof(SessionPromotionResponse))]
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
[JsonSerializable(typeof(SecurityPostureResponse))]
[JsonSerializable(typeof(ApprovalSimulationRequest))]
[JsonSerializable(typeof(ApprovalSimulationResponse))]
[JsonSerializable(typeof(IncidentBundleResponse))]
[JsonSerializable(typeof(RetentionStatusResponse))]
[JsonSerializable(typeof(RetentionSweepResponse))]
[JsonSerializable(typeof(RetentionSweepErrorResponse))]
[JsonSerializable(typeof(BranchRestoreResponse))]
[JsonSerializable(typeof(ContractPolicy))]
[JsonSerializable(typeof(ScopedCapability))]
[JsonSerializable(typeof(ScopedCapability[]))]
[JsonSerializable(typeof(ContractValidationResult))]
[JsonSerializable(typeof(ContractExecutionSnapshot))]
[JsonSerializable(typeof(ContractCreateRequest))]
[JsonSerializable(typeof(ContractCreateResponse))]
[JsonSerializable(typeof(ContractValidateRequest))]
[JsonSerializable(typeof(ContractStatusResponse))]
[JsonSerializable(typeof(ContractListResponse))]
[JsonSerializable(typeof(Dictionary<string, decimal>))]
[JsonSerializable(typeof(VerificationPolicy))]
[JsonSerializable(typeof(VerificationCheckDefinition))]
[JsonSerializable(typeof(VerificationCheckDefinition[]))]
[JsonSerializable(typeof(VerificationCheckResult))]
[JsonSerializable(typeof(List<VerificationCheckResult>))]
[JsonSerializable(typeof(ToolUsageSnapshot))]
[JsonSerializable(typeof(List<ToolUsageSnapshot>))]
[JsonSerializable(typeof(OpenClaw.Core.Observability.ToolAuditEntry))]
[JsonSerializable(typeof(IReadOnlyList<OpenClaw.Core.Observability.ToolAuditEntry>))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeMediaAttachment))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeMediaAttachment[]))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeChannelTypingRequest))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeChannelReceiptRequest))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeChannelReactionRequest))]
[JsonSerializable(typeof(OpenClaw.Core.Plugins.BridgeChannelAuthEvent))]
[JsonSerializable(typeof(ChannelAuthStatusResponse))]
[JsonSerializable(typeof(ChannelAuthStatusItem))]
[JsonSerializable(typeof(WhatsAppSetupRequest))]
[JsonSerializable(typeof(WhatsAppSetupResponse))]
[JsonSerializable(typeof(SlackChannelConfig))]
[JsonSerializable(typeof(DiscordChannelConfig))]
[JsonSerializable(typeof(SignalChannelConfig))]
[JsonSerializable(typeof(RoutingConfig))]
[JsonSerializable(typeof(AgentRouteConfig))]
[JsonSerializable(typeof(TailscaleConfig))]
[JsonSerializable(typeof(GmailPubSubConfig))]
[JsonSerializable(typeof(MdnsConfig))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class CoreJsonContext : JsonSerializerContext;
