namespace OpenClaw.Core.Models;

/// <summary>
/// Optional JSON envelope used by WebSocket clients.
/// Raw-text clients may continue sending plain text.
/// </summary>
public sealed record WsClientEnvelope
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? Content { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }

    // Tool approval decision (client -> server)
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
}

/// <summary>
/// JSON envelope sent by the gateway when a client opts into envelopes.
/// </summary>
public sealed record WsServerEnvelope
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? InReplyToMessageId { get; init; }

    // Tool approval request/status (server -> client)
    public string? ApprovalId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsPreview { get; init; }
    public bool? Approved { get; init; }
}
