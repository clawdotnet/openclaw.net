using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(BackendAssistantMessageEvent), typeDiscriminator: "assistant_message")]
[JsonDerivedType(typeof(BackendStdoutOutputEvent), typeDiscriminator: "stdout_output")]
[JsonDerivedType(typeof(BackendStderrOutputEvent), typeDiscriminator: "stderr_output")]
[JsonDerivedType(typeof(BackendToolCallRequestedEvent), typeDiscriminator: "tool_call_requested")]
[JsonDerivedType(typeof(BackendShellCommandProposedEvent), typeDiscriminator: "shell_command_proposed")]
[JsonDerivedType(typeof(BackendShellCommandExecutedEvent), typeDiscriminator: "shell_command_executed")]
[JsonDerivedType(typeof(BackendPatchProposedEvent), typeDiscriminator: "patch_proposed")]
[JsonDerivedType(typeof(BackendPatchAppliedEvent), typeDiscriminator: "patch_applied")]
[JsonDerivedType(typeof(BackendFileReadEvent), typeDiscriminator: "file_read")]
[JsonDerivedType(typeof(BackendFileWriteEvent), typeDiscriminator: "file_write")]
[JsonDerivedType(typeof(BackendErrorEvent), typeDiscriminator: "error")]
[JsonDerivedType(typeof(BackendSessionCompletedEvent), typeDiscriminator: "session_completed")]
public abstract record BackendEvent
{
    public required string SessionId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? RawLine { get; init; }
}

public sealed record BackendAssistantMessageEvent : BackendEvent
{
    public required string Text { get; init; }
}

public sealed record BackendStdoutOutputEvent : BackendEvent
{
    public required string Text { get; init; }
}

public sealed record BackendStderrOutputEvent : BackendEvent
{
    public required string Text { get; init; }
}

public sealed record BackendToolCallRequestedEvent : BackendEvent
{
    public required string ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
}

public sealed record BackendShellCommandProposedEvent : BackendEvent
{
    public required string Command { get; init; }
}

public sealed record BackendShellCommandExecutedEvent : BackendEvent
{
    public required string Command { get; init; }
    public int? ExitCode { get; init; }
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
}

public sealed record BackendPatchProposedEvent : BackendEvent
{
    public string? Path { get; init; }
    public required string Patch { get; init; }
}

public sealed record BackendPatchAppliedEvent : BackendEvent
{
    public string? Path { get; init; }
    public string? Summary { get; init; }
}

public sealed record BackendFileReadEvent : BackendEvent
{
    public required string Path { get; init; }
}

public sealed record BackendFileWriteEvent : BackendEvent
{
    public required string Path { get; init; }
}

public sealed record BackendErrorEvent : BackendEvent
{
    public required string Message { get; init; }
}

public sealed record BackendSessionCompletedEvent : BackendEvent
{
    public int? ExitCode { get; init; }
    public string? Reason { get; init; }
}

public sealed class CodingBackendsConfig
{
    public bool Enabled { get; set; } = true;
    public CodingCliBackendConfig Codex { get; set; } = new()
    {
        BackendId = "codex-cli",
        Provider = "codex"
    };
    public CodingCliBackendConfig GeminiCli { get; set; } = new()
    {
        BackendId = "gemini-cli",
        Provider = "gemini-cli"
    };
    public CodingCliBackendConfig GitHubCopilotCli { get; set; } = new()
    {
        BackendId = "github-copilot-cli",
        Provider = "github-copilot-cli"
    };

    public IEnumerable<CodingCliBackendConfig> EnumerateConfiguredBackends()
    {
        yield return Codex;
        yield return GeminiCli;
        yield return GitHubCopilotCli;
    }
}

public sealed class CodingCliBackendConfig
{
    public bool Enabled { get; set; } = false;
    public string BackendId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? ExecutablePath { get; set; }
    public string[] Args { get; set; } = [];
    public string[] ProbeArgs { get; set; } = ["--help"];
    public int TimeoutSeconds { get; set; } = 600;
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public string? DefaultModel { get; set; }
    public bool RequireWorkspace { get; set; } = true;
    public string? DefaultWorkspacePath { get; set; }
    public bool ReadOnlyByDefault { get; set; }
    public bool WriteEnabled { get; set; } = true;
    public bool PreferStructuredOutput { get; set; } = true;
    public BackendCredentialSourceConfig Credentials { get; set; } = new();
}

public sealed class BackendCredentialSourceConfig
{
    public string? SecretRef { get; set; }
    public string? TokenFilePath { get; set; }
    public string? ConnectedAccountId { get; set; }
}

