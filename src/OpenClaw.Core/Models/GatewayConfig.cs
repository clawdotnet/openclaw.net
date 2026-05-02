using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Models;

/// <summary>
/// Configuration for the OpenClaw gateway. Loaded from appsettings or env vars.
/// </summary>
public sealed class GatewayConfig
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 18789;
    public string? AuthToken { get; set; }
    public RuntimeConfig Runtime { get; set; } = new();
    public LlmProviderConfig Llm { get; set; } = new();
    public ModelsConfig Models { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public WebSocketConfig WebSocket { get; set; } = new();
    public CanvasConfig Canvas { get; set; } = new();
    public ToolingConfig Tooling { get; set; } = new();
    public SandboxConfig Sandbox { get; set; } = new();
    public ExecutionConfig Execution { get; set; } = new();
    public CodingBackendsConfig CodingBackends { get; set; } = new();
    public MultimodalConfig Multimodal { get; set; } = new();
    public ChannelsConfig Channels { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();
    public SkillsConfig Skills { get; set; } = new();
    public DelegationConfig Delegation { get; set; } = new();
    public CronConfig Cron { get; set; } = new();
    public AutomationsConfig Automations { get; set; } = new();
    public ProfilesConfig Profiles { get; set; } = new();
    public LearningConfig Learning { get; set; } = new();
    public WebhooksConfig Webhooks { get; set; } = new();
    public RoutingConfig Routing { get; set; } = new();
    public TailscaleConfig Tailscale { get; set; } = new();
    public GmailPubSubConfig GmailPubSub { get; set; } = new();
    public MdnsConfig Mdns { get; set; } = new();
    public DiagnosticsConfig Diagnostics { get; set; } = new();
    public string UsageFooter { get; set; } = "off"; // "off", "tokens", "full"

    public int MaxConcurrentSessions { get; set; } = 64;
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>Max total tokens (input + output) per session. 0 = unlimited.</summary>
    public long SessionTokenBudget { get; set; } = 0;

    /// <summary>
    /// When true, reject turns early if the estimated prompt tokens alone would exhaust the remaining session budget.
    /// Disabled by default for backward compatibility with historical token-budget semantics.
    /// </summary>
    public bool EnableEstimatedTokenAdmissionControl { get; set; } = false;

    /// <summary>Max messages per minute per session at the agent level. 0 = unlimited.</summary>
    public int SessionRateLimitPerMinute { get; set; } = 0;

    /// <summary>Seconds to wait for in-flight requests to complete during shutdown. 0 = no drain.</summary>
    public int GracefulShutdownSeconds { get; set; } = 15;

    /// <summary>
    /// Token cost rates by "provider:model" key, in USD per 1K tokens.
    /// Used for contract-governed USD cost budgets. Example: { "openai:gpt-4o": 0.005 }
    /// </summary>
    public Dictionary<string, decimal> TokenCostRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Detailed token cost rates by "provider:model" or "provider" key, in USD per 1K tokens.
    /// Used when providers have asymmetric input/output pricing.
    /// </summary>
    public Dictionary<string, TokenCostRateConfig> TokenCostRateDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TokenCostRateConfig
{
    public decimal InputUsdPer1K { get; set; }
    public decimal OutputUsdPer1K { get; set; }
}

public sealed class LlmProviderConfig
{
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "gpt-4o";
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string[] FallbackModels { get; set; } = [];
    public int MaxTokens { get; set; } = 4096;
    public float Temperature { get; set; } = 0.7f;

    /// <summary>Per-call timeout in seconds for LLM requests. 0 = no timeout.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Number of retry attempts for transient LLM failures (429/5xx). 0 = no retries.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Consecutive failures before the circuit breaker opens.</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Seconds the circuit breaker stays open before probing.</summary>
    public int CircuitBreakerCooldownSeconds { get; set; } = 30;

    public PromptCachingConfig PromptCaching { get; set; } = new();
}

public sealed class PromptCachingConfig
{
    public bool? Enabled { get; set; }
    public string? Retention { get; set; } // none | short | long | auto
    public string? Dialect { get; set; } // auto | openai | anthropic | gemini | none
    public bool? KeepWarmEnabled { get; set; }
    public int KeepWarmIntervalMinutes { get; set; } = 55;
    public bool? TraceEnabled { get; set; }
    public string? TraceFilePath { get; set; }
}

public sealed class DiagnosticsConfig
{
    public PromptCacheTraceConfig CacheTrace { get; set; } = new();
}

public sealed class PromptCacheTraceConfig
{
    public bool Enabled { get; set; }
    public string? FilePath { get; set; }
    public bool IncludeMessages { get; set; } = true;
    public bool IncludePrompt { get; set; } = true;
    public bool IncludeSystem { get; set; } = true;
}

public sealed class MemoryConfig
{
    /// <summary>Memory backend provider: "file" (default), "sqlite", or "mempalace".</summary>
    public string Provider { get; set; } = "file";

    public string StoragePath { get; set; } = "./memory";
    public int MaxHistoryTurns { get; set; } = 50;
    public int? MaxCachedSessions { get; set; }

    public MemorySqliteConfig Sqlite { get; set; } = new();
    public MemoryMempalaceConfig Mempalace { get; set; } = new();
    public MemoryRecallConfig Recall { get; set; } = new();
    public MemoryRetentionConfig Retention { get; set; } = new();

    /// <summary>When true, old history turns are summarized by the LLM instead of dropped.</summary>
    public bool EnableCompaction { get; set; } = false;

    /// <summary>Number of history turns that triggers compaction (must exceed MaxHistoryTurns).</summary>
    public int CompactionThreshold { get; set; } = 80;

    /// <summary>Number of recent turns to keep verbatim during compaction.</summary>
    public int CompactionKeepRecent { get; set; } = 10;

    /// <summary>Identifier for project-level memory scoping. Defaults to OPENCLAW_PROJECT env var.</summary>
    public string? ProjectId { get; set; }
}

public sealed class MemoryRetentionConfig
{
    public bool Enabled { get; set; } = false;
    public bool RunOnStartup { get; set; } = true;
    public int SweepIntervalMinutes { get; set; } = 30;
    public int SessionTtlDays { get; set; } = 30;
    public int BranchTtlDays { get; set; } = 14;
    public bool ArchiveEnabled { get; set; } = true;
    public string ArchivePath { get; set; } = "./memory/archive";
    public int ArchiveRetentionDays { get; set; } = 30;
    public int MaxItemsPerSweep { get; set; } = 1000;
}

public sealed class MemorySqliteConfig
{
    public string DbPath { get; set; } = "./memory/openclaw.db";
    public bool EnableFts { get; set; } = true;
    public bool EnableVectors { get; set; } = false;

    /// <summary>Embedding model name (e.g. "text-embedding-3-small"). Null disables vector embeddings.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Embedding vector dimensions. Defaults to 1536 (OpenAI text-embedding-3-small).</summary>
    public int EmbeddingDimensions { get; set; } = 1536;
}

public sealed class MemoryMempalaceConfig
{
    public string BasePath { get; set; } = "./memory/mempalace";
    public string PalaceId { get; set; } = "openclaw";
    public string? Namespace { get; set; }
    public string CollectionName { get; set; } = "memories";
    public int EmbeddingDimensions { get; set; } = 384;
    public string EmbedderIdentifier { get; set; } = "openclaw:mempalace:hash-v1";
    public string DefaultWing { get; set; } = "openclaw";
    public string DefaultRoom { get; set; } = "notes";
    public string SessionDbPath { get; set; } = "./memory/mempalace/openclaw-sessions.db";
    public string KnowledgeGraphDbPath { get; set; } = "./memory/mempalace/kg.db";
    public int MaxSearchCandidates { get; set; } = 200;
}

/// <summary>
/// Built-in token cost rates (USD per 1K tokens) for common provider:model combinations.
/// Used as fallback when the operator has not configured TokenCostRates.
/// </summary>
public static class DefaultTokenCostRates
{
    public static IReadOnlyDictionary<string, decimal> Rates { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai:gpt-4o"] = 0.005m,
            ["openai:gpt-4o-mini"] = 0.0003m,
            ["openai:gpt-4.1"] = 0.004m,
            ["openai:gpt-4.1-mini"] = 0.0008m,
            ["openai:gpt-4.1-nano"] = 0.0002m,
            ["anthropic:claude-sonnet-4-5"] = 0.006m,
            ["anthropic:claude-haiku-3-5"] = 0.002m,
            ["ollama"] = 0.0m,
        };

    public static IReadOnlyDictionary<string, TokenCostRateConfig> DetailedRates { get; } =
        new Dictionary<string, TokenCostRateConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai:gpt-4o"] = new() { InputUsdPer1K = 0.0025m, OutputUsdPer1K = 0.010m },
            ["openai:gpt-4o-mini"] = new() { InputUsdPer1K = 0.00015m, OutputUsdPer1K = 0.0006m },
            ["openai:gpt-4.1"] = new() { InputUsdPer1K = 0.002m, OutputUsdPer1K = 0.008m },
            ["openai:gpt-4.1-mini"] = new() { InputUsdPer1K = 0.0004m, OutputUsdPer1K = 0.0016m },
            ["openai:gpt-4.1-nano"] = new() { InputUsdPer1K = 0.0001m, OutputUsdPer1K = 0.0004m },
            ["anthropic:claude-sonnet-4-5"] = new() { InputUsdPer1K = 0.003m, OutputUsdPer1K = 0.015m },
            ["anthropic:claude-haiku-3-5"] = new() { InputUsdPer1K = 0.0008m, OutputUsdPer1K = 0.004m },
            ["ollama"] = new() { InputUsdPer1K = 0.0m, OutputUsdPer1K = 0.0m }
        };
}

public static class TokenCostRateResolver
{
    public static TokenCostRateConfig Resolve(GatewayConfig config, string providerId, string modelId)
    {
        var key = $"{providerId}:{modelId}";

        if (config.TokenCostRateDetails.TryGetValue(key, out var modelDetailedRate))
            return modelDetailedRate;
        if (config.TokenCostRateDetails.TryGetValue(providerId, out var providerDetailedRate))
            return providerDetailedRate;
        if (config.TokenCostRates.TryGetValue(key, out var modelRate))
            return new TokenCostRateConfig { InputUsdPer1K = modelRate, OutputUsdPer1K = modelRate };
        if (config.TokenCostRates.TryGetValue(providerId, out var providerRate))
            return new TokenCostRateConfig { InputUsdPer1K = providerRate, OutputUsdPer1K = providerRate };
        if (DefaultTokenCostRates.DetailedRates.TryGetValue(key, out var defaultModelDetailedRate))
            return defaultModelDetailedRate;
        if (DefaultTokenCostRates.DetailedRates.TryGetValue(providerId, out var defaultProviderDetailedRate))
            return defaultProviderDetailedRate;
        if (DefaultTokenCostRates.Rates.TryGetValue(key, out var defaultModelRate))
            return new TokenCostRateConfig { InputUsdPer1K = defaultModelRate, OutputUsdPer1K = defaultModelRate };
        if (DefaultTokenCostRates.Rates.TryGetValue(providerId, out var defaultProviderRate))
            return new TokenCostRateConfig { InputUsdPer1K = defaultProviderRate, OutputUsdPer1K = defaultProviderRate };

        return new TokenCostRateConfig();
    }
}

public sealed class MemoryRecallConfig
{
    public bool Enabled { get; set; } = false;
    public int MaxNotes { get; set; } = 8;
    public int MaxChars { get; set; } = 8000;
}

public sealed class SecurityConfig
{
    /// <summary>
    /// Applies a fail-closed public-bind preset before startup validation. This forces safer approval and
    /// tooling defaults for Internet-facing deployments without introducing a separate policy system.
    /// </summary>
    public bool StrictPublicBindProfile { get; set; } = false;

    public bool AllowQueryStringToken { get; set; } = false;
    public string[] AllowedOrigins { get; set; } = [];
    public bool TrustForwardedHeaders { get; set; } = false;
    public string[] KnownProxies { get; set; } = [];
    public bool RequireRequesterMatchForHttpToolApproval { get; set; } = false;

    /// <summary>
    /// When binding to a non-loopback address, the gateway refuses to start if the local tooling
    /// is configured in an unsafe way (e.g. shell enabled or wildcard roots). Set this to true
    /// only if you fully trust your network perimeter and token distribution.
    /// </summary>
    public bool AllowUnsafeToolingOnPublicBind { get; set; } = false;

    /// <summary>
    /// When binding to a non-loopback address, the gateway refuses to start if the TypeScript/JS
    /// plugin bridge is enabled. Set true to allow running third-party plugins while Internet-facing.
    /// </summary>
    public bool AllowPluginBridgeOnPublicBind { get; set; } = false;

    /// <summary>
    /// When binding to a non-loopback address, disallow raw: secret refs by default to reduce the
    /// chance of committing secrets to config files.
    /// </summary>
    public bool AllowRawSecretRefsOnPublicBind { get; set; } = false;

    /// <summary>Idle timeout (minutes) for browser admin sessions. Default 60 minutes.</summary>
    public int BrowserSessionIdleMinutes { get; set; } = 60;

    /// <summary>Lifetime (days) for persistent browser admin sessions created with "Remember me". Default 30 days.</summary>
    public int BrowserRememberDays { get; set; } = 30;
}

public sealed class UrlSafetyConfig
{
    /// <summary>Enable URL validation for tools that initiate outbound navigation or fetches.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Block loopback, private, link-local, metadata, and other non-public address ranges.</summary>
    public bool BlockPrivateNetworkTargets { get; set; } = true;

    /// <summary>Additional host globs to block. Examples: ["*.internal", "metadata.google.internal"].</summary>
    public string[] BlockedHostGlobs { get; set; } = [];

    /// <summary>Additional CIDR ranges to block. Examples: ["203.0.113.0/24", "2001:db8::/32"].</summary>
    public string[] BlockedCidrs { get; set; } = [];
}

public sealed class WebSocketConfig
{
    public int MaxMessageBytes { get; set; } = 256 * 1024;
    public int MaxConnections { get; set; } = 1_000;
    public int MaxConnectionsPerIp { get; set; } = 50;
    public int MessagesPerMinutePerConnection { get; set; } = 120;
    public int ReceiveTimeoutSeconds { get; set; } = 120;
}

public sealed class CanvasConfig
{
    /// <summary>Enables first-party Canvas/A2UI command forwarding on websocket clients.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Canvas is disabled on non-loopback binds unless this is explicitly enabled.</summary>
    public bool AllowOnPublicBind { get; set; } = false;

    public int MaxCommandBytes { get; set; } = 256 * 1024;
    public int MaxSnapshotBytes { get; set; } = 256 * 1024;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int MaxFramesPerPush { get; set; } = 100;
    public bool EnableLocalHtml { get; set; } = true;
    public bool EnableRemoteNavigation { get; set; } = false;
    public bool EnableEval { get; set; } = true;
}

public sealed class ToolingConfig
{
    /// <summary>Autonomy mode: "readonly", "supervised", or "full".</summary>
    public string AutonomyMode { get; set; } = "supervised";

    /// <summary>Workspace root used when WorkspaceOnly=true. Supports env:OPENCLAW_WORKSPACE.</summary>
    public string? WorkspaceRoot { get; set; } = "env:OPENCLAW_WORKSPACE";

    /// <summary>When true, file paths must be within WorkspaceRoot.</summary>
    public bool WorkspaceOnly { get; set; } = false;

    /// <summary>Shell commands are allowed only if they match one of these globs. ["*"] allows all.</summary>
    public string[] AllowedShellCommandGlobs { get; set; } = ["*"];

    /// <summary>Forbidden path globs (deny wins). Applied to file-based tools and as a heuristic for shell.</summary>
    public string[] ForbiddenPathGlobs { get; set; } = [];

    public bool AllowShell { get; set; } = true;
    public bool ReadOnlyMode { get; set; } = false;
    public string[] AllowedReadRoots { get; set; } = ["*"];
    public string[] AllowedWriteRoots { get; set; } = ["*"];

    /// <summary>Per-tool execution timeout in seconds. 0 = no timeout.</summary>
    public int ToolTimeoutSeconds { get; set; } = 30;

    /// <summary>Execute independent tool calls in parallel when the LLM requests multiple tools.</summary>
    public bool ParallelToolExecution { get; set; } = true;

    /// <summary>When true, tools in ApprovalRequiredTools need explicit user approval before executing.</summary>
    public bool RequireToolApproval { get; set; } = false;

    /// <summary>Tool names that require user approval when RequireToolApproval is true.</summary>
    public string[] ApprovalRequiredTools { get; set; } = ["shell", "write_file"];

    /// <summary>Seconds to wait for a tool approval decision before denying. Default: 300 (5 minutes).</summary>
    public int ToolApprovalTimeoutSeconds { get; set; } = 300;

    public bool EnableBrowserTool { get; set; } = true;
    public bool AllowBrowserEvaluate { get; set; } = true;
    public bool BrowserHeadless { get; set; } = true;
    public int BrowserTimeoutSeconds { get; set; } = 30;
    public UrlSafetyConfig UrlSafety { get; set; } = new();
    public Dictionary<string, ToolsetConfig> Toolsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ToolPresetConfig> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SurfaceBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChannelsConfig
{
    /// <summary>Allowlist semantics: "legacy" (backward-compatible) or "strict" ([]=deny, ["*"]=allow-all).</summary>
    public string AllowlistSemantics { get; set; } = "legacy";
    public SmsChannelConfig Sms { get; set; } = new();
    public TelegramChannelConfig Telegram { get; set; } = new();
    public WhatsAppChannelConfig WhatsApp { get; set; } = new();
    public TeamsChannelConfig Teams { get; set; } = new();
    public SlackChannelConfig Slack { get; set; } = new();
    public DiscordChannelConfig Discord { get; set; } = new();
    public SignalChannelConfig Signal { get; set; } = new();
}

public sealed class WhatsAppChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string Type { get; set; } = "official"; // "official", "bridge", or "first_party_worker"
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string WebhookPath { get; set; } = "/whatsapp/inbound";
    public string? WebhookPublicBaseUrl { get; set; }
    public string WebhookVerifyToken { get; set; } = "openclaw-verify";
    public string WebhookVerifyTokenRef { get; set; } = "env:WHATSAPP_VERIFY_TOKEN";

    /// <summary>
    /// When true, validates X-Hub-Signature-256 for official Cloud API webhooks.
    /// Recommended for all non-loopback/public binds.
    /// </summary>
    public bool ValidateSignature { get; set; } = false;

    /// <summary>Meta app secret used to validate official webhook signatures (direct value).</summary>
    public string? WebhookAppSecret { get; set; }

    /// <summary>Meta app secret reference (env: or raw:) used when WebhookAppSecret is null.</summary>
    public string WebhookAppSecretRef { get; set; } = "env:WHATSAPP_APP_SECRET";
    
    // Official Cloud API settings
    public string? CloudApiToken { get; set; }
    public string CloudApiTokenRef { get; set; } = "env:WHATSAPP_CLOUD_API_TOKEN";
    public string? PhoneNumberId { get; set; }
    public string? BusinessAccountId { get; set; }

    // Bridge settings (e.g. for whatsmeow bridge)
    public string? BridgeUrl { get; set; }
    public string? BridgeToken { get; set; }
    public string BridgeTokenRef { get; set; } = "env:WHATSAPP_BRIDGE_TOKEN";
    public bool BridgeSuppressSendExceptions { get; set; } = false;

    // First-party worker settings
    public WhatsAppFirstPartyWorkerConfig FirstPartyWorker { get; set; } = new();

    public int MaxInboundChars { get; set; } = 4096;

    /// <summary>Max inbound webhook request size in bytes.</summary>
    public int MaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>Optional allowlist for inbound senders (wa_id / from). Interpreted using Channels.AllowlistSemantics.</summary>
    public string[] AllowedFromIds { get; set; } = [];
}

public sealed class WhatsAppFirstPartyWorkerConfig
{
    /// <summary>
    /// Worker transport engine. "baileys" and "whatsmeow" launch the bundled external workers;
    /// "simulated" is available for tests and dry-run validation.
    /// </summary>
    public string Driver { get; set; } = "baileys";

    /// <summary>
    /// Optional explicit path to the worker executable or DLL. When empty, the gateway tries
    /// colocated deployment paths before failing.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Optional explicit working directory for the worker child process.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Root path used by the worker for session, media, and cache files.</summary>
    public string StoragePath { get; set; } = "./memory/whatsapp-worker";

    public string? MediaCachePath { get; set; }
    public bool HistorySync { get; set; } = true;
    public string? Proxy { get; set; }
    public List<WhatsAppWorkerAccountConfig> Accounts { get; set; } = [];
}

public sealed class WhatsAppWorkerAccountConfig
{
    public string AccountId { get; set; } = "default";
    public string SessionPath { get; set; } = "./session/default";
    public string DeviceName { get; set; } = "OpenClaw";
    public string PairingMode { get; set; } = "qr"; // "qr" or "pairing_code"
    public string? PhoneNumber { get; set; }
    public bool SendReadReceipts { get; set; } = true;
    public bool AckReaction { get; set; } = false;
    public string? MediaCachePath { get; set; }
    public bool HistorySync { get; set; } = true;
    public string? Proxy { get; set; }
}

public sealed class TeamsChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string GroupPolicy { get; set; } = "allowlist"; // open, allowlist, disabled

    /// <summary>Azure Bot App ID.</summary>
    public string? AppId { get; set; }
    public string AppIdRef { get; set; } = "env:TEAMS_APP_ID";

    /// <summary>Azure Bot Client Secret.</summary>
    public string? AppPassword { get; set; }
    public string AppPasswordRef { get; set; } = "env:TEAMS_APP_PASSWORD";

    /// <summary>Azure AD Tenant ID (single-tenant).</summary>
    public string? TenantId { get; set; }
    public string TenantIdRef { get; set; } = "env:TEAMS_TENANT_ID";

    /// <summary>Webhook path for inbound Bot Framework activities.</summary>
    public string WebhookPath { get; set; } = "/api/messages";

    /// <summary>Validate the Azure Bot Framework JWT token on inbound requests.</summary>
    public bool ValidateToken { get; set; } = true;

    /// <summary>Require @mention of the bot in team channels and group chats.</summary>
    public bool RequireMention { get; set; } = true;

    /// <summary>Reply style: "thread" posts as reply, "top-level" posts new message.</summary>
    public string ReplyStyle { get; set; } = "thread";

    /// <summary>Maximum text length per outbound message before chunking.</summary>
    public int TextChunkLimit { get; set; } = 4000;

    /// <summary>Chunking mode: "length" splits at character limit, "newline" splits at newline boundaries.</summary>
    public string ChunkMode { get; set; } = "length";

    public int MaxInboundChars { get; set; } = 4096;
    public int MaxRequestBytes { get; set; } = 256 * 1024;

    /// <summary>Allowed Azure AD tenant IDs (empty = all tenants).</summary>
    public string[] AllowedTenantIds { get; set; } = [];

    /// <summary>Allowed sender IDs (AAD object IDs or UPNs).</summary>
    public string[] AllowedFromIds { get; set; } = [];

    /// <summary>Allowed team IDs for group policy.</summary>
    public string[] AllowedTeamIds { get; set; } = [];

    /// <summary>Allowed conversation IDs for group policy.</summary>
    public string[] AllowedConversationIds { get; set; } = [];
}

