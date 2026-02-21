using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Models;

/// <summary>
/// Defines a named agent persona for multi-agent delegation.
/// Each profile specifies a system prompt, allowed tools, and optional model override.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Unique name for this profile (e.g. "researcher", "coder").</summary>
    public string Name { get; set; } = "";

    /// <summary>Custom system prompt for the sub-agent.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Tool names this sub-agent is allowed to use. Empty = all tools.</summary>
    public string[] AllowedTools { get; set; } = [];

    /// <summary>Max tool-use iterations for the sub-agent (default 5).</summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>Max history turns for the sub-agent's ephemeral session.</summary>
    public int MaxHistoryTurns { get; set; } = 20;
}

/// <summary>
/// Configuration for multi-agent delegation.
/// </summary>
public sealed class DelegationConfig
{
    /// <summary>Enable the delegate_agent tool.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Max delegation depth to prevent infinite agent loops.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Named agent profiles that can be delegated to.</summary>
    public Dictionary<string, AgentProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
}
