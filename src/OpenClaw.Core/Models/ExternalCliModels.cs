using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

public sealed class ExternalCliOptions
{
    public bool Enabled { get; set; } = false;
    public int DefaultTimeoutSeconds { get; set; } = 60;
    public int MaxStdoutBytes { get; set; } = 262_144;
    public int MaxStderrBytes { get; set; } = 65_536;
    public bool RedactSecrets { get; set; } = true;
    public bool AllowFreeformCommands { get; set; } = false;
    public bool RequireApprovalForMutatingCommands { get; set; } = true;
    public string[] Presets { get; set; } = [];
    public Dictionary<string, ExternalCliConnectorOptions> Connectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExternalCliConnectorOptions
{
    public bool Enabled { get; set; } = false;
    public string DisplayName { get; set; } = "";
    public string Executable { get; set; } = "";
    public string[] DefaultArgs { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public ExternalCliStatusCommandOptions? StatusCommand { get; set; }
    public ExternalCliStatusCommandOptions? VersionCommand { get; set; }
    public string DefaultOutputFormat { get; set; } = ExternalCliOutputFormat.Text;
    public bool RequiresApproval { get; set; } = false;
    public string[] RedactionRules { get; set; } = [];
    public Dictionary<string, ExternalCliCommandOptions> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExternalCliStatusCommandOptions
{
    public string[] Args { get; set; } = [];
    public int? TimeoutSeconds { get; set; }
}

public sealed class ExternalCliCommandOptions
{
    public string Description { get; set; } = "";
    public string[] ArgsTemplate { get; set; } = [];
    public Dictionary<string, ExternalCliParameterOptions> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowUnknownParameters { get; set; } = false;
    public string RiskLevel { get; set; } = ExternalCliRiskLevel.Low;
    public bool ReadOnly { get; set; } = true;
    public bool RequiresApproval { get; set; } = false;
    public bool SupportsDryRun { get; set; } = false;
    public string[] DryRunArgsTemplate { get; set; } = [];
    public string StructuredOutput { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public string[] RedactionRules { get; set; } = [];
    public string[] RequiredScopes { get; set; } = [];
    public string? RequiredIdentity { get; set; }
    public string[] Tags { get; set; } = [];
}

public sealed class ExternalCliParameterOptions
{
    public bool Required { get; set; } = false;
    public string Description { get; set; } = "";
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public string[] AllowedValues { get; set; } = [];
}

public static class ExternalCliRiskLevel
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Medium => Medium,
            High => High,
            _ => Low
        };
}

public static class ExternalCliOutputFormat
{
    public const string Json = "json";
    public const string Ndjson = "ndjson";
    public const string Csv = "csv";
    public const string Text = "text";
    public const string Table = "table";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Json => Json,
            Ndjson => Ndjson,
            Csv => Csv,
            Table => Table,
            _ => Text
        };
}

public sealed class ExternalCliPreviewRequest
{
    public string? Connector { get; init; }
    public string? Command { get; init; }
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("execute_dry_run")]
    public bool ExecuteDryRun { get; init; }
}

public sealed class ExternalCliExecuteRequest
{
    public string? Connector { get; init; }
    public string? Command { get; init; }
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("approved_fingerprint")]
    public string? ApprovedFingerprint { get; init; }
    [JsonPropertyName("approval_reason")]
    public string? ApprovalReason { get; init; }
}

public sealed class ExternalCliToolRequest
{
    public string Action { get; init; } = "list_connectors";
    public string? Connector { get; init; }
    public string? Command { get; init; }
    public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("execute_dry_run")]
    public bool ExecuteDryRun { get; init; }
    [JsonPropertyName("approved_fingerprint")]
    public string? ApprovedFingerprint { get; init; }
    [JsonPropertyName("approval_reason")]
    public string? ApprovalReason { get; init; }
}

public sealed class ExternalCliConnectorSummary
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; }
    public string Executable { get; init; } = "";
    public int CommandCount { get; init; }
}

public sealed class ExternalCliConnectorListResponse
{
    public IReadOnlyList<ExternalCliConnectorSummary> Items { get; init; } = [];
}