public sealed class SmsChannelConfig
{
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public TwilioSmsConfig Twilio { get; set; } = new();
}

public sealed class TwilioSmsConfig
{
    public bool Enabled { get; set; } = false;
    public string? AccountSid { get; set; }
    public string? AuthTokenRef { get; set; }
    public string? MessagingServiceSid { get; set; }
    public string? FromNumber { get; set; }
    public string WebhookPath { get; set; } = "/twilio/sms/inbound";
    public string? WebhookPublicBaseUrl { get; set; }
    public bool ValidateSignature { get; set; } = true;
    public string[] AllowedFromNumbers { get; set; } = [];
    public string[] AllowedToNumbers { get; set; } = [];
    public int MaxInboundChars { get; set; } = 2000;
    public int MaxRequestBytes { get; set; } = 64 * 1024;
    public int RateLimitPerFromPerMinute { get; set; } = 30;
    public bool AutoReplyForBlocked { get; set; } = false;
    public string HelpText { get; set; } = "OpenClaw: reply STOP to opt out.";
}

public sealed class TelegramChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string? BotToken { get; set; }
    public string BotTokenRef { get; set; } = "env:TELEGRAM_BOT_TOKEN";
    public string WebhookPath { get; set; } = "/telegram/inbound";
    public string? WebhookPublicBaseUrl { get; set; }
    public string[] AllowedFromUserIds { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public int MaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>When true, validates the X-Telegram-Bot-Api-Secret-Token header on inbound webhooks.</summary>
    public bool ValidateSignature { get; set; } = false;

    /// <summary>Secret token set via Telegram's setWebhook API (direct value).</summary>
    public string? WebhookSecretToken { get; set; }

    /// <summary>Secret token reference (env: or raw:). Used when WebhookSecretToken is null.</summary>
    public string WebhookSecretTokenRef { get; set; } = "env:TELEGRAM_WEBHOOK_SECRET";
}

