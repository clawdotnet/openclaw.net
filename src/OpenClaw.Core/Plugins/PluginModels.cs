using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Plugins;

/// <summary>
/// Represents an openclaw.plugin.json manifest file.
/// Compatible with the OpenClaw TypeScript plugin ecosystem spec.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Canonical plugin id (e.g. "voice-call").</summary>
    public required string Id { get; init; }

    /// <summary>Display name for the plugin.</summary>
    public string? Name { get; init; }

    /// <summary>Short plugin summary.</summary>
    public string? Description { get; init; }

    /// <summary>Plugin version (informational).</summary>
    public string? Version { get; init; }

    /// <summary>Plugin kind for exclusive slot categories (e.g. "memory").</summary>
    public string? Kind { get; init; }

    /// <summary>Channel ids registered by this plugin.</summary>
    public string[] Channels { get; init; } = [];

    /// <summary>Provider ids registered by this plugin.</summary>
    public string[] Providers { get; init; } = [];

    /// <summary>Skill directories to load (relative to plugin root).</summary>
    public string[] Skills { get; init; } = [];

    /// <summary>JSON Schema for plugin config validation.</summary>
    public JsonElement? ConfigSchema { get; init; }

    /// <summary>UI hints for config rendering.</summary>
    public JsonElement? UiHints { get; init; }
}

/// <summary>
/// A discovered plugin on disk — manifest + filesystem location.
/// </summary>
public sealed class DiscoveredPlugin
{
    public required PluginManifest Manifest { get; init; }

    /// <summary>Absolute path to the plugin root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Absolute path to the plugin entry file (TypeScript/JavaScript).</summary>
    public required string EntryPath { get; init; }
}

/// <summary>
/// Per-plugin configuration from the gateway config.
/// </summary>
public sealed class PluginEntryConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Plugin-specific config (opaque JSON, validated against plugin configSchema).</summary>
    public JsonElement? Config { get; set; }
}

/// <summary>
/// Top-level plugin system configuration.
/// </summary>
public sealed class PluginsConfig
{
    /// <summary>Master toggle for the plugin system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When both a native replica and a bridge plugin provide the same tool name,
    /// this decides the winner. "native" = prefer native, "bridge" = prefer bridge.
    /// </summary>
    public string Prefer { get; set; } = "native";

    /// <summary>
    /// Per-tool overrides: tool-name → "native" | "bridge".
    /// Takes precedence over <see cref="Prefer"/>.
    /// </summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Plugin id allowlist (optional — empty means all allowed).</summary>
    public string[] Allow { get; set; } = [];

    /// <summary>Plugin id denylist (deny wins over allow).</summary>
    public string[] Deny { get; set; } = [];

    /// <summary>Extra plugin files/directories to scan.</summary>
    public PluginLoadConfig Load { get; set; } = new();

    /// <summary>Per-plugin toggles and config.</summary>
    public Dictionary<string, PluginEntryConfig> Entries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Exclusive slot assignments (e.g. memory → "memory-core").</summary>
    public Dictionary<string, string> Slots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Transport configuration for the plugin bridge.</summary>
    public BridgeTransportConfig Transport { get; set; } = new();

    /// <summary>Configuration for native plugin replicas.</summary>
    public NativePluginsConfig Native { get; set; } = new();

    /// <summary>Configuration for MCP servers exposed as native tools.</summary>
    public McpPluginsConfig Mcp { get; set; } = new();

    /// <summary>Configuration for in-process dynamic .NET plugins. JIT mode only.</summary>
    public NativeDynamicPluginsConfig DynamicNative { get; set; } = new();
}

