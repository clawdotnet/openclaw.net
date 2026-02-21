namespace OpenClaw.Core.Models;

/// <summary>
/// A snapshot of a session's conversation history at a specific point in time.
/// Used for conversation branching — exploring alternative conversation paths
/// while preserving the ability to return to any previous branch point.
/// </summary>
public sealed class SessionBranch
{
    public required string BranchId { get; init; }
    public required string SessionId { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required List<ChatTurn> History { get; init; }
}
