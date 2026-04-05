namespace OpenClaw.Core.Models;

/// <summary>
/// Configuration for the tool execution routing layer.
/// Routing is config-driven: any tool can be routed to any backend via the <see cref="Tools"/> dictionary.
/// V1 targets shell, code_exec, and browser tools; all other tools default to local execution.
/// </summary>
public sealed class ExecutionConfig
{
    public bool Enabled { get; set; } = true;
    public string DefaultBackend { get; set; } = "local";
    public Dictionary<string, ExecutionBackendProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = new ExecutionBackendProfileConfig
        {
            Type = ExecutionBackendType.Local
        }
    };
    public Dictionary<string, ExecutionToolRouteConfig> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration for a single execution backend profile.
/// <see cref="TimeoutSeconds"/> applies to all backend types including OpenSandbox.
/// </summary>
public sealed class ExecutionBackendProfileConfig
{
    public string Type { get; set; } = ExecutionBackendType.Local;
    public bool Enabled { get; set; } = true;
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Image { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 22;
    public string? Username { get; set; }
    public string? PrivateKeyPath { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? WorkspaceRoot { get; set; }
}

/// <summary>
/// Configures which backend handles a specific tool.
/// V1 scope targets shell, code_exec, and browser; additional tools may be routed as needed.
/// </summary>
public sealed class ExecutionToolRouteConfig
{
    public string Backend { get; set; } = "";
    public string? FallbackBackend { get; set; }
    public bool RequireWorkspace { get; set; } = true;
}

public static class ExecutionBackendType
{
    public const string Local = "local";
    public const string OpenSandbox = "opensandbox";
    public const string Docker = "docker";
    public const string Ssh = "ssh";
}

public sealed class ExecutionRequest
{
    public required string ToolName { get; init; }
    public required string BackendName { get; init; }
    public required string Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? LeaseKey { get; init; }
    public string? WorkingDirectory { get; init; }
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string? Template { get; init; }
    public int? TimeToLiveSeconds { get; init; }
    public bool RequireWorkspace { get; init; } = true;
}

public sealed class ExecutionResult
{
    public required string BackendName { get; init; }
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool FallbackUsed { get; init; }
    public double DurationMs { get; init; }
}

public sealed class ExecutionBackendCapabilities
{
    public bool SupportsOneShotCommands { get; init; } = true;
    public bool SupportsProcesses { get; init; }
    public bool SupportsPty { get; init; }
    public bool SupportsInteractiveInput { get; init; }
}

public sealed class ExecutionProcessStartRequest
{
    public required string ToolName { get; init; }
    public required string BackendName { get; init; }
    public required string OwnerSessionId { get; init; }
    public required string OwnerChannelId { get; init; }
    public required string OwnerSenderId { get; init; }
    public required string Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public int? TimeoutSeconds { get; init; }
    public bool Pty { get; init; }
    public string? Template { get; init; }
    public bool RequireWorkspace { get; init; } = true;
}

public sealed class ExecutionProcessHandle
{
    public required string ProcessId { get; init; }
    public required string BackendName { get; init; }
    public required string OwnerSessionId { get; init; }
    public required string OwnerChannelId { get; init; }
    public required string OwnerSenderId { get; init; }
    public string CommandPreview { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; init; } = DateTimeOffset.UtcNow.AddHours(1);
    public bool Pty { get; init; }
}

public sealed class ExecutionProcessStatus
{
    public required string ProcessId { get; init; }
    public required string BackendName { get; init; }
    public required string OwnerSessionId { get; init; }
    public string State { get; init; } = ExecutionProcessState.Running;
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Pty { get; init; }
    public int? NativeProcessId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public double DurationMs { get; init; }
    public long StdoutBytes { get; init; }
    public long StderrBytes { get; init; }
    public string CommandPreview { get; init; } = "";
}

public sealed class ExecutionProcessLogRequest
{
    public required string ProcessId { get; init; }
    public string? OwnerSessionId { get; init; }
    public int StdoutOffset { get; init; }
    public int StderrOffset { get; init; }
    public int MaxChars { get; init; } = 8_192;
}

public sealed class ExecutionProcessLogResult
{
    public required string ProcessId { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public int NextStdoutOffset { get; init; }
    public int NextStderrOffset { get; init; }
    public bool Completed { get; init; }
}

public sealed class ExecutionProcessInputRequest
{
    public required string ProcessId { get; init; }
    public string? OwnerSessionId { get; init; }
    public required string Data { get; init; }
}

public static class ExecutionProcessState
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Killed = "killed";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
}
