namespace OpenClaw.Gateway.Bootstrap;

internal sealed record LocalStartupSession(
    string Mode,
    string WorkspacePath,
    string MemoryPath,
    int Port,
    string Provider,
    string Model,
    string ApiKeyReference,
    string? Endpoint);
