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
    public LlmProviderConfig Llm { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public WebSocketConfig WebSocket { get; set; } = new();
    public ToolingConfig Tooling { get; set; } = new();
    public ChannelsConfig Channels { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();
    public SkillsConfig Skills { get; set; } = new();
    public DelegationConfig Delegation { get; set; } = new();
    public CronConfig Cron { get; set; } = new();
    public WebhooksConfig Webhooks { get; set; } = new();
    public string UsageFooter { get; set; } = "off"; // "off", "tokens", "full"

    public int MaxConcurrentSessions { get; set; } = 64;
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>Max total tokens (input + output) per session. 0 = unlimited.</summary>
    public long SessionTokenBudget { get; set; } = 0;

    /// <summary>Max messages per minute per session at the agent level. 0 = unlimited.</summary>
    public int SessionRateLimitPerMinute { get; set; } = 0;

    /// <summary>Seconds to wait for in-flight requests to complete during shutdown. 0 = no drain.</summary>
    public int GracefulShutdownSeconds { get; set; } = 15;
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
}

public sealed class MemoryConfig
{
    public string StoragePath { get; set; } = "./memory";
    public int MaxHistoryTurns { get; set; } = 50;
    public int? MaxCachedSessions { get; set; }

    /// <summary>When true, old history turns are summarized by the LLM instead of dropped.</summary>
    public bool EnableCompaction { get; set; } = false;

    /// <summary>Number of history turns that triggers compaction (must exceed MaxHistoryTurns).</summary>
    public int CompactionThreshold { get; set; } = 40;

    /// <summary>Number of recent turns to keep verbatim during compaction.</summary>
    public int CompactionKeepRecent { get; set; } = 10;

    /// <summary>Identifier for project-level memory scoping. Defaults to OPENCLAW_PROJECT env var.</summary>
    public string? ProjectId { get; set; }
}

public sealed class SecurityConfig
{
    public bool AllowQueryStringToken { get; set; } = false;
    public string[] AllowedOrigins { get; set; } = [];
    public bool TrustForwardedHeaders { get; set; } = false;
    public string[] KnownProxies { get; set; } = [];

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
}

public sealed class WebSocketConfig
{
    public int MaxMessageBytes { get; set; } = 65_536;
    public int MaxConnections { get; set; } = 1_000;
    public int MaxConnectionsPerIp { get; set; } = 50;
    public int MessagesPerMinutePerConnection { get; set; } = 120;
}

public sealed class ToolingConfig
{
    public bool AllowShell { get; set; } = true;
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

    public bool EnableBrowserTool { get; set; } = true;
    public bool BrowserHeadless { get; set; } = true;
    public int BrowserTimeoutSeconds { get; set; } = 30;
}

public sealed class ChannelsConfig
{
    public SmsChannelConfig Sms { get; set; } = new();
    public TelegramChannelConfig Telegram { get; set; } = new();
    public WhatsAppChannelConfig WhatsApp { get; set; } = new();
}

public sealed class WhatsAppChannelConfig
{
    public bool Enabled { get; set; } = false;
    public string Type { get; set; } = "official"; // "official" or "bridge"
    public string DmPolicy { get; set; } = "pairing"; // open, pairing, closed
    public string WebhookPath { get; set; } = "/whatsapp/inbound";
    public string? WebhookPublicBaseUrl { get; set; }
    public string WebhookVerifyToken { get; set; } = "openclaw-verify";
    public string WebhookVerifyTokenRef { get; set; } = "env:WHATSAPP_VERIFY_TOKEN";
    
    // Official Cloud API settings
    public string? CloudApiToken { get; set; }
    public string CloudApiTokenRef { get; set; } = "env:WHATSAPP_CLOUD_API_TOKEN";
    public string? PhoneNumberId { get; set; }
    public string? BusinessAccountId { get; set; }

    // Bridge settings (e.g. for whatsmeow bridge)
    public string? BridgeUrl { get; set; }
    public string? BridgeToken { get; set; }
    public string BridgeTokenRef { get; set; } = "env:WHATSAPP_BRIDGE_TOKEN";

    public int MaxInboundChars { get; set; } = 4096;
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
    public string? SessionId { get; set; }
    public string? ChannelId { get; set; }
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
}
