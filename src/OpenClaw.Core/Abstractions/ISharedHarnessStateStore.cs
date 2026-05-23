using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ISharedHarnessStateStore
{
    ValueTask SaveAsync(SharedHarnessState state, CancellationToken ct);
    ValueTask<SharedHarnessState?> GetAsync(string id, CancellationToken ct);
    ValueTask<SharedHarnessState?> GetBySessionAsync(string sessionId, CancellationToken ct);
    ValueTask<IReadOnlyList<SharedHarnessState>> ListAsync(SharedHarnessStateListQuery query, CancellationToken ct);
    ValueTask DeleteAsync(string id, CancellationToken ct);
}
