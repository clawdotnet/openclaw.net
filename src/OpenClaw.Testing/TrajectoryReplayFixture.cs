namespace OpenClaw.Testing;

/// <summary>Sanitized provider responses and tool outputs for one exported user exchange.</summary>
public sealed class TrajectoryReplayFixture
{
    public int SchemaVersion { get; init; } = 1;
    public string Prompt { get; init; } = "";
    public List<ReplayResponse> Responses { get; init; } = [];
}

public sealed class ReplayResponse
{
    public string Text { get; init; } = "";
    public List<ReplayToolCall> ToolCalls { get; init; } = [];
}

public sealed class ReplayToolCall
{
    public string ToolName { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
    public string Result { get; init; } = "";
}