public sealed class McpPluginsConfig
{
    public bool Enabled { get; set; } = false;
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class McpServerConfig
{
    public bool Enabled { get; set; } = true;
    public string? Name { get; set; }
    public string? Transport { get; set; }
    public string? Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ToolNamePrefix { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 15;
    public int RequestTimeoutSeconds { get; set; } = 60;
}

public static class McpServerConfigExtensions
{
    public static string NormalizeTransport(this McpServerConfig config)
    {
        var transport = config.Transport?.Trim();
        if (string.IsNullOrWhiteSpace(transport))
            return string.IsNullOrWhiteSpace(config.Url) ? "stdio" : "http";

        if (transport.Equals("streamable-http", StringComparison.OrdinalIgnoreCase) ||
            transport.Equals("streamable_http", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        return transport.ToLowerInvariant();
    }
}

/// <summary>
/// Configuration for native (C#) replicas of popular OpenClaw plugins.
/// Each property matches the canonical plugin id.
/// </summary>
public sealed class NativePluginsConfig
{
    public WebSearchConfig WebSearch { get; set; } = new();
    public WebFetchConfig WebFetch { get; set; } = new();
    public GitToolsConfig GitTools { get; set; } = new();
    public CodeExecConfig CodeExec { get; set; } = new();
    public ImageGenConfig ImageGen { get; set; } = new();
    public PdfReadConfig PdfRead { get; set; } = new();
    public CalendarConfig Calendar { get; set; } = new();
    public EmailConfig Email { get; set; } = new();
    public DatabaseConfig Database { get; set; } = new();
    public InboxZeroConfig InboxZero { get; set; } = new();
    public HomeAssistantConfig HomeAssistant { get; set; } = new();
    public MqttConfig Mqtt { get; set; } = new();
}

public sealed class HomeAssistantConfig
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";
    public string TokenRef { get; set; } = "env:HOME_ASSISTANT_TOKEN";
    public int TimeoutSeconds { get; set; } = 15;
    public bool VerifyTls { get; set; } = true;
    public int MaxOutputChars { get; set; } = 60_000;
    public int MaxEntities { get; set; } = 200;

    public HomeAssistantPolicyConfig Policy { get; set; } = new();
    public HomeAssistantEventsConfig Events { get; set; } = new();
}

public sealed class HomeAssistantPolicyConfig
{
    public string[] AllowEntityIdGlobs { get; set; } = ["*"];
    public string[] DenyEntityIdGlobs { get; set; } = [];
    public string[] AllowServiceGlobs { get; set; } = ["*"];
    public string[] DenyServiceGlobs { get; set; } = [];
}

public sealed class HomeAssistantEventsConfig
{
    public bool Enabled { get; set; } = false;
    public string ChannelId { get; set; } = "homeassistant";
    public string SessionId { get; set; } = "homeassistant:events";
    public string[] SubscribeEventTypes { get; set; } = ["state_changed"];
    public bool EmitAllMatchingEvents { get; set; } = true;
    public int GlobalCooldownSeconds { get; set; } = 2;
    public string[] AllowEntityIdGlobs { get; set; } = ["*"];
    public string[] DenyEntityIdGlobs { get; set; } = [];
    public string PromptTemplate { get; set; } =
        "Home Assistant event: {event_type} entity={entity_id} from={from_state} to={to_state} (name={friendly_name})";
    public List<HomeAssistantEventRule> Rules { get; set; } = [];
}

public sealed class HomeAssistantEventRule
{
    public string Name { get; set; } = "";
    public string[] EntityIdGlobs { get; set; } = ["*"];
    public string? FromState { get; set; }
    public string? ToState { get; set; }

    /// <summary>
    /// Local-time window in HH:mm format, e.g. "22:00".
    /// When both set, the rule only matches within this window.
    /// Supports overnight windows (e.g. 22:00–06:00).
    /// </summary>
    public string? BetweenLocalStart { get; set; }
    public string? BetweenLocalEnd { get; set; }

    /// <summary>
    /// Days of week allowed for this rule. Empty = all days.
    /// Values: "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun".
    /// </summary>
    public string[] DaysOfWeek { get; set; } = [];

    public string PromptTemplate { get; set; } = "";
    public int CooldownSeconds { get; set; } = 2;
}

public sealed class MqttConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; } = false;
    public string? UsernameRef { get; set; }
    public string? PasswordRef { get; set; }
    public string ClientId { get; set; } = "openclaw";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxPayloadBytes { get; set; } = 262_144;

    public MqttPolicyConfig Policy { get; set; } = new();
    public MqttEventsConfig Events { get; set; } = new();
}

public sealed class MqttPolicyConfig
{
    public string[] AllowPublishTopicGlobs { get; set; } = ["*"];
    public string[] DenyPublishTopicGlobs { get; set; } = [];
    public string[] AllowSubscribeTopicGlobs { get; set; } = ["*"];
    public string[] DenySubscribeTopicGlobs { get; set; } = [];
}

public sealed class MqttEventsConfig
{
    public bool Enabled { get; set; } = false;
    public string ChannelId { get; set; } = "mqtt";
    public string SessionId { get; set; } = "mqtt:events";
    public List<MqttSubscriptionConfig> Subscriptions { get; set; } = [];
}

public sealed class MqttSubscriptionConfig
{
    public string Topic { get; set; } = "";
    public int Qos { get; set; } = 0;
    public string PromptTemplate { get; set; } = "MQTT message on {topic}: {payload}";
    public int CooldownSeconds { get; set; } = 1;
}

public sealed class WebSearchConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Search provider: "tavily", "brave", or "searxng".</summary>
    public string Provider { get; set; } = "tavily";

