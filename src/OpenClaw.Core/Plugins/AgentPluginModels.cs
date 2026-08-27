namespace OpenClaw.Core.Plugins;

/// <summary>
/// Represents an agent-plugin manifest (agent-plugin.json).
/// Compatible with the Agent Plugins 1.0 specification.
/// </summary>
public sealed class AgentPluginManifest
{
    /// <summary>Plugin name.</summary>
    public required string Name { get; init; }

    /// <summary>Plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Plugin description.</summary>
    public required string Description { get; init; }

    /// <summary>License identifier (e.g. "MIT", "Apache-2.0").</summary>
    public required string License { get; init; }

    /// <summary>Homepage URL.</summary>
    public string? Homepage { get; init; }

    /// <summary>Repository URL.</summary>
    public string? Repository { get; init; }

    /// <summary>Keywords for discovery.</summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>JSON Schema for plugin configuration.</summary>
    public string? Schema { get; init; }

    /// <summary>Author information.</summary>
    public string? Author { get; init; }
}

/// <summary>
/// Represents a discovered Agent Plugin package on disk - manifest + filesystem location.
/// </summary>
public sealed class AgentPluginPackage
{
    /// <summary>The plugin manifest.</summary>
    public required AgentPluginManifest Manifest { get; init; }

    /// <summary>Absolute path to the plugin root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Absolute path to the skills directory (relative to RootPath).</summary>
    public string? SkillsPath { get; init; }

    /// <summary>Absolute path to the MCP configuration file (relative to RootPath).</summary>
    public string? McpConfigPath { get; init; }

    /// <summary>List of skill directories or files.</summary>
    public List<string> Skills { get; init; } = [];

    /// <summary>MCP server configurations.</summary>
    public List<McpServerConfig> McpServers { get; init; } = [];
}

/// <summary>
/// Result of Agent Plugin discovery: discovered packages plus structured load reports for invalid entries.
/// </summary>
public sealed class AgentPluginDiscoveryResult
{
    /// <summary>Successfully discovered Agent Plugin packages.</summary>
    public List<AgentPluginPackage> Packages { get; } = [];

    /// <summary>Per-plugin load reports for diagnostics and status surfaces.</summary>
    public List<PluginLoadReport> Reports { get; } = [];
}
