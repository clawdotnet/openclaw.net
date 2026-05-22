using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IGovernanceLedgerStore
{
    ValueTask SaveAsync(GovernanceLedgerEntry entry, CancellationToken ct);
    ValueTask<GovernanceLedgerEntry?> GetAsync(string id, CancellationToken ct);
    ValueTask<IReadOnlyList<GovernanceLedgerEntry>> ListAsync(GovernanceLedgerListQuery query, CancellationToken ct);
    ValueTask<GovernanceLedgerEntry?> RevokeAsync(string id, string revokedBy, string reason, CancellationToken ct);
}