    /// <summary>API key (or env: / raw: secret ref).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Base URL for SearXNG instance (only used when Provider = "searxng").</summary>
    public string? Endpoint { get; set; }

    /// <summary>Maximum results to return.</summary>
    public int MaxResults { get; set; } = 5;
}

public sealed class WebFetchConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Maximum response body size in KB.</summary>
    public int MaxSizeKb { get; set; } = 512;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>User-Agent header for outbound requests.</summary>
    public string UserAgent { get; set; } = "OpenClaw/1.0";
}

public sealed class GitToolsConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Whether destructive operations (push, reset --hard) are allowed.</summary>
    public bool AllowPush { get; set; } = false;

    /// <summary>Maximum diff output size in bytes.</summary>
    public int MaxDiffBytes { get; set; } = 64 * 1024;
}

public sealed class CodeExecConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Execution backend: "docker", "process".</summary>
    public string Backend { get; set; } = "process";

    /// <summary>Docker image used when Backend = "docker".</summary>
    public string DockerImage { get; set; } = "python:3.12-slim";

    /// <summary>Timeout per execution in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum output bytes captured.</summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;

    /// <summary>Allowed languages (empty = all supported).</summary>
    public string[] AllowedLanguages { get; set; } = ["python", "javascript", "bash"];
}

public sealed class ImageGenConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Provider: "openai" (DALL-E).</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>API key (or env: / raw: secret ref).</summary>
    public string? ApiKey { get; set; }

    /// <summary>API endpoint (optional, for compatible APIs).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name (e.g. "dall-e-3").</summary>
    public string Model { get; set; } = "dall-e-3";

    /// <summary>Default image size.</summary>
    public string Size { get; set; } = "1024x1024";

    /// <summary>Default quality ("standard" or "hd" for DALL-E 3).</summary>
    public string Quality { get; set; } = "standard";
}

public sealed class PdfReadConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Maximum pages to extract (0 = all).</summary>
    public int MaxPages { get; set; } = 50;

    /// <summary>Maximum output characters.</summary>
    public int MaxOutputChars { get; set; } = 100_000;
}

public sealed class CalendarConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Provider: "google".</summary>
    public string Provider { get; set; } = "google";

    /// <summary>Path to service account JSON key or OAuth credentials file.</summary>
    public string? CredentialsPath { get; set; }

    /// <summary>Calendar ID to operate on (default: primary).</summary>
    public string CalendarId { get; set; } = "primary";

    /// <summary>Maximum events to return in list operations.</summary>
    public int MaxEvents { get; set; } = 25;
}

public sealed class EmailConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Whether the email channel should poll IMAP and emit inbound messages.</summary>
    public bool InboundEnabled { get; set; } = false;

    /// <summary>SMTP server host for sending.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP server port.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Whether to use TLS for SMTP.</summary>
    public bool SmtpUseTls { get; set; } = true;

    /// <summary>IMAP server host for reading.</summary>
    public string? ImapHost { get; set; }

    /// <summary>IMAP server port.</summary>
    public int ImapPort { get; set; } = 993;

    /// <summary>Whether to use TLS for IMAP.</summary>
    public bool ImapUseTls { get; set; } = true;

    /// <summary>IMAP folder to poll for inbound messages.</summary>
    public string InboundFolder { get; set; } = "INBOX";

    /// <summary>Polling interval in seconds for inbound IMAP checks.</summary>
    public int InboundPollSeconds { get; set; } = 30;

    /// <summary>Maximum number of unseen messages to process per poll.</summary>
    public int InboundMaxMessagesPerPoll { get; set; } = 10;

    /// <summary>Whether inbound messages should be marked as read after successful handoff.</summary>
    public bool MarkInboundAsRead { get; set; } = true;

    /// <summary>Email account username.</summary>
    public string? Username { get; set; }

    /// <summary>Email account password (or env: / raw: secret ref).</summary>
    public string? PasswordRef { get; set; }

    /// <summary>From address for outgoing mail.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Maximum emails to return in list/search operations.</summary>
    public int MaxResults { get; set; } = 20;
}