public sealed class SlackChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string? BotToken { get; set; }
    public string BotTokenRef { get; set; } = "env:SLACK_BOT_TOKEN";
    public string? SigningSecret { get; set; }
    public string SigningSecretRef { get; set; } = "env:SLACK_SIGNING_SECRET";
    public string WebhookPath { get; set; } = "/slack/events";
    public string SlashCommandPath { get; set; } = "/slack/commands";
    public string[] AllowedWorkspaceIds { get; set; } = [];
    public string[] AllowedFromUserIds { get; set; } = [];
    public string[] AllowedChannelIds { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public int MaxRequestBytes { get; set; } = 64 * 1024;
    public bool ValidateSignature { get; set; } = true;
}

public sealed class DiscordChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string? BotToken { get; set; }
    public string BotTokenRef { get; set; } = "env:DISCORD_BOT_TOKEN";
    public string? ApplicationId { get; set; }
    public string ApplicationIdRef { get; set; } = "env:DISCORD_APPLICATION_ID";
    public string? PublicKey { get; set; }
    public string PublicKeyRef { get; set; } = "env:DISCORD_PUBLIC_KEY";
    public string WebhookPath { get; set; } = "/discord/interactions";
    public string[] AllowedGuildIds { get; set; } = [];
    public string[] AllowedFromUserIds { get; set; } = [];
    public string[] AllowedChannelIds { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public int MaxRequestBytes { get; set; } = 64 * 1024;
    public bool ValidateSignature { get; set; } = true;
    public bool RegisterSlashCommands { get; set; } = true;
    public string SlashCommandPrefix { get; set; } = "claw";
}

