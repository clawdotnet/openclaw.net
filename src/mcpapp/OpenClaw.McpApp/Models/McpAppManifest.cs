using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.McpApp.Models;

/// <summary>
/// Represents an openclaw.mcpapp.json manifest file that describes
/// an MCP Application — its identity, capabilities, and runtime requirements.
/// MCP Apps are MCP Servers that may additionally include an interactive UI
/// (served as a resource with MIME type text/html;profile=mcp-app).
/// </summary>
public sealed class McpAppManifest
{
    /// <summary>Canonical app id (e.g. "grocery-inventory").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? Name { get; init; }

    /// <summary>Short description of what the app does.</summary>
    public string? Description { get; init; }

    /// <summary>Semantic version.</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>Minimum OpenClaw host version required.</summary>
    public string? MinHostVersion { get; init; }

    /// <summary>MCP protocol version(s) supported (e.g. "2025-03-26").</summary>
    public string ProtocolVersion { get; init; } = "2025-03-26";

    /// <summary>Icon URL or relative path for the app.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Author or organization name.</summary>
    public string? Author { get; init; }

    /// <summary>License identifier (SPDX).</summary>
    public string? License { get; init; }

    /// <summary>URL to the app homepage or repository.</summary>
    public string? HomepageUrl { get; init; }

    /// <summary>Tags for categorization / discovery.</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>
    /// Transport mode for connecting to the MCP App server.
    /// "stdio" (launch a process), "http" (connect to a URL), or "inprocess" (host in-process).
    /// </summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>Command to launch (stdio transport).</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for the launch command.</summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>Working directory for the launched process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Base URL to connect to (http transport).</summary>
    public string? Url { get; init; }

    /// <summary>HTTP headers for transport authentication.</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Environment variables to set when launching the process.</summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the app includes an MCP App UI bundle (HTML resource
    /// served with MIME type text/html;profile=mcp-app).
    /// </summary>
    public bool HasUi { get; init; }

    /// <summary>
    /// URI template for the main UI resource (e.g. "ui://grocery/store-dashboard.html").
    /// Only meaningful when <see cref="HasUi"/> is true.
    /// </summary>
    public string? UiResourceUri { get; init; }

    /// <summary>
    /// Category / kind of the MCP App for organizational grouping.
    /// Examples: "data", "productivity", "devtools", "entertainment".
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// List of capability strings the app declares (e.g. "tools", "resources", "prompts", "completions").
    /// </summary>
    public string[] Capabilities { get; init; } = ["tools"];

    /// <summary>Startup timeout in seconds.</summary>
    public int StartupTimeoutSeconds { get; set; } = 15;

    /// <summary>Per-request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Prefix to prepend to tool names for disambiguation.</summary>
    public string? ToolNamePrefix { get; init; }

    /// <summary>
    /// Arbitrary metadata dictionary for extensibility.
    /// </summary>
    public Dictionary<string, JsonElement> Metadata { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// JSON serializer context for <see cref="McpAppManifest"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(McpAppManifest))]
public sealed partial class McpAppManifestJsonContext : JsonSerializerContext;