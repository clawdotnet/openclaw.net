namespace OpenClaw.Core.Abstractions;

public sealed record MemoryNoteHit
{
    public required string Key { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public float Score { get; init; }
}

public interface IMemoryNoteSearch
{
    ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct);
}

