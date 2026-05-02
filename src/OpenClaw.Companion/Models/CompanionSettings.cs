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
    public bool ApprovalDesktopNotificationsEnabled { get; set; } = true;
    public bool ApprovalDesktopNotificationsOnlyWhenUnfocused { get; set; } = true;
    public bool AutoStartLocalGateway { get; set; } = true;
    public string SetupProvider { get; set; } = "openai";
    public string SetupModel { get; set; } = "gpt-4o";
    public string SetupModelPreset { get; set; } = "";
    public string SetupWorkspacePath { get; set; } = "";

    [JsonIgnore]
    public string? AuthToken { get; set; }
}