public sealed class DatabaseConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Database provider: "sqlite", "postgres", "mysql".</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>Connection string (or env: / raw: secret ref).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Whether to allow write operations (INSERT, UPDATE, DELETE, CREATE, DROP).</summary>
    public bool AllowWrite { get; set; } = false;

    /// <summary>Query timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum rows to return.</summary>
    public int MaxRows { get; set; } = 1000;

    /// <summary>
    /// Allowed table names (schema-qualified optional). Empty = allow all tables.
    /// </summary>
    public string[] AllowedTables { get; set; } = [];

    /// <summary>
    /// Denied table names (schema-qualified optional). Deny wins over allow.
    /// </summary>
    public string[] DeniedTables { get; set; } = [];

    /// <summary>
    /// Whether SQL containing multiple statements is allowed.
    /// Default false to reduce accidental/destructive multi-statement execution.
    /// </summary>
    public bool AllowMultiStatement { get; set; } = false;
}

public sealed class InboxZeroConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>VIP sender addresses — emails from these are never auto-archived.</summary>
    public string[] VipSenders { get; set; } = [];

    /// <summary>Protected sender addresses or domains — e.g. doctor@hospital.org, bank.com.</summary>
    public string[] ProtectedSenders { get; set; } = [];

    /// <summary>Protected keywords in subject — emails matching these are never auto-archived.</summary>
    public string[] ProtectedKeywords { get; set; } = ["appointment", "flight", "boarding", "medical", "prescription", "invoice", "payment", "receipt"];

    /// <summary>Maximum emails to process per batch.</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>When true, report what would happen without actually moving/deleting anything.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Optional IMAP operation timeout in seconds.
    /// 0 disables this additional timeout and relies on the caller/tool timeout.
    /// </summary>
    public int ImapOperationTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Maximum number of IMAP response lines to read for a tagged command.
    /// Prevents infinite loops on protocol desync.
    /// </summary>
    public int MaxResponseLinesPerCommand { get; set; } = 10_000;
}

public sealed class PluginLoadConfig
{
    /// <summary>Extra plugin paths to scan (file or directory).</summary>
    public string[] Paths { get; set; } = [];
}

