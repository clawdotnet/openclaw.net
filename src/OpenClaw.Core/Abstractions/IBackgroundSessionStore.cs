using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IBackgroundSessionStore
{
    ValueTask<IReadOnlyList<Session>> ListBackgroundRunnableSessionsAsync(int limit, CancellationToken ct);
}
