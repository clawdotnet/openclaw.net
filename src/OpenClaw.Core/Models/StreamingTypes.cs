namespace OpenClaw.Core.Models;

/// <summary>
/// Event types emitted by the streaming agent runtime.
/// </summary>
public enum AgentStreamEventType : byte
{
    /// <summary>Incremental text from the LLM.</summary>
    TextDelta,

    /// <summary>A tool execution is starting.</summary>
    ToolStart,

    /// <summary>A tool execution completed (Content = result).</summary>
    ToolResult,

    /// <summary>An error occurred during processing.</summary>
    Error,

    /// <summary>The agent turn is complete.</summary>
    Done
}

/// <summary>
/// A single event in a streaming agent response. Designed for zero-allocation hot path
/// (struct, no heap allocs for the common TextDelta case).
/// </summary>
public readonly record struct AgentStreamEvent
{
    public AgentStreamEventType Type { get; init; }
    public string Content { get; init; }
    public string? ToolName { get; init; }

    public static AgentStreamEvent TextDelta(string text) =>
        new() { Type = AgentStreamEventType.TextDelta, Content = text };

    public static AgentStreamEvent ToolStarted(string toolName) =>
        new() { Type = AgentStreamEventType.ToolStart, Content = toolName, ToolName = toolName };

    public static AgentStreamEvent ToolCompleted(string toolName, string result) =>
        new() { Type = AgentStreamEventType.ToolResult, Content = result, ToolName = toolName };

    public static AgentStreamEvent ErrorOccurred(string error) =>
        new() { Type = AgentStreamEventType.Error, Content = error };

    public static AgentStreamEvent Complete() =>
        new() { Type = AgentStreamEventType.Done, Content = "" };

    /// <summary>Maps the event type to a WS envelope type string.</summary>
    public string EnvelopeType => Type switch
    {
        AgentStreamEventType.TextDelta => "assistant_chunk",
        AgentStreamEventType.ToolStart => "tool_start",
        AgentStreamEventType.ToolResult => "tool_result",
        AgentStreamEventType.Error => "error",
        AgentStreamEventType.Done => "assistant_done",
        _ => "assistant_chunk"
    };
}