/// <summary>
/// Structured compatibility or validation diagnostic for plugin loading.
/// </summary>
public sealed class PluginCompatibilityDiagnostic
{
    public string Severity { get; init; } = "error";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Surface { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// Result of plugin discovery: discovered plugins plus structured load reports for invalid entries.
/// </summary>
public sealed class PluginDiscoveryResult
{
    public List<DiscoveredPlugin> Plugins { get; } = [];
    public List<PluginLoadReport> Reports { get; } = [];
}

/// <summary>
/// Per-plugin load report for diagnostics and doctor/status surfaces.
/// </summary>
public sealed class PluginLoadReport
{
    public required string PluginId { get; init; }
    public required string SourcePath { get; init; }
    public string? EntryPath { get; init; }
    public string Origin { get; init; } = "bridge";
    public bool Loaded { get; init; }
    public string EffectiveRuntimeMode { get; init; } = "jit";
    public string[] RequestedCapabilities { get; init; } = [];
    public bool BlockedByRuntimeMode { get; init; }
    public string? BlockedReason { get; init; }
    public int ToolCount { get; init; }
    public int ChannelCount { get; init; }
    public int CommandCount { get; init; }
    public int EventSubscriptionCount { get; init; }
    public int ProviderCount { get; init; }
    public string[] SkillDirectories { get; init; } = [];
    public PluginCompatibilityDiagnostic[] Diagnostics { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>
/// Tool registration from a plugin bridge — describes a tool the plugin exports.
/// </summary>
public sealed class PluginToolRegistration
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema for tool parameters.</summary>
    public required JsonElement Parameters { get; init; }

    /// <summary>Whether this tool is optional (opt-in only).</summary>
    public bool Optional { get; init; }
}

/// <summary>
/// JSON-RPC request envelope for plugin bridge communication.
/// </summary>
public sealed class BridgeRequest
{
    public required string Method { get; init; }
    public required string Id { get; init; }
    public JsonElement? Params { get; init; }
}

/// <summary>
/// JSON-RPC response envelope from the plugin bridge.
/// </summary>
public sealed class BridgeResponse
{
    public required string Id { get; init; }
    public JsonElement? Result { get; init; }
    public BridgeError? Error { get; init; }
}

/// <summary>
/// Error payload from the plugin bridge.
/// </summary>
public sealed class BridgeError
{
    public int Code { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Result of initializing a plugin bridge process.
/// </summary>
public sealed class BridgeInitResult
{
    public PluginToolRegistration[] Tools { get; init; } = [];
    public BridgeChannelRegistration[] Channels { get; init; } = [];
    public BridgeCommandRegistration[] Commands { get; init; } = [];
    public string[] EventSubscriptions { get; init; } = [];
    public BridgeProviderRegistration[] Providers { get; init; } = [];
    public string[] Capabilities { get; init; } = [];
    public PluginCompatibilityDiagnostic[] Diagnostics { get; init; } = [];
    public bool Compatible { get; init; } = true;
}

public sealed class NativeDynamicPluginsConfig
{
    public bool Enabled { get; set; } = false;
    public string[] Allow { get; set; } = [];
    public string[] Deny { get; set; } = [];
    public PluginLoadConfig Load { get; set; } = new();
    public Dictionary<string, PluginEntryConfig> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NativeDynamicPluginManifest
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public required string AssemblyPath { get; init; }
    public required string TypeName { get; init; }
    public string[] Capabilities { get; init; } = [];
    public string[] Skills { get; init; } = [];
    public bool JitOnly { get; init; } = true;
}

public sealed class DiscoveredNativeDynamicPlugin
{
    public required NativeDynamicPluginManifest Manifest { get; init; }
    public required string RootPath { get; init; }
    public required string ManifestPath { get; init; }
    public required string AssemblyPath { get; init; }
}

/// <summary>
/// Notification from a plugin bridge process (plugin → gateway).
/// </summary>
public sealed class BridgeNotification
{
    public required string Notification { get; init; }
    public JsonElement? Params { get; init; }
}

/// <summary>
/// Transport configuration for the plugin bridge.
/// </summary>
public sealed class BridgeTransportConfig
{
    /// <summary>"stdio" (default), "socket", or "hybrid".</summary>
    public string Mode { get; set; } = "stdio";

    /// <summary>Socket path for socket transport. Auto-generated per plugin if empty.</summary>
    public string? SocketPath { get; set; }
}

/// <summary>
/// Runtime transport details sent to the bridge process during initialization.
/// </summary>
public sealed class BridgeTransportRuntimeConfig
{
    public string Mode { get; init; } = "stdio";
    public string? SocketPath { get; init; }
}

/// <summary>
/// Channel registration from a plugin bridge.
/// </summary>
public sealed class BridgeChannelRegistration
{
    public required string Id { get; init; }
}

/// <summary>
/// Command registration from a plugin bridge.
/// </summary>
public sealed class BridgeCommandRegistration
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// Provider registration from a plugin bridge.
/// </summary>
public sealed class BridgeProviderRegistration
{
    public required string Id { get; init; }
    public string[] Models { get; init; } = [];
}

/// <summary>
/// Provider completion request sent from the gateway to a plugin bridge.
/// </summary>
public sealed class BridgeProviderRequest
{
    public required string ProviderId { get; init; }
    public required JsonElement Messages { get; init; }
    public BridgeProviderOptions? Options { get; init; }
}

/// <summary>
/// Serializable subset of <c>ChatOptions</c> forwarded to plugin providers.
/// </summary>
public sealed class BridgeProviderOptions
{
    public string? ConversationId { get; init; }
    public string? Instructions { get; init; }
    public float? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public float? FrequencyPenalty { get; init; }
    public float? PresencePenalty { get; init; }
    public long? Seed { get; init; }
    public BridgeReasoningOptions? Reasoning { get; init; }
    public BridgeResponseFormat? ResponseFormat { get; init; }
    public string? ModelId { get; init; }
    public string[] StopSequences { get; init; } = [];
    public bool? AllowMultipleToolCalls { get; init; }
    public BridgeToolMode? ToolMode { get; init; }
    public BridgeToolDescriptor[] Tools { get; init; } = [];
    public bool? AllowBackgroundResponses { get; init; }
    public string? ContinuationToken { get; init; }
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = new(StringComparer.Ordinal);
}

public sealed class BridgeReasoningOptions
{
    public string? Effort { get; init; }
    public string? Output { get; init; }
}

public sealed class BridgeResponseFormat
{
    public required string Kind { get; init; }
    public JsonElement? Schema { get; init; }
    public string? SchemaName { get; init; }
    public string? SchemaDescription { get; init; }
}

public sealed class BridgeToolMode
{
    public required string Kind { get; init; }
    public string? FunctionName { get; init; }
}

public sealed class BridgeToolDescriptor
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public JsonElement? InputSchema { get; init; }
    public JsonElement? ReturnSchema { get; init; }
}

public sealed class BridgeInitRequest
{
    public required string EntryPath { get; init; }
    public required string PluginId { get; init; }
    public JsonElement? Config { get; init; }
    public BridgeTransportRuntimeConfig Transport { get; init; } = new();
}

public sealed class BridgeExecuteRequest
{
    public required string Name { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed class BridgeChannelControlRequest
{
    public required string ChannelId { get; init; }
}

public sealed class BridgeChannelSendRequest
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public required string Text { get; init; }
    public string? SessionId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? Subject { get; init; }
    public BridgeMediaAttachment[]? Attachments { get; init; }
}

/// <summary>
/// Media attachment for bridge channel messages.
/// </summary>
public sealed class BridgeMediaAttachment
{
    /// <summary>Media type: "image", "video", "audio", "document", "sticker".</summary>
    public required string Type { get; init; }

    /// <summary>HTTP URL or file path to the media.</summary>
    public string? Url { get; init; }

    /// <summary>Optional caption for the media.</summary>
    public string? Caption { get; init; }

    /// <summary>MIME type hint (e.g. "audio/ogg; codecs=opus").</summary>
    public string? MimeType { get; init; }

    /// <summary>Original file name.</summary>
    public string? FileName { get; init; }

    /// <summary>When true, video should be sent as an animated GIF.</summary>
    public bool GifPlayback { get; init; }
}

/// <summary>
/// Request to send a typing indicator through a bridge channel.
/// </summary>
public sealed class BridgeChannelTypingRequest
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public bool IsTyping { get; init; } = true;
}

/// <summary>
/// Request to send a read receipt through a bridge channel.
/// </summary>
public sealed class BridgeChannelReceiptRequest
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public string? RemoteJid { get; init; }
    public string? Participant { get; init; }
}

/// <summary>
/// Request to send a reaction through a bridge channel.
/// </summary>
public sealed class BridgeChannelReactionRequest
{
    public required string ChannelId { get; init; }
    public required string MessageId { get; init; }
    public required string Emoji { get; init; }
    public string? RemoteJid { get; init; }
    public string? Participant { get; init; }
}

/// <summary>
/// Auth event notification from a bridge channel (e.g. QR code for WhatsApp linking).
/// </summary>
public sealed class BridgeChannelAuthEvent
{
    public required string ChannelId { get; init; }

    /// <summary>Auth state: "qr_code", "connected", "disconnected", "error".</summary>
    public required string State { get; init; }

    /// <summary>State-specific data (QR string, error message, etc.).</summary>
    public string? Data { get; init; }

    /// <summary>Account identifier for multi-account channels.</summary>
    public string? AccountId { get; init; }

    /// <summary>Timestamp assigned when the gateway receives the auth event.</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeCommandExecuteRequest
{
    public required string Name { get; init; }
    public string Args { get; init; } = "";
}

public sealed class BridgeHookBeforeRequest
{
    public required string EventName { get; init; }
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
}

public sealed class BridgeHookAfterRequest
{
    public required string EventName { get; init; }
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public required string Result { get; init; }
    public double DurationMs { get; init; }
    public bool Failed { get; init; }
}

/// <summary>
/// Tool execution result from the plugin bridge.
/// </summary>
public sealed class BridgeToolResult
{
    public ToolContentItem[] Content { get; init; } = [];
}

/// <summary>
/// MCP-compatible content item returned by plugin tools.
/// </summary>
public sealed class ToolContentItem
{
    public required string Type { get; init; }
    public string? Text { get; init; }
}
