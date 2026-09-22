namespace OpenClaw.Core.Models;

/// <summary>Lightweight recovery metadata; histories and checkpoints are skipped during scans.</summary>
public sealed class BackgroundRecoveryIndexEntry
{
    public string Id { get; init; } = "";
    public SessionRunState RunState { get; init; }
    public BackgroundRunMetadata? BackgroundRun { get; init; }
}
