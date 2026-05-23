using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IHarnessContractStore
{
    ValueTask SaveAsync(HarnessContract contract, CancellationToken ct);
    ValueTask<HarnessContract?> GetAsync(string id, CancellationToken ct);
    ValueTask<IReadOnlyList<HarnessContract>> ListAsync(HarnessContractListQuery query, CancellationToken ct);
    ValueTask DeleteAsync(string id, CancellationToken ct);
}