public sealed class ExternalCliPresetSummary
{
    public required string Id { get; init; }
    public required string Connector { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Commands { get; init; } = [];
}

public sealed class ExternalCliPresetListResponse
{
    public IReadOnlyList<ExternalCliPresetSummary> Items { get; init; } = [];
}

public sealed class ExternalCliCommandSummary
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string RiskLevel { get; init; } = ExternalCliRiskLevel.Low;
    public bool ReadOnly { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public bool SupportsDryRun { get; init; }
    public string StructuredOutput { get; init; } = ExternalCliOutputFormat.Text;
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class ExternalCliCommandListResponse
{
    public required string Connector { get; init; }
    public IReadOnlyList<ExternalCliCommandSummary> Items { get; init; } = [];
}

public sealed class ExternalCliCommandSchemaResponse
{
    public required string Connector { get; init; }
    public required string Command { get; init; }
    public string Description { get; init; } = "";
    public Dictionary<string, ExternalCliParameterOptions> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> RequiredParameters { get; init; } = [];
    public string RiskLevel { get; init; } = ExternalCliRiskLevel.Low;
    public bool ReadOnly { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public bool SupportsDryRun { get; init; }
    public string StructuredOutput { get; init; } = ExternalCliOutputFormat.Text;
}

public sealed class ExternalCliInvocationPreview
{
    public required string Connector { get; init; }
    public required string Command { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyList<string> RedactedArguments { get; init; } = [];
    public string RedactedCommandLine { get; init; } = "";
    public string RiskLevel { get; init; } = ExternalCliRiskLevel.Low;
    public bool ReadOnly { get; init; } = true;
    public bool RequiresApproval { get; init; }
    public bool SupportsDryRun { get; init; }
    public bool IsDryRun { get; init; }
    public string StructuredOutput { get; init; } = ExternalCliOutputFormat.Text;
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];
    public string? RequiredIdentity { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; }
    public string Fingerprint { get; init; } = "";
    public string ParametersHash { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ExternalCliPreviewResponse
{
    public required ExternalCliInvocationPreview Preview { get; init; }
    public ExternalCliExecutionResult? DryRunResult { get; init; }
}

public sealed class ExternalCliExecutionResult
{
    public required ExternalCliInvocationPreview Preview { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
    public bool TimedOut { get; init; }
    public double DurationMs { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public JsonElement? ParsedJson { get; init; }
    public string? ParseError { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ExternalCliConnectorStatus
{
    public required string Connector { get; init; }
    public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; }
    public string Executable { get; init; } = "";
    public bool ExecutableFound { get; init; }
    public string? ResolvedExecutablePath { get; init; }
    public string? Version { get; init; }
    public bool? Authenticated { get; init; }
    public string AuthenticationStatus { get; init; } = "unknown";
    public string? IdentitySummary { get; init; }
    public IReadOnlyList<string> GrantedScopes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset LastCheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ExternalCliAuditEntry
{
    public required string Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SessionId { get; init; } = "";
    public string ChannelId { get; init; } = "";
    public string SenderId { get; init; } = "";
    public string ActorId { get; init; } = "";
    public required string Connector { get; init; }
    public required string Command { get; init; }
    public required string Executable { get; init; }
    public string ArgsHash { get; init; } = "";
    public string RedactedArgsPreview { get; init; } = "";
    public string ParametersHash { get; init; } = "";
    public string? ApprovalId { get; init; }
    public string? ApprovalFingerprint { get; init; }
    public int ExitCode { get; init; }
    public double DurationMs { get; init; }
    public bool TimedOut { get; init; }
    public bool Failed { get; init; }
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
    public string RiskLevel { get; init; } = ExternalCliRiskLevel.Low;
    public bool ReadOnly { get; init; }
    public string? WorkingDirectory { get; init; }
}

public sealed class ExternalCliRuntimeEvent
{
    public string SessionId { get; init; } = "";
    public string ChannelId { get; init; } = "";
    public string SenderId { get; init; } = "";
    public string Action { get; init; } = "";
    public string Severity { get; init; } = "info";
    public string Summary { get; init; } = "";
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
}
