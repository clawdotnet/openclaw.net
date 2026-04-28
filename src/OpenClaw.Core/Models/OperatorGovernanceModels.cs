namespace OpenClaw.Core.Models;

public static class OperatorRoleNames
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";

    public static string Normalize(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            Viewer => Viewer,
            Operator => Operator,
            Admin => Admin,
            _ => Viewer
        };

    public static bool CanAccess(string grantedRole, string requiredRole)
        => Rank(Normalize(grantedRole)) >= Rank(Normalize(requiredRole));

    private static int Rank(string role)
        => role switch
        {
            Admin => 3,
            Operator => 2,
            _ => 1
        };
}

public static class OrganizationAuthModeNames
{
    public const string BootstrapToken = "bootstrap_token";
    public const string BrowserSession = "browser_session";
    public const string AccountToken = "account_token";
}

public sealed class OperatorIdentitySnapshot
{
    public string AuthMode { get; init; } = "unauthorized";
    public string Role { get; init; } = OperatorRoleNames.Viewer;
    public string? AccountId { get; init; }
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public bool IsBootstrapAdmin { get; init; }
}

public sealed class OperatorAccountSummary
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = OperatorRoleNames.Viewer;
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; init; }
    public int TokenCount { get; init; }
}

public sealed class OperatorAccountTokenSummary
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public string TokenPrefix { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed class OperatorAccountListResponse
{
    public IReadOnlyList<OperatorAccountSummary> Items { get; init; } = [];
}

public sealed class OperatorAccountDetailResponse
{
    public OperatorAccountSummary? Account { get; init; }
    public IReadOnlyList<OperatorAccountTokenSummary> Tokens { get; init; } = [];
}

public sealed class OperatorAccountCreateRequest
{
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public string Role { get; init; } = OperatorRoleNames.Viewer;
    public string? Password { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class OperatorAccountUpdateRequest
{
    public string? DisplayName { get; init; }
    public string? Role { get; init; }
    public string? Password { get; init; }
    public bool? Enabled { get; init; }
}

public sealed class OperatorAccountTokenCreateRequest
{
    public string? Label { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

public sealed class OperatorAccountTokenCreateResponse
{
    public OperatorAccountSummary? Account { get; init; }
    public OperatorAccountTokenSummary? TokenInfo { get; init; }
    public string Token { get; init; } = "";
}

public sealed class OrganizationPolicySnapshot
{
    public bool BootstrapTokenEnabled { get; init; } = true;
    public string[] AllowedAuthModes { get; init; } =
    [
        OrganizationAuthModeNames.BootstrapToken,
        OrganizationAuthModeNames.BrowserSession,
        OrganizationAuthModeNames.AccountToken
    ];
    public string MinimumPluginTrustLevel { get; init; } = "untrusted";
    public int ExportRetentionDays { get; init; } = 30;
    public bool RequireInteractiveAdminForHighRiskMutations { get; init; }
    public bool PublicDeploymentGuardrails { get; init; }
}

public sealed class OrganizationPolicyResponse
{
    public required OrganizationPolicySnapshot Policy { get; init; }
    public string Message { get; init; } = "";
}

public sealed class SetupArtifactStatusItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Path { get; init; }
    public bool Exists { get; init; }
    public string Status { get; init; } = "missing";
}

public static class SetupCheckStates
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Skip = "skip";
    public const string NotRun = "not_run";
}

public static class DoctorCheckCategories
{
    public const string Config = "config";
    public const string Workspace = "workspace";
    public const string Security = "security";
    public const string Browser = "browser";
    public const string Operator = "operator";
    public const string ModelDoctor = "model_doctor";
    public const string ProviderSmoke = "provider_smoke";
    public const string Runtime = "runtime";
    public const string Storage = "storage";
    public const string Network = "network";
    public const string Plugins = "plugins";
    public const string Channels = "channels";
}

public static class SetupVerificationSources
{
    public const string Cli = "cli";
    public const string Admin = "admin";
    public const string LaunchStartup = "launch-startup";
}

public static class ToolResultStatuses
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
}

public static class ToolFailureCodes
{
    public const string PresetBlocked = "preset_blocked";
    public const string OperatorAuthRequired = "operator_auth_required";
    public const string ApprovalRequired = "approval_required";
    public const string RuntimeCapabilityUnavailable = "runtime_capability_unavailable";
    public const string BrowserBackendMissing = "browser_backend_missing";
    public const string Timeout = "timeout";
    public const string ToolFailed = "tool_failed";
}

public sealed class BrowserToolCapabilitySummary
{
    public bool ConfiguredEnabled { get; init; }
    public bool LocalExecutionSupported { get; init; }
    public bool ExecutionBackendConfigured { get; init; }
    public bool Registered { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class SetupStatusResponse
{
    public string Profile { get; init; } = "local";
    public string BindAddress { get; init; } = "";
    public int Port { get; init; }
    public bool PublicBind { get; init; }
    public bool AuthTokenConfigured { get; init; }
    public bool BootstrapTokenEnabled { get; init; }
    public string[] AllowedAuthModes { get; init; } = [];
    public string MinimumPluginTrustLevel { get; init; } = "untrusted";
    public bool ReverseProxyRecommended { get; init; }
    public string ReachableBaseUrl { get; init; } = "";
    public string? WorkspacePath { get; init; }
    public bool WorkspaceExists { get; init; }
    public bool HasOperatorAccounts { get; init; }
    public int OperatorAccountCount { get; init; }
    public bool ProviderConfigured { get; init; }
    public string ProviderSmokeStatus { get; init; } = SetupCheckStates.NotRun;
    public string ModelDoctorStatus { get; init; } = SetupCheckStates.Skip;
    public bool BrowserToolRegistered { get; init; }
    public bool BrowserExecutionBackendConfigured { get; init; }
    public string BrowserCapabilityReason { get; init; } = "";
    public DateTimeOffset? LastVerificationAtUtc { get; init; }
    public string? LastVerificationSource { get; init; }
    public string LastVerificationStatus { get; init; } = SetupCheckStates.NotRun;
    public bool LastVerificationHasFailures { get; init; }
    public bool LastVerificationHasWarnings { get; init; }
    public string BootstrapGuidanceState { get; init; } = "not_applicable";
    public IReadOnlyList<string> RecommendedNextActions { get; init; } = [];
    public IReadOnlyList<ChannelReadinessDto> ChannelReadiness { get; init; } = [];
    public IReadOnlyList<SetupArtifactStatusItem> Artifacts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ReliabilitySnapshot Reliability { get; init; } = new();
}

public sealed class SetupVerificationCheck
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Category { get; init; } = DoctorCheckCategories.Config;
    public string Status { get; init; } = SetupCheckStates.Pass;
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string? NextStep { get; init; }
}

public sealed class SetupVerificationResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string OverallStatus { get; init; } = SetupCheckStates.Pass;
    public bool HasFailures { get; init; }
    public bool HasWarnings { get; init; }
    public bool HasSkips { get; init; }
    public IReadOnlyList<SetupVerificationCheck> Checks { get; init; } = [];
    public IReadOnlyList<string> RecommendedNextActions { get; init; } = [];
}

public sealed class SetupVerificationSnapshot
{
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = "";
    public bool Offline { get; init; }
    public bool RequireProvider { get; init; }
    public required SetupVerificationResponse Verification { get; init; }
}

public sealed class UpgradeRollbackSnapshotArtifact
{
    public required string Kind { get; init; }
    public required string TargetPath { get; init; }
    public bool Exists { get; init; }
    public bool IsDirectory { get; init; }
    public string? SnapshotRelativePath { get; init; }
}

public sealed class UpgradeRollbackSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string SnapshotId { get; init; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string CreatedByVersion { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public string? WorkspacePath { get; init; }
    public string VerificationStatus { get; init; } = SetupCheckStates.Pass;
    public bool Offline { get; init; }
    public bool RequireProvider { get; init; }
    public IReadOnlyList<UpgradeRollbackSnapshotArtifact> Artifacts { get; init; } = [];
}

public sealed class DoctorCheckItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Category { get; init; } = DoctorCheckCategories.Config;
    public string Status { get; init; } = SetupCheckStates.Pass;
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string? NextStep { get; init; }
}

public sealed class DoctorReportResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string OverallStatus { get; init; } = SetupCheckStates.Pass;
    public bool HasFailures { get; init; }
    public bool HasWarnings { get; init; }
    public bool HasSkips { get; init; }
    public IReadOnlyList<DoctorCheckItem> Checks { get; init; } = [];
    public IReadOnlyList<string> RecommendedNextActions { get; init; } = [];
}

public sealed class ObservabilityMetricPoint
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public int ApprovalDecisions { get; init; }
    public int ApprovalPending { get; init; }
    public int AutomationRuns { get; init; }
    public int AutomationFailures { get; init; }
    public int ProviderErrors { get; init; }
    public int ProviderRetries { get; init; }
    public int RuntimeWarnings { get; init; }
    public int RuntimeErrors { get; init; }
    public int DeadLetters { get; init; }
    public int ActiveSessions { get; init; }
    public int ChannelDrift { get; init; }
    public int OperatorActions { get; init; }
}

