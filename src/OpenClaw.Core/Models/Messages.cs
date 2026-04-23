namespace OpenClaw.Core.Models;

/// <summary>
/// Inbound message from any channel adapter.
/// </summary>
public sealed record InboundMessage
{
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public string? AccountId { get; init; }
    public string? SessionId { get; init; }
    public string? CronJobName { get; init; }
    public string? AutomationRunId { get; init; }
    public string? AutomationTriggerSource { get; init; }
    public string? Type { get; init; }
    public required string Text { get; init; }
    public string? SenderName { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public bool IsSystem { get; init; }
    public string? Subject { get; init; }
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationToken RequestCancellation { get; init; } = CancellationToken.None;

    // Group chat fields
    public bool IsGroup { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }
    public string[]? MentionedIds { get; init; }

    // Media fields
    public string? MediaType { get; init; }
    public string? MediaUrl { get; init; }
    public string? MediaMimeType { get; init; }
    public string? MediaFileName { get; init; }
}

/// <summary>
/// Outbound message to be routed back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public required string Text { get; init; }
    public string? AccountId { get; init; }
    public string? SessionId { get; init; }
    public string? CronJobName { get; init; }
    public string? AutomationRunId { get; init; }
    public string? Subject { get; init; }
    public string? ReplyToMessageId { get; init; }
}
