namespace OpenClaw.Core.Models;

public static class AutomationLifecycleStates
{
    public const string Never = "never";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Stuck = "stuck";
}

public static class AutomationVerificationStatuses
{
    public const string NotRun = "not_run";
    public const string Verified = "verified";
    public const string NotVerified = "not_verified";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
}

public static class AutomationHealthStates
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Quarantined = "quarantined";
}

public static class AutomationRunTriggerSources
{
    public const string Schedule = "schedule";
    public const string Manual = "manual";
    public const string Retry = "retry";
    public const string Replay = "replay";
    public const string Heartbeat = "heartbeat";
}

public static class AutomationSignalSeverities
{
    public const string Alert = "alert";
    public const string Error = "error";
}

public sealed class AutomationsConfig
{
    public bool Enabled { get; set; } = true;
    public string DefaultDeliveryChannelId { get; set; } = "cron";
    public int SuggestionThreshold { get; set; } = 3;
}

public sealed class AutomationRetryPolicy
{
    public bool Enabled { get; init; }
    public int MaxRetries { get; init; }
}

public sealed class AutomationDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Schedule { get; init; } = "@hourly";
    public string? Timezone { get; init; }
    public string Prompt { get; init; } = "";
    public string? ModelId { get; init; }
    public string ResponseMode { get; init; } = SessionResponseModes.Default;
    public bool RunOnStartup { get; init; }
    public string? SessionId { get; init; }
    public string DeliveryChannelId { get; init; } = "cron";
    public string? DeliveryRecipientId { get; init; }
    public string? DeliverySubject { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsDraft { get; init; }
    public string Source { get; init; } = "managed";
    public string? TemplateKey { get; init; }
    public VerificationPolicy? Verification { get; init; }
    public AutomationRetryPolicy RetryPolicy { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationRunState
{
    public required string AutomationId { get; init; }
    public string Outcome { get; init; } = "never";
    public string LifecycleState { get; init; } = AutomationLifecycleStates.Never;
    public string VerificationStatus { get; init; } = AutomationVerificationStatuses.NotRun;
    public string HealthState { get; init; } = AutomationHealthStates.Unknown;
    public DateTimeOffset? LastRunAtUtc { get; init; }
    public DateTimeOffset? LastCompletedAtUtc { get; init; }
    public DateTimeOffset? LastDeliveredAtUtc { get; init; }
    public DateTimeOffset? LastVerifiedSuccessAtUtc { get; init; }
    public DateTimeOffset? QuarantinedAtUtc { get; init; }
    public DateTimeOffset? NextRetryAtUtc { get; init; }
    public bool DeliverySuppressed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public int FailureStreak { get; init; }
    public int UnverifiedStreak { get; init; }
    public int? NextRetryAttempt { get; init; }
    public string? LastRunId { get; init; }
    public string? SessionId { get; init; }
    public string? MessagePreview { get; init; }
    public string? VerificationSummary { get; init; }
    public string? QuarantineReason { get; init; }
    public string? SignalSeverity { get; init; }
}

public sealed record AutomationRunRecord
{
    public required string RunId { get; init; }
    public required string AutomationId { get; init; }
    public string TriggerSource { get; init; } = AutomationRunTriggerSources.Manual;
    public string LifecycleState { get; init; } = AutomationLifecycleStates.Queued;
    public string VerificationStatus { get; init; } = AutomationVerificationStatuses.NotRun;
    public string? ReplayOfRunId { get; init; }
    public int RetryAttempt { get; init; }
    public string? SessionId { get; init; }
    public string? MessagePreview { get; init; }
    public string? VerificationSummary { get; init; }
    public IReadOnlyList<VerificationCheckResult> VerificationChecks { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? LastDeliveredAtUtc { get; init; }
    public bool DeliverySuppressed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}

public sealed class AutomationTemplate
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string SuggestedName { get; init; } = "";
    public string Schedule { get; init; } = "@daily";
    public string Prompt { get; init; } = "";
    public string DeliveryChannelId { get; init; } = "cron";
    public string? DeliverySubject { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

public sealed class AutomationTemplateListResponse
{
    public IReadOnlyList<AutomationTemplate> Items { get; init; } = [];
}

public sealed class AutomationValidationIssue
{
    public string Severity { get; init; } = "error";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class AutomationPreview
{
    public required AutomationDefinition Definition { get; init; }
    public IReadOnlyList<AutomationValidationIssue> Issues { get; init; } = [];
    public IReadOnlyList<AutomationTemplate> Templates { get; init; } = [];
    public string PromptPreview { get; init; } = "";
    public int EstimatedRunsPerMonth { get; init; }
}
