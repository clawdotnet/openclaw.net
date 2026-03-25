using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Http;

namespace OpenClaw.Gateway;

internal interface ITeamsTokenValidator
{
    Task<bool> ValidateAsync(string authHeader, string? serviceUrl, string? channelId, CancellationToken ct);
}

internal sealed class BotFrameworkTokenValidator : ITeamsTokenValidator, IAsyncDisposable
{
    private const string OpenIdMetadataUrl = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    private const string DefaultJwksUrl = "https://login.botframework.com/v1/.well-known/keys";
    private const string ExpectedIssuer = "https://api.botframework.com";
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly string _appId;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private SigningKeysSnapshot? _snapshot;

    public BotFrameworkTokenValidator(string appId, HttpClient? httpClient = null, ILogger? logger = null)
    {
        _appId = appId;
        _http = httpClient ?? HttpClientFactory.Create();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<bool> ValidateAsync(string authHeader, string? serviceUrl, string? channelId, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(authHeader) ||
                !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (!TryParseJwt(token, out var headerDoc, out var payloadDoc, out var signedBytes, out var signature))
                return false;

            using (headerDoc)
            using (payloadDoc)
            {
                if (!TryGetRequiredString(headerDoc.RootElement, "alg", out var algorithm) ||
                    !string.Equals(algorithm, "RS256", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Rejected Teams JWT with unsupported algorithm '{Algorithm}'.", algorithm);
                    return false;
                }

                if (!TryGetRequiredString(payloadDoc.RootElement, "iss", out var issuer) ||
                    !string.Equals(issuer, ExpectedIssuer, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Rejected Teams JWT with unexpected issuer '{Issuer}'.", issuer);
                    return false;
                }

                if (!TryGetRequiredString(payloadDoc.RootElement, "aud", out var audience) ||
                    !string.Equals(audience, _appId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected Teams JWT with unexpected audience '{Audience}'.", audience);
                    return false;
                }

                if (!TryGetUnixTime(payloadDoc.RootElement, "exp", out var expiresAt) ||
                    expiresAt < DateTimeOffset.UtcNow.Subtract(ClockSkew))
                {
                    _logger.LogWarning("Rejected Teams JWT because it is expired.");
                    return false;
                }

                if (TryGetUnixTime(payloadDoc.RootElement, "nbf", out var notBefore) &&
                    notBefore > DateTimeOffset.UtcNow.Add(ClockSkew))
                {
                    _logger.LogWarning("Rejected Teams JWT because it is not valid yet.");
                    return false;
                }

                if (!TryGetRequiredString(payloadDoc.RootElement, "serviceUrl", out var tokenServiceUrl) ||
                    string.IsNullOrWhiteSpace(serviceUrl) ||
                    !string.Equals(
                        tokenServiceUrl.TrimEnd('/'),
                        serviceUrl.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Rejected Teams JWT because serviceUrl claim did not match the incoming activity.");
                    return false;
                }

                var snapshot = await GetSigningKeysAsync(ct);
                if (!TryResolveKey(headerDoc.RootElement, snapshot.Keys, out var key))
                {
                    _logger.LogWarning("Rejected Teams JWT because no matching Bot Framework signing key was found.");
                    return false;
                }

                if (!IsChannelEndorsed(key, channelId))
                {
                    _logger.LogWarning("Rejected Teams JWT because the signing key is not endorsed for channel '{ChannelId}'.", channelId);
                    return false;
                }

                using var rsa = CreateRsa(key);
                if (rsa is null || !rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    _logger.LogWarning("Rejected Teams JWT because signature verification failed.");
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Teams JWT validation failed.");
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _metadataGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SigningKeysSnapshot> GetSigningKeysAsync(CancellationToken ct)
    {
        if (_snapshot is { } snapshotNow && snapshotNow.ExpiresAt > DateTimeOffset.UtcNow)
            return snapshotNow;

        await _metadataGate.WaitAsync(ct);
        try
        {
            if (_snapshot is { } snapshotLocked && snapshotLocked.ExpiresAt > DateTimeOffset.UtcNow)
                return snapshotLocked;

            var metadataResponse = await _http.GetAsync(OpenIdMetadataUrl, ct);
            metadataResponse.EnsureSuccessStatusCode();
            await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(ct);
            using var metadataDocument = await JsonDocument.ParseAsync(metadataStream, cancellationToken: ct);
            var jwksUrl = TryGetString(metadataDocument.RootElement, "jwks_uri");

            jwksUrl = string.IsNullOrWhiteSpace(jwksUrl) ? DefaultJwksUrl : jwksUrl;
            var keysResponse = await _http.GetAsync(jwksUrl, ct);
            keysResponse.EnsureSuccessStatusCode();
            await using var keysStream = await keysResponse.Content.ReadAsStreamAsync(ct);
            using var keysDocument = await JsonDocument.ParseAsync(keysStream, cancellationToken: ct);
            var keys = ParseSigningKeys(keysDocument.RootElement);

            _snapshot = new SigningKeysSnapshot(
                keys.Where(static key => !string.IsNullOrWhiteSpace(key.Kid) || !string.IsNullOrWhiteSpace(key.X5t)).ToArray(),
                DateTimeOffset.UtcNow.Add(MetadataTtl));
            return _snapshot;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private static bool TryParseJwt(
        string token,
        out JsonDocument header,
        out JsonDocument payload,
        out byte[] signedBytes,
        out byte[] signature)
    {
        header = null!;
        payload = null!;
        signedBytes = [];
        signature = [];

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return false;

            header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            signedBytes = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            signature = Base64UrlDecodeToBytes(parts[2]);
            return true;
        }
        catch
        {
            header?.Dispose();
            payload?.Dispose();
            return false;
        }
    }

    private static bool TryResolveKey(JsonElement header, IReadOnlyList<BotFrameworkJwk> keys, out BotFrameworkJwk key)
    {
        key = null!;
        var kid = TryGetString(header, "kid");
        var x5t = TryGetString(header, "x5t");

        foreach (var candidate in keys)
        {
            if (!string.IsNullOrWhiteSpace(kid) &&
                string.Equals(candidate.Kid, kid, StringComparison.Ordinal))
            {
                key = candidate;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(x5t) &&
                string.Equals(candidate.X5t, x5t, StringComparison.Ordinal))
            {
                key = candidate;
                return true;
            }
        }

        return false;
    }

    private static RSA? CreateRsa(BotFrameworkJwk key)
    {
        try
        {
            if (key.X5c is { Length: > 0 } chain && !string.IsNullOrWhiteSpace(chain[0]))
            {
                var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(chain[0]));
                return certificate.GetRSAPublicKey();
            }

            if (string.IsNullOrWhiteSpace(key.N) || string.IsNullOrWhiteSpace(key.E))
                return null;

            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64UrlDecodeToBytes(key.N),
                Exponent = Base64UrlDecodeToBytes(key.E)
            });
            return rsa;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsChannelEndorsed(BotFrameworkJwk key, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || key.Endorsements is not { Length: > 0 })
            return true;

        return key.Endorsements.Contains(channelId, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = TryGetString(element, propertyName) ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static bool TryGetUnixTime(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                long seconds;
                if (property.Value.ValueKind == JsonValueKind.Number)
                    seconds = property.Value.GetInt64();
                else if (property.Value.ValueKind == JsonValueKind.String && long.TryParse(property.Value.GetString(), out var parsed))
                    seconds = parsed;
                else
                    return false;

                value = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string Base64UrlDecode(string input) => Encoding.UTF8.GetString(Base64UrlDecodeToBytes(input));

    private static byte[] Base64UrlDecodeToBytes(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private sealed record SigningKeysSnapshot(IReadOnlyList<BotFrameworkJwk> Keys, DateTimeOffset ExpiresAt);

    internal sealed class BotFrameworkJwk
    {
        public string? Kid { get; set; }
        public string? X5t { get; set; }
        public string? N { get; set; }
        public string? E { get; set; }
        public string[]? X5c { get; set; }
        public string[]? Endorsements { get; set; }
    }

    private static BotFrameworkJwk[] ParseSigningKeys(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "keys", out var keysNode) || keysNode.ValueKind != JsonValueKind.Array)
            return [];

        var keys = new List<BotFrameworkJwk>();
        foreach (var keyNode in keysNode.EnumerateArray())
        {
            if (keyNode.ValueKind != JsonValueKind.Object)
                continue;

            keys.Add(new BotFrameworkJwk
            {
                Kid = TryGetString(keyNode, "kid"),
                X5t = TryGetString(keyNode, "x5t"),
                N = TryGetString(keyNode, "n"),
                E = TryGetString(keyNode, "e"),
                X5c = TryGetStringArray(keyNode, "x5c"),
                Endorsements = TryGetStringArray(keyNode, "endorsements")
            });
        }

        return [.. keys];
    }

    private static string[]? TryGetStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                items.Add(text);
        }

        return [.. items];
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
