namespace OpenClaw.McpApp.Models;

/// <summary>
/// Runtime state for a discovered and potentially running MCP App.
/// Tracks lifecycle: discovered → loaded → running → stopped.
/// </summary>
public sealed class McpAppInstallState
{
    /// <summary>The app's manifest.</summary>
    public required McpAppManifest Manifest { get; init; }

    /// <summary>Absolute path to the manifest file on disk.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Absolute path to the app root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Whether the app passed validation and is loadable.</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Validation errors that prevent loading.</summary>
    public List<string> ValidationErrors { get; init; } = [];

    /// <summary>Current lifecycle state.</summary>
    public McpAppLifecycle Lifecycle { get; set; } = McpAppLifecycle.Discovered;

    /// <summary>Timestamp when the app was discovered.</summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Timestamp when the app last transitioned state.</summary>
    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Number of tools discovered from this app.</summary>
    public int DiscoveredToolCount { get; set; }

    /// <summary>Number of resources discovered from this app.</summary>
    public int DiscoveredResourceCount { get; set; }

    /// <summary>Number of prompts discovered from this app.</summary>
    public int DiscoveredPromptCount { get; set; }
}

/// <summary>
/// Lifecycle states for an MCP App.
/// </summary>
public enum McpAppLifecycle
{
    /// <summary>Manifest found on disk, not yet validated.</summary>
    Discovered = 0,

    /// <summary>Manifest validated, not yet loaded.</summary>
    Validated = 1,

    /// <summary>App client connected and capabilities enumerated.</summary>
    Loaded = 2,

    /// <summary>App is running and tools/resources are available.</summary>
    Running = 3,

    /// <summary>App has been stopped or disconnected.</summary>
    Stopped = 4,

    /// <summary>App failed to load or crashed.</summary>
    Failed = 5,

    /// <summary>App is explicitly disabled by configuration.</summary>
    Disabled = 6,
}