using OpenClaw.Core.Models;
namespace OpenClaw.Core.Abstractions;

/// <summary>Detached persisted sessions; reading never populates or evicts the runtime session cache.</summary>
public interface ISessionSnapshotSource
{
    IAsyncEnumerable<Session> ReadSnapshotsAsync(DateTimeOffset? since, CancellationToken ct);
}
