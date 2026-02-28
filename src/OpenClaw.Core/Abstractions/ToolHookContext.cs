namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Context passed to <see cref="IToolHookWithContext"/>.
/// </summary>
public readonly record struct ToolHookContext
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string CorrelationId { get; init; }

    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }

    public required bool IsStreaming { get; init; }
}
