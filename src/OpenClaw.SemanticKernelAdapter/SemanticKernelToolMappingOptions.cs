namespace OpenClaw.SemanticKernelAdapter;

/// <summary>
/// Config-driven mapping of SK plugins/functions to OpenClaw tools.
/// Adapter-only: not used by the main OpenClaw gateway.
/// </summary>
public sealed class SemanticKernelToolMappingOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Optional allowlist of plugin names to expose.</summary>
    public string[] Plugins { get; set; } = [];

    /// <summary>Maximum number of tools to expose.</summary>
    public int MaxTools { get; set; } = 256;

    /// <summary>Tool name prefix, default sk_.</summary>
    public string NamePrefix { get; set; } = "sk_";

    /// <summary>Default output format for the entrypoint tool.</summary>
    public string DefaultFormat { get; set; } = "text";
}
