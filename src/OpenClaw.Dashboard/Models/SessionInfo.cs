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
    Dictionary<string, object>? Metadata,
    string? RunState = null,
    string? BackgroundRunObjective = null,
    int BackgroundContinuationCount = 0
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
    List<SessionMessage>? Messages,
    string? RunState = null,
    string? BackgroundRunObjective = null,
    int BackgroundContinuationCount = 0
);

public record SessionMessage(
    string Role,
    string? Content,
    DateTime? Timestamp
);
