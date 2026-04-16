using System.Text.Json.Serialization;

namespace OpenClaw.Companion.Models;

public sealed class CompanionSettings
{
    public string ServerUrl { get; set; } = "ws://127.0.0.1:18789/ws";
    public string Username { get; set; } = "";
    public string OperatorTokenLabel { get; set; } = "companion";
    public bool RememberToken { get; set; } = false;
    public bool AllowPlaintextTokenFallback { get; set; } = false;
    public bool DebugMode { get; set; } = false;

    [JsonIgnore]
    public string? AuthToken { get; set; }
}
