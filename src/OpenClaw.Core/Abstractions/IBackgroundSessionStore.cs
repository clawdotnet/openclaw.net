using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IBackgroundSessionStore
{
    ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct);

    /// <summary>Pages runnable sessions by stable ordinal session ID for startup recovery.</summary>
    ValueTask<IReadOnlyList<Session>> ListBackgroundRecoveryPageAsync(int limit, string? afterSessionId, CancellationToken ct);
}
