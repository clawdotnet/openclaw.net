namespace OpenClaw.Core.Models;

public enum ToolSandboxMode : byte
{
    None = 0,
    Prefer = 1,
    Require = 2
}

public sealed class SandboxExecutionRequest
{
    public required string Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string[] Arguments { get; set; } = [];
    public string? LeaseKey { get; set; }
    public string? Template { get; set; }
    public int? TimeToLiveSeconds { get; set; }
}

public sealed class SandboxResult
{
    public required int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
}

