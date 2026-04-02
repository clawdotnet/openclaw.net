namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafOptions
{
    public const string SectionName = "OpenClaw:Experimental:MicrosoftAgentFramework";

    public string AgentName { get; set; } = "OpenClaw";

    public string AgentDescription { get; set; } =
        "Microsoft Agent Framework orchestration backend for OpenClaw.";

    public string SessionSidecarPath { get; set; } = "experiments/maf/sessions";

    public bool EnableStreaming { get; set; } = true;

    public bool EnableStreamingFallback
    {
        get => EnableStreaming;
        set => EnableStreaming = value;
    }

    // ── A2A Protocol Configuration ──────────────────────────────────

    /// <summary>Whether to expose A2A protocol endpoints when MAF is enabled.</summary>
    public bool EnableA2A { get; set; } = true;

    /// <summary>URL path prefix for A2A endpoints (default: <c>/a2a</c>).</summary>
    public string A2APathPrefix { get; set; } = "/a2a";

    /// <summary>Version string reported in the A2A <c>AgentCard</c>.</summary>
    public string A2AVersion { get; set; } = "1.0.0";

    /// <summary>Skill descriptors published in the A2A agent card.</summary>
    public List<A2ASkillConfig> A2ASkills { get; set; } = [];
}

/// <summary>
/// Configures a single skill entry in the A2A agent card.
/// </summary>
public sealed class A2ASkillConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
}
