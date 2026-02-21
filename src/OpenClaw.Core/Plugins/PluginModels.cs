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

    /// <summary>Configuration for native plugin replicas.</summary>
    public NativePluginsConfig Native { get; set; } = new();
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
}

public sealed class PluginLoadConfig
{
    /// <summary>Extra plugin paths to scan (file or directory).</summary>
    public string[] Paths { get; set; } = [];
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
