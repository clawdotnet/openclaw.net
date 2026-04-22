namespace OpenClaw.Gateway.Bootstrap;

internal sealed class LocalStartupState
{
    public string? WorkspacePath { get; init; }
    public string? MemoryPath { get; init; }
    public int? Port { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public bool BrowserPromptShown { get; init; }
    public string? LastSavedConfigPath { get; init; }
}
