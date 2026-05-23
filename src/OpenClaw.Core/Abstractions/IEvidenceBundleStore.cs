using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IEvidenceBundleStore
{
    ValueTask SaveAsync(EvidenceBundle bundle, CancellationToken ct);
    ValueTask<EvidenceBundle?> GetAsync(string id, CancellationToken ct);
    ValueTask<IReadOnlyList<EvidenceBundle>> ListAsync(EvidenceBundleListQuery query, CancellationToken ct);
    ValueTask DeleteAsync(string id, CancellationToken ct);
}
