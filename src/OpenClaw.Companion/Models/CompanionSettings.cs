namespace OpenClaw.Companion.Models;

public sealed class CompanionSettings
{
    public string ServerUrl { get; set; } = "ws://127.0.0.1:18789/ws";
    public bool RememberToken { get; set; } = false;
    public string? AuthToken { get; set; }
}

