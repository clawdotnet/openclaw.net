namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Options controlling Semantic Kernel interop behavior.
/// </summary>
public sealed class SemanticKernelInteropOptions
{
    /// <summary>Prefix for per-function tool names.</summary>
    public string ToolNamePrefix { get; set; } = "sk_";

    /// <summary>Maximum tool name length.</summary>
    public int MaxToolNameLength { get; set; } = 64;

    /// <summary>Maximum number of SK functions to map into tools.</summary>
    public int MaxMappedTools { get; set; } = 256;

    /// <summary>
    /// Optional allowlist of SK plugin names. Empty => allow all.
    /// </summary>
    public string[] AllowedPlugins { get; set; } = [];

    /// <summary>
    /// Optional allowlist of SK function names. Empty => allow all.
    /// </summary>
    public string[] AllowedFunctions { get; set; } = [];
}
