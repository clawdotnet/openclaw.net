using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Durable memory store for sessions and agent notes.
/// File-system backed by default for local-first operation.
/// </summary>
public interface IMemoryStore
{
    ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct);
    ValueTask SaveSessionAsync(Session session, CancellationToken ct);
    ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct);
    ValueTask SaveNoteAsync(string key, string content, CancellationToken ct);
    ValueTask DeleteNoteAsync(string key, CancellationToken ct);
    ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct);

    // ── Conversation Branching ─────────────────────────────────────────
    ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct);
    ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct);
    ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct);
    ValueTask DeleteBranchAsync(string branchId, CancellationToken ct);
}