public sealed class SignalChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string Driver { get; set; } = "signald"; // "signald" or "signal_cli"
    public string SocketPath { get; set; } = "/var/run/signald/signald.sock";
    public string? SignalCliPath { get; set; }
    public string? AccountPhoneNumber { get; set; }
    public string AccountPhoneNumberRef { get; set; } = "env:SIGNAL_PHONE_NUMBER";
    public string[] AllowedFromNumbers { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public bool NoContentLogging { get; set; } = false;
    public bool TrustAllKeys { get; set; } = true;
}

public sealed class CronConfig
{
    public bool Enabled { get; set; } = false;
    public List<CronJobConfig> Jobs { get; set; } = [];
}

public sealed class CronJobConfig
{
    public string Name { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool RunOnStartup { get; set; } = false;
    public string? SessionId { get; set; }
    public string? ChannelId { get; set; }
    public string? RecipientId { get; set; }
    public string? Subject { get; set; }
    public string? AutomationId { get; set; }
    public string? AutomationTriggerSource { get; set; }

    /// <summary>IANA timezone ID (e.g. "America/New_York"). Null defaults to UTC.</summary>
    public string? Timezone { get; set; }
}

public sealed class WebhooksConfig
{
    public bool Enabled { get; set; } = false;
    public Dictionary<string, WebhookEndpointConfig> Endpoints { get; set; } = [];
}

public sealed class WebhookEndpointConfig
{
    public string? Secret { get; set; }
    public bool ValidateHmac { get; set; } = false;
    public string HmacHeader { get; set; } = "X-Hub-Signature-256";
    public string? SessionId { get; set; }
    public string PromptTemplate { get; set; } = "Webhook received:\n\n{body}";
    public int MaxRequestBytes { get; set; } = 128 * 1024;

