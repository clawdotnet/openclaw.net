namespace OpenClaw.Core.Models;

public sealed class SessionSummary
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; init; }
    public SessionState State { get; init; }
    public int HistoryTurns { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public bool IsActive { get; init; }
}

public sealed class PagedSessionList
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
    public int ReturnedCount => Items.Count;
    public IReadOnlyList<SessionSummary> Items { get; init; } = [];
}

public sealed class SessionListQuery
{
    public string? Search { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public SessionState? State { get; init; }
    public bool? Starred { get; init; }
    public string? Tag { get; init; }
}
