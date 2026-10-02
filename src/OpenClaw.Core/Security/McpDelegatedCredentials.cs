using System.Text.Json.Serialization;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Core.Security;

public sealed class McpCallerCredentialContext(
    string oidcAccessToken,
    string subject,
    DateTimeOffset expiresAtUtc)
{
    [JsonIgnore]
    public string OidcAccessToken { get; } = oidcAccessToken;

    public string Subject { get; } = subject;

    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

    public override string ToString() => "McpCallerCredentialContext { OidcAccessToken = [REDACTED] }";
}

public sealed class McpDelegatedCredential(
    string accessToken,
    DateTimeOffset expiresAtUtc)
{
    [JsonIgnore]
    public string AccessToken { get; } = accessToken;

    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

    public override string ToString() => "McpDelegatedCredential { AccessToken = [REDACTED] }";
}

public sealed class McpDelegatedToolInvocationException(string failureCode) : InvalidOperationException(failureCode)
{
    public string FailureCode { get; } = failureCode;
}

public sealed class McpDelegatedToolCallRequest
{
    public required string EndpointId { get; init; }
    public required Uri Endpoint { get; init; }
    public IReadOnlyDictionary<string, string> StaticHeaders { get; init; } = new Dictionary<string, string>();
    public required int RequestTimeoutSeconds { get; init; }
    public required McpDelegatedCredentialsConfig Policy { get; init; }
    public required string RemoteToolName { get; init; }
    public required string ArgumentsJson { get; init; }

    [JsonIgnore]
    public McpCallerCredentialContext? CallerCredentialContext { get; init; }

    public bool SuppressStructuredContent { get; init; }
}

public sealed class McpDelegatedToolCallResult
{
    public required string ResponseText { get; init; }
    public required bool IsError { get; init; }
}

public interface IMcpDelegatedCredentialProvider
{
    Task<McpDelegatedCredential> GetCredentialAsync(
        McpDelegatedCredentialsConfig policy,
        McpCallerCredentialContext caller,
        CancellationToken ct);
}

public interface IMcpDelegatedToolInvoker
{
    Task<McpDelegatedToolCallResult> InvokeAsync(McpDelegatedToolCallRequest request, CancellationToken ct);
}