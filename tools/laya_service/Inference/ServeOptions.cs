namespace OpenClaw.LayaService.Inference;

public sealed record ServeOptions(
    string ManifestPath,
    string? CalibrationPath,
    int Port,
    string Device,
    string Checkpoint,
    int Threads);