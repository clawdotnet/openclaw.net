namespace OpenClaw.Core.Models;

public sealed record TurnTokenUsageRecord
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
    public bool IsEstimated { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}