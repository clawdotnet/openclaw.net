namespace OpenClaw.Dashboard.Models;

public record SessionInfo(
    string SessionId,
    string? ChannelId,
    string? SenderId,
    DateTime? LastActive,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    Dictionary<string, object>? Metadata
);

public record SessionDetail(
    string SessionId,
    string? ChannelId,
    string? SenderId,
    DateTime? LastActive,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    Dictionary<string, object>? Metadata,
    List<SessionMessage>? Messages
);

public record SessionMessage(
    string Role,
    string? Content,
    DateTime? Timestamp
);