public sealed class ConnectedAccount
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public string? DisplayName { get; init; }
    public string SecretKind { get; init; } = ConnectedAccountSecretKind.ProtectedBlob;
    public string? SecretRef { get; init; }
    public string? EncryptedSecretJson { get; init; }
    public string? TokenFilePath { get; init; }
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; } = true;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ConnectedAccountSecretRef
{
    public string? SecretRef { get; init; }
    public string? TokenFilePath { get; init; }
    public string? ConnectedAccountId { get; init; }
}

public static class ConnectedAccountSecretKind
{
    public const string ProtectedBlob = "protected_blob";
    public const string SecretRef = "secret_ref";
    public const string TokenFile = "token_file";
}

public sealed class ConnectedAccountSecretPayload
{
    public required string Secret { get; init; }
}

public sealed class ResolvedBackendCredential
{
    public required string Provider { get; init; }
    public required string SourceKind { get; init; }
    public string? AccountId { get; init; }
    public string? DisplayName { get; init; }
    public string? Secret { get; init; }
    public string? TokenFilePath { get; init; }
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BackendDefinition
{
    public required string BackendId { get; init; }
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public string? ExecutablePath { get; init; }
    public string? DefaultModel { get; init; }
    public BackendCapabilities Capabilities { get; init; } = new();
    public BackendAccessPolicy AccessPolicy { get; init; } = new();
}

public sealed class BackendCapabilities
{
    public bool SupportsSessions { get; init; } = true;
    public bool SupportsInteractiveInput { get; init; } = true;
    public bool SupportsJsonEvents { get; init; }
    public bool SupportsStructuredStreaming { get; init; }
    public bool SupportsWorkspace { get; init; } = true;
    public bool SupportsReadOnlyMode { get; init; } = true;
    public bool SupportsWriteMode { get; init; } = true;
    public bool SupportsModelOverride { get; init; } = true;
}

public sealed class BackendAccessPolicy
{
    public bool ReadOnlyByDefault { get; init; }
    public bool WriteEnabled { get; init; } = true;
    public bool RequireWorkspace { get; init; } = true;
}

public sealed record BackendSessionHandle
{
    public required string BackendId { get; init; }
    public required string SessionId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BackendSessionRecord
{
    public required string SessionId { get; init; }
    public required string BackendId { get; init; }
    public required string Provider { get; init; }
    public string State { get; init; } = BackendSessionState.Pending;
    public string? OwnerSessionId { get; init; }
    public string? WorkspacePath { get; init; }
    public string? Model { get; init; }
    public bool ReadOnly { get; init; }
    public bool StructuredOutputEnabled { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long LastEventSequence { get; init; }
    public int? ExitCode { get; init; }
    public string? LastError { get; init; }
}

public static class BackendSessionState
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record StartBackendSessionRequest
{
    public required string BackendId { get; init; }
    public string? OwnerSessionId { get; init; }
    public string? WorkspacePath { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public bool? ReadOnly { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public ConnectedAccountSecretRef? CredentialSource { get; init; }
}

public sealed class BackendInput
{
    public string? Text { get; init; }
    public bool AppendNewline { get; init; } = true;
    public bool CloseInput { get; init; }
}

public sealed class BackendProbeRequest
{
    public string? WorkspacePath { get; init; }
    public string? Model { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public ConnectedAccountSecretRef? CredentialSource { get; init; }
}

public sealed class BackendProbeResult
{
    public required string BackendId { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ExitCode { get; init; }
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public double DurationMs { get; init; }
    public bool StructuredOutputSupported { get; init; }
}

public sealed class ConnectedAccountCreateRequest
{
    public required string Provider { get; init; }
    public string? DisplayName { get; init; }
    public string? SecretRef { get; init; }
    public string? Secret { get; init; }
    public string? TokenFilePath { get; init; }
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool? IsActive { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BackendCredentialResolutionRequest
{
    public string? Provider { get; init; }
    public string? BackendId { get; init; }
    public ConnectedAccountSecretRef? CredentialSource { get; init; }
}

public sealed class BackendCredentialResolutionResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool HasSecret { get; init; }
    public ResolvedBackendCredential? Credential { get; init; }
}

public sealed class IntegrationAccountsResponse
{
    public IReadOnlyList<ConnectedAccount> Items { get; init; } = [];
}

public sealed class IntegrationConnectedAccountResponse
{
    public ConnectedAccount? Account { get; init; }
}

public sealed class IntegrationBackendsResponse
{
    public IReadOnlyList<BackendDefinition> Items { get; init; } = [];
}

public sealed class IntegrationBackendResponse
{
    public BackendDefinition? Backend { get; init; }
}

public sealed class IntegrationBackendSessionResponse
{
    public BackendSessionRecord? Session { get; init; }
}

public sealed class IntegrationBackendEventsResponse
{
    public required string SessionId { get; init; }
    public long NextSequence { get; init; }
    public IReadOnlyList<BackendEvent> Items { get; init; } = [];
}
