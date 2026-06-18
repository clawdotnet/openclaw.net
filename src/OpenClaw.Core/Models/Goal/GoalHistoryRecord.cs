using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models.Goal;

/// <summary>
/// Record written to the goal history JSONL file when a goal completes or transitions
/// to a terminal/non-pursuable state.
/// </summary>
public sealed record GoalHistoryRecord
{
    public required string Timestamp { get; init; }
    public required string SessionId { get; init; }
    public required string Objective { get; init; }
    public required string Status { get; init; }
    public long TokenBudget { get; init; }
    public long TokensUsed { get; init; }
    public int ContinuationCount { get; init; }
    public required string CreatedAt { get; init; }
}

[JsonSerializable(typeof(GoalHistoryRecord))]
internal sealed partial class GoalJsonContext : JsonSerializerContext;
