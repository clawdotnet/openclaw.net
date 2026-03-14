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
}
