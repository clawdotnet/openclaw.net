using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Security;

public sealed class McpDelegatedCredentialProvider(
    HttpClient httpClient,
    ILogger<McpDelegatedCredentialProvider> logger) : IMcpDelegatedCredentialProvider
{
    private const string TokenExchangeMode = "token_exchange";
    private const string GatewaySignedMode = "gateway_signed";
    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    public async Task<McpDelegatedCredential> GetCredentialAsync(
        McpDelegatedCredentialsConfig policy,
        McpCallerCredentialContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        if (!policy.Enabled)
            throw new InvalidOperationException("Delegated credentials are not enabled for this endpoint.");
        if (string.Equals(policy.Mode, GatewaySignedMode, StringComparison.OrdinalIgnoreCase))
            return CreateGatewaySignedCredential(policy, caller);
        if (!string.Equals(policy.Mode, TokenExchangeMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The delegated credential mode is not supported.");
        if (!Uri.TryCreate(policy.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint) || tokenEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The MCP token exchange endpoint must be an absolute https URL.");
        if (string.IsNullOrWhiteSpace(policy.Audience) ||
            policy.Scopes is null ||
            policy.Scopes.Length == 0 ||
            policy.Scopes.Any(string.IsNullOrWhiteSpace) ||
            string.IsNullOrWhiteSpace(policy.ClientId))
        {
            throw new InvalidOperationException("The MCP token exchange policy is incomplete.");
        }

        var clientSecret = SecretResolver.Resolve(policy.ClientSecretRef);
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("The MCP token exchange client secret could not be resolved.");
        if (string.IsNullOrWhiteSpace(caller.OidcAccessToken))
            throw new InvalidOperationException("A caller OIDC access token is required for MCP token exchange.");

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", TokenExchangeGrantType),
            new KeyValuePair<string, string>("subject_token", caller.OidcAccessToken),
            new KeyValuePair<string, string>("subject_token_type", AccessTokenType),
            new KeyValuePair<string, string>("audience", policy.Audience),
            new KeyValuePair<string, string>("scope", string.Join(' ', policy.Scopes)),
            new KeyValuePair<string, string>("client_id", policy.ClientId),
            new KeyValuePair<string, string>("client_secret", clientSecret)
        ]);

        using var response = await httpClient.PostAsync(tokenEndpoint, content, ct);
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            logger.LogWarning("MCP delegated token exchange failed with HTTP status {StatusCode}.", (int)response.StatusCode);
            throw new InvalidOperationException($"MCP delegated token exchange failed with HTTP status {(int)response.StatusCode}.");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            logger.LogWarning("MCP delegated token exchange returned invalid JSON.");
            throw new InvalidOperationException("MCP delegated token exchange returned an invalid response.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var accessTokenElement) ||
                accessTokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(accessTokenElement.GetString()) ||
                !root.TryGetProperty("expires_in", out var expiresInElement) ||
                !expiresInElement.TryGetInt64(out var expiresIn) ||
                expiresIn <= 0)
            {
                logger.LogWarning("MCP delegated token exchange returned a response without a valid access token and expiry.");
                throw new InvalidOperationException("MCP delegated token exchange returned an invalid token response.");
            }

            DateTimeOffset expiresAtUtc;
            try
            {
                expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            }
            catch (ArgumentOutOfRangeException)
            {
                logger.LogWarning("MCP delegated token exchange returned an invalid token expiry.");
                throw new InvalidOperationException("MCP delegated token exchange returned an invalid token expiry.");
            }

            return new McpDelegatedCredential(accessTokenElement.GetString()!, expiresAtUtc);
        }
    }

    private static McpDelegatedCredential CreateGatewaySignedCredential(
        McpDelegatedCredentialsConfig policy,
        McpCallerCredentialContext caller)
    {
        if (policy.LifetimeSeconds < 1)
            throw new InvalidOperationException("The gateway-signed credential lifetime must be positive.");
        if (string.IsNullOrWhiteSpace(policy.Issuer) ||
            string.IsNullOrWhiteSpace(policy.Audience) ||
            policy.Scopes is null ||
            policy.Scopes.Length == 0 ||
            policy.Scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("The gateway-signed credential policy is incomplete.");
        }

        var signingSecret = SecretResolver.Resolve(policy.SigningKeyRef);
        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new InvalidOperationException("The gateway-signed credential signing key could not be resolved.");

        if (string.IsNullOrWhiteSpace(caller.OidcAccessToken) || string.IsNullOrWhiteSpace(caller.Subject))
            throw new InvalidOperationException("A verified caller credential with a subject is required for gateway signing.");

        var now = DateTimeOffset.UtcNow;
        var issuedAtSeconds = now.ToUnixTimeSeconds();
        var expiresAtSeconds = Math.Min(
            issuedAtSeconds + policy.LifetimeSeconds,
            caller.ExpiresAtUtc.ToUnixTimeSeconds());
        if (expiresAtSeconds <= issuedAtSeconds)
            throw new InvalidOperationException("The caller credential is expired or too close to expiry for gateway signing.");

        var keyBytes = Encoding.UTF8.GetBytes(signingSecret);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException("The gateway-signed credential signing key must be at least 32 bytes.");

        var issuedAtUtc = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
        var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = policy.Issuer,
            Audience = policy.Audience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = caller.Subject,
                ["scope"] = string.Join(' ', policy.Scopes)
            },
            IssuedAt = issuedAtUtc.UtcDateTime,
            Expires = expiresAtUtc.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keyBytes),
                SecurityAlgorithms.HmacSha256)
        };

        try
        {
            var token = new JsonWebTokenHandler().CreateToken(descriptor);
            return new McpDelegatedCredential(token, expiresAtUtc);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The gateway-signed credential could not be signed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}