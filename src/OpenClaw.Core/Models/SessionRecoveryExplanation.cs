namespace OpenClaw.Core.Models;

/// <summary>A read-only explanation of recorded run state, not a replay authorization.</summary>
public sealed class SessionRecoveryExplanation
{
    public string Status { get; init; } = "unknown";
    public string Summary { get; init; } = "";
    public string[] Evidence { get; init; } = [];
    public string[] NextSteps { get; init; } = [];
}
