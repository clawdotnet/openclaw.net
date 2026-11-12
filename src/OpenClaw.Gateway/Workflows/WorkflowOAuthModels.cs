using System.Text.Json.Serialization;

namespace OpenClaw.Gateway.Workflows;

internal sealed class WorkflowOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }
    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; init; }
    [JsonPropertyName("issued_token_type")]
    public string? IssuedTokenType { get; init; }
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed class WorkflowTokenExchangeResult(string accessToken, DateTimeOffset exchangeStartedAt, DateTimeOffset acquiredAt, long expiresInSeconds)
{
    [JsonIgnore]
    public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExchangeStartedAt { get; } = exchangeStartedAt;
    public DateTimeOffset AcquiredAt { get; } = acquiredAt;
    public long ExpiresInSeconds { get; } = expiresInSeconds;
    public override string ToString() => "WorkflowTokenExchangeResult { [REDACTED] }";
}
