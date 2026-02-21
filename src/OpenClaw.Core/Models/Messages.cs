namespace OpenClaw.Core.Models;

/// <summary>
/// Inbound message from any channel adapter.
/// </summary>
public sealed record InboundMessage
{
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public string? SessionId { get; init; }
    public required string Text { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Outbound message to be routed back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public required string Text { get; init; }
    public string? ReplyToMessageId { get; init; }
}
