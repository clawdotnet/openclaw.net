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
    public IReadOnlyList<ChannelReadinessDto> ChannelReadiness { get; init; } = [];
    public IReadOnlyList<SetupArtifactStatusItem> Artifacts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
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
