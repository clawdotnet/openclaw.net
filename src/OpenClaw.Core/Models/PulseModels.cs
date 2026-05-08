namespace OpenClaw.Core.Models;

public sealed class PulseConfig
{
    public bool Enabled { get; set; } = true;
    public string Every { get; set; } = "30m";
    public string? Model { get; set; }
    public string Prompt { get; set; } = PulseDefaults.Prompt;
    public string AckToken { get; set; } = PulseDefaults.AckToken;
    public int AckMaxChars { get; set; } = 300;
    public string Target { get; set; } = "none";
    public string? To { get; set; }
    public string? AccountId { get; set; }
    public string DirectPolicy { get; set; } = "allow";
    public bool IncludeReasoning { get; set; } = false;
    public bool LightContext { get; set; } = false;
    public bool IsolatedSession { get; set; } = false;
    public bool SkipWhenBusy { get; set; } = true;
    public bool SuppressToolErrorWarnings { get; set; } = true;
    public string Session { get; set; } = "main";
    public PulseActiveHoursConfig? ActiveHours { get; set; }
    public PulseVisibilityConfig Visibility { get; set; } = new();
}

public sealed class PulseActiveHoursConfig
{
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "17:00";
    public string? Timezone { get; set; }
}

public sealed class PulseVisibilityConfig
{
    public bool ShowOk { get; set; } = false;
    public bool ShowAlerts { get; set; } = true;
    public bool UseIndicator { get; set; } = true;
}

public sealed class PulseRunRequest
{
    public string? Text { get; init; }
    public string Mode { get; init; } = "now";
}

public sealed class PulseRunResponse
{
    public bool Success { get; init; }
    public string Outcome { get; init; } = "unknown";
    public string? SkipReason { get; init; }
    public string? SessionId { get; init; }
    public string? MessagePreview { get; init; }
}

public sealed class PulseStatusResponse
{
    public required PulseConfig Config { get; init; }
    public string HeartbeatPath { get; init; } = "";
    public bool HeartbeatExists { get; init; }
    public bool HeartbeatEmpty { get; init; }
    public bool Enabled { get; init; }
    public string Interval { get; init; } = "30m";
    public DateTimeOffset? LastRunAtUtc { get; init; }
    public DateTimeOffset? LastCompletedAtUtc { get; init; }
    public DateTimeOffset? NextRunAtUtc { get; init; }
    public string LastResult { get; init; } = "never";
    public string? LastSkipReason { get; init; }
    public int RecentAlertCount { get; init; }
    public int RecentOkCount { get; init; }
    public IReadOnlyList<PulseAlertDto> RecentAlerts { get; init; } = [];
    public string? PendingManualText { get; init; }
}

public sealed class PulseAlertDto
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Text { get; init; } = "";
    public string Severity { get; init; } = "info";
}

public sealed class PulseState
{
    public DateTimeOffset? LastRunAtUtc { get; set; }
    public DateTimeOffset? LastCompletedAtUtc { get; set; }
    public DateTimeOffset? NextRunAtUtc { get; set; }
    public string LastResult { get; set; } = "never";
    public string? LastSkipReason { get; set; }
    public int RecentOkCount { get; set; }
    public string? PendingManualText { get; set; }
    public List<PulseAlertDto> RecentAlerts { get; set; } = [];
    public Dictionary<string, DateTimeOffset> TaskLastRunUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PulseTaskDefinition
{
    public string Name { get; init; } = "";
    public string Interval { get; init; } = "";
    public string Prompt { get; init; } = "";
}

public static class PulseDefaults
{
    public const string AckToken = "HEARTBEAT_OK";
    public const string Prompt = "Read HEARTBEAT.md if it exists in the workspace context. Follow it strictly. Do not infer or repeat old tasks from prior chats. If nothing needs attention, reply HEARTBEAT_OK.";
}

public static class PulseEventActions
{
    public const string RunStarted = "pulse_run_started";
    public const string RunCompleted = "pulse_run_completed";
    public const string Skipped = "pulse_skipped";
    public const string Alert = "pulse_alert";
    public const string OkSuppressed = "pulse_ok_suppressed";
    public const string DeliverySkipped = "pulse_delivery_skipped";
    public const string Error = "pulse_error";
    public const string ManualWake = "pulse_manual_wake";
}

public static class PulseSkipReasons
{
    public const string Disabled = "disabled";
    public const string OutsideActiveHours = "outside-active-hours";
    public const string Busy = "busy";
    public const string EmptyHeartbeatFile = "empty-heartbeat-file";
    public const string VisibilityDisabled = "visibility-disabled";
    public const string NoSession = "no-session";
    public const string EmptyManualWake = "empty-manual-wake";
    public const string DeliveryBlocked = "delivery-blocked";
    public const string DmBlocked = "dm-blocked";
    public const string ModelUnavailable = "model-unavailable";
    public const string Error = "error";
}
