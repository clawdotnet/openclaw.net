using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

public sealed class AdminSettingsSnapshot
{
    public string UsageFooter { get; init; } = "off";
    public int MaxConcurrentSessions { get; init; }
    public int SessionTimeoutMinutes { get; init; }
    public long SessionTokenBudget { get; init; }
    public int SessionRateLimitPerMinute { get; init; }
    public bool AllowQueryStringToken { get; init; }
    public int BrowserSessionIdleMinutes { get; init; }
    public int BrowserRememberDays { get; init; }
    public string AutonomyMode { get; init; } = "supervised";
    public bool RequireToolApproval { get; init; }
    public int ToolApprovalTimeoutSeconds { get; init; }
    public bool ParallelToolExecution { get; init; }
    public bool AllowShell { get; init; }
    public bool ReadOnlyMode { get; init; }
    public bool EnableBrowserTool { get; init; }
    public bool AllowBrowserEvaluate { get; init; }
    public int MaxHistoryTurns { get; init; }
    public bool EnableCompaction { get; init; }
    public int CompactionThreshold { get; init; }
    public int CompactionKeepRecent { get; init; }
    public bool RetentionEnabled { get; init; }
    public bool RetentionRunOnStartup { get; init; }
    public int RetentionSweepIntervalMinutes { get; init; }
    public int RetentionSessionTtlDays { get; init; }
    public int RetentionBranchTtlDays { get; init; }
    public bool RetentionArchiveEnabled { get; init; }
    public int RetentionArchiveRetentionDays { get; init; }
    public int RetentionMaxItemsPerSweep { get; init; }
    public string AllowlistSemantics { get; init; } = "legacy";
    public bool SmsEnabled { get; init; }
    public bool SmsValidateSignature { get; init; }
    public string SmsDmPolicy { get; init; } = "pairing";
    public bool TelegramEnabled { get; init; }
    public bool TelegramValidateSignature { get; init; }
    public string TelegramDmPolicy { get; init; } = "pairing";
    public bool TeamsEnabled { get; init; }
    public bool TeamsValidateToken { get; init; }
    public string TeamsDmPolicy { get; init; } = "pairing";
    public bool SlackEnabled { get; init; }
    public bool SlackValidateSignature { get; init; }
    public string SlackDmPolicy { get; init; } = "pairing";
    public bool DiscordEnabled { get; init; }
    public bool DiscordValidateSignature { get; init; }
    public string DiscordDmPolicy { get; init; } = "pairing";
    public bool SignalEnabled { get; init; }
    public string SignalDmPolicy { get; init; } = "pairing";
    [JsonPropertyName("whatsappEnabled")]
    public bool WhatsAppEnabled { get; init; }
    [JsonPropertyName("whatsappValidateSignature")]
    public bool WhatsAppValidateSignature { get; init; }
    [JsonPropertyName("whatsappDmPolicy")]
    public string WhatsAppDmPolicy { get; init; } = "pairing";
    [JsonPropertyName("whatsappType")]
    public string WhatsAppType { get; init; } = "official";
    [JsonPropertyName("whatsappWebhookPath")]
    public string WhatsAppWebhookPath { get; init; } = "/whatsapp/inbound";
    [JsonPropertyName("whatsappWebhookPublicBaseUrl")]
    public string? WhatsAppWebhookPublicBaseUrl { get; init; }
    [JsonPropertyName("whatsappWebhookVerifyToken")]
    public string WhatsAppWebhookVerifyToken { get; init; } = "openclaw-verify";
    [JsonPropertyName("whatsappWebhookVerifyTokenRef")]
    public string WhatsAppWebhookVerifyTokenRef { get; init; } = "env:WHATSAPP_VERIFY_TOKEN";
    [JsonPropertyName("whatsappWebhookAppSecret")]
    public string? WhatsAppWebhookAppSecret { get; init; }
    [JsonPropertyName("whatsappWebhookAppSecretRef")]
    public string WhatsAppWebhookAppSecretRef { get; init; } = "env:WHATSAPP_APP_SECRET";
    [JsonPropertyName("whatsappCloudApiToken")]
    public string? WhatsAppCloudApiToken { get; init; }
    [JsonPropertyName("whatsappCloudApiTokenRef")]
    public string WhatsAppCloudApiTokenRef { get; init; } = "env:WHATSAPP_CLOUD_API_TOKEN";
    [JsonPropertyName("whatsappPhoneNumberId")]
    public string? WhatsAppPhoneNumberId { get; init; }
    [JsonPropertyName("whatsappBusinessAccountId")]
    public string? WhatsAppBusinessAccountId { get; init; }
    [JsonPropertyName("whatsappBridgeUrl")]
    public string? WhatsAppBridgeUrl { get; init; }
    [JsonPropertyName("whatsappBridgeToken")]
    public string? WhatsAppBridgeToken { get; init; }
    [JsonPropertyName("whatsappBridgeTokenRef")]
    public string WhatsAppBridgeTokenRef { get; init; } = "env:WHATSAPP_BRIDGE_TOKEN";
    [JsonPropertyName("whatsappBridgeSuppressSendExceptions")]
    public bool WhatsAppBridgeSuppressSendExceptions { get; init; }
    [JsonPropertyName("whatsappFirstPartyWorker")]
    public WhatsAppFirstPartyWorkerConfig WhatsAppFirstPartyWorker { get; init; } = new();
}

public sealed class AdminSettingsPersistenceInfo
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public DateTimeOffset? LastModifiedAtUtc { get; init; }
}