    /// <summary>Maximum webhook body length in characters before truncation. Limits prompt injection surface.</summary>
    public int MaxBodyLength { get; set; } = 10_240;
}

// ── Multi-Agent Routing ─────────────────────────────────────────

public sealed class RoutingConfig
{
    public bool Enabled { get; set; } = false;
    public Dictionary<string, AgentRouteConfig> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentRouteConfig
{
    public string? ChannelId { get; set; }
    public string? SenderId { get; set; }
    public string? SystemPrompt { get; set; }
    public string? ModelOverride { get; set; }
    public string? ModelProfileId { get; set; }
    public string[] PreferredModelTags { get; set; } = [];
    public string[] FallbackModelProfileIds { get; set; } = [];
    public ModelSelectionRequirements ModelRequirements { get; set; } = new();
    public string? PresetId { get; set; }
    public string[] AllowedTools { get; set; } = [];
}

// ── Tailscale ───────────────────────────────────────────────────

public sealed class TailscaleConfig
{
    public bool Enabled { get; set; } = false;
    public string Mode { get; set; } = "off"; // "off", "serve", "funnel"
    public int Port { get; set; } = 443;
    public string? Hostname { get; set; }
}

// ── Gmail Pub/Sub ───────────────────────────────────────────────

public sealed class GmailPubSubConfig
{
    public bool Enabled { get; set; } = false;
    public string? CredentialsPath { get; set; }
    public string CredentialsPathRef { get; set; } = "env:GOOGLE_APPLICATION_CREDENTIALS";
    public string? TopicName { get; set; }
    public string? SubscriptionName { get; set; }
    public string WebhookPath { get; set; } = "/gmail/push";
    public string? SessionId { get; set; }
    public string Prompt { get; set; } = "A new email notification was received. Check inbox and triage.";

    /// <summary>Shared secret token for authenticating push requests. Set as a query param or header.</summary>
    public string? WebhookSecret { get; set; }
    public string WebhookSecretRef { get; set; } = "env:GMAIL_PUBSUB_SECRET";
}

// ── mDNS/Bonjour Discovery ─────────────────────────────────────

public sealed class MdnsConfig
{
    public bool Enabled { get; set; } = false;
    public string ServiceType { get; set; } = "_openclaw._tcp";
    public string? InstanceName { get; set; }
    public int Port { get; set; } = 0; // 0 = use gateway port
}
