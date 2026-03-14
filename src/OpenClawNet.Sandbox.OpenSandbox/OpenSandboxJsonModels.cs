using System.Text.Json.Serialization;

namespace OpenClawNet.Sandbox.OpenSandbox;

internal sealed class OpenSandboxImageSpec
{
    public required string Uri { get; set; }
}

internal sealed class OpenSandboxCreateRequest
{
    public required OpenSandboxImageSpec Image { get; set; }
    public int Timeout { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

internal sealed class OpenSandboxCreateResponse
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
}

internal sealed class OpenSandboxExecRequest
{
    public required string Command { get; set; }
}

internal sealed class OpenSandboxExecResponse
{
    public int ExitCode { get; set; }

    [JsonPropertyName("stdOut")]
    public string StdOut { get; set; } = string.Empty;

    [JsonPropertyName("stdErr")]
    public string StdErr { get; set; } = string.Empty;
}

internal sealed class OpenSandboxRenewRequest
{
    public required string ExpiresAt { get; set; }
}

internal sealed class OpenSandboxRenewResponse
{
    public DateTimeOffset? ExpiresAt { get; set; }
}

