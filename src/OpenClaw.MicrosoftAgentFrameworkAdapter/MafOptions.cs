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

    public bool EnableA2A { get; set; } = false;

    public string A2APathPrefix { get; set; } = "/a2a";

    public string A2AVersion { get; set; } = "1.0.0";

    public string? A2APublicBaseUrl { get; set; }

    public List<A2ASkillConfig> A2ASkills { get; set; } = [];
}

public sealed class A2ASkillConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
}