public sealed class ObservabilitySummaryCard
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Value { get; init; }
    public string Note { get; init; } = "";
}

public sealed class ObservabilitySummaryResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ObservabilitySummaryCard> Cards { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> ApprovalLatencyBuckets { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> ProviderErrorsByRoute { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> ProviderRetriesByRoute { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> OperatorActions { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> OperatorActionsByRole { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> OperatorActionsByAccount { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> ChannelDrift { get; init; } = [];
}

public sealed class OperatorInsightsResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset EndUtc { get; init; }
    public required OperatorInsightsTotals Totals { get; init; }
    public required OperatorInsightsSessionCounts Sessions { get; init; }
    public IReadOnlyList<OperatorInsightsProviderUsage> Providers { get; init; } = [];
    public IReadOnlyList<OperatorInsightsToolFrequency> Tools { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class OperatorInsightsTotals
{
    public long ProviderRequests { get; init; }
    public long ProviderErrors { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCostUsd { get; init; }
    public long ToolCalls { get; init; }
}

public sealed class OperatorInsightsSessionCounts
{
    public int Active { get; init; }
    public int Persisted { get; init; }
    public int UniqueTotal { get; init; }
    public int Last24Hours { get; init; }
    public int Last7Days { get; init; }
    public int InRange { get; init; }
    public IReadOnlyList<DashboardNamedMetric> ByChannel { get; init; } = [];
    public IReadOnlyList<DashboardNamedMetric> ByState { get; init; } = [];
}

public sealed class OperatorInsightsProviderUsage
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long Requests { get; init; }
    public long Retries { get; init; }
    public long Errors { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCostUsd { get; init; }
}

public sealed class OperatorInsightsToolFrequency
{
    public required string ToolName { get; init; }
    public long Calls { get; init; }
    public long Failures { get; init; }
    public long Timeouts { get; init; }
    public double AverageDurationMs { get; init; }
}

public sealed class ObservabilitySeriesResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset EndUtc { get; init; }
    public int BucketMinutes { get; init; } = 60;
    public IReadOnlyList<ObservabilityMetricPoint> Points { get; init; } = [];
}

public sealed class AuditExportManifest
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public OrganizationPolicySnapshot? Policy { get; init; }
    public int RetentionDays { get; init; }
    public long? OperatorAuditSequenceStart { get; init; }
    public long? OperatorAuditSequenceEnd { get; init; }
    public string? OperatorAuditPreviousEntryHash { get; init; }
    public string? OperatorAuditLastEntryHash { get; init; }
    public IReadOnlyDictionary<string, int>? FileEntryCounts { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class TrajectoryExportRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string Type { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public int TurnIndex { get; init; }
    public string? Role { get; init; }
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public long? DurationMs { get; init; }
    public string? ResultStatus { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public bool Anonymized { get; init; }
}

public sealed class UpstreamMigrationCompatibilityItem
{
    public required string Type { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public string Summary { get; init; } = "";
    public string[] Warnings { get; init; } = [];
}

public sealed class UpstreamMigrationSkillItem
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetSlug { get; init; }
    public string Status { get; init; } = "supported";
}

public sealed class UpstreamMigrationPluginItem
{
    public required string Subject { get; init; }
    public string? PackageSpec { get; init; }
    public string Status { get; init; } = "partial";
    public string[] Guidance { get; init; } = [];
}

public sealed class UpstreamMigrationReport
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourcePath { get; init; } = "";
    public string TargetConfigPath { get; init; } = "";
    public string? DiscoveredConfigPath { get; init; }
    public string? ManagedSkillRootPath { get; init; }
    public string? PluginReviewPlanPath { get; init; }
    public bool Applied { get; init; }
    public IReadOnlyList<UpstreamMigrationCompatibilityItem> Compatibility { get; init; } = [];
    public IReadOnlyList<UpstreamMigrationSkillItem> Skills { get; init; } = [];
    public IReadOnlyList<UpstreamMigrationPluginItem> Plugins { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string[] SkippedSettings { get; init; } = [];
}
