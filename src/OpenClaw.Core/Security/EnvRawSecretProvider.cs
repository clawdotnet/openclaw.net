namespace OpenClaw.Core.Security;

/// <summary>
/// Resolves <c>env:</c>, <c>raw:</c>, and bare-string (treated as env var name
/// with literal fallback) secret references. Behavior is identical to the
/// legacy <see cref="SecretResolver"/> implementation.
/// </summary>
public sealed class EnvRawSecretProvider : ISecretProvider, ISyncSecretProvider
{
    private const string EnvPrefix = "env:";
    private const string RawPrefix = "raw:";

    public string Scheme => "env";

    public bool CanResolve(string secretRef) =>
        !string.IsNullOrWhiteSpace(secretRef) && (
            secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase) ||
            secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase) ||
            LooksLikeEnvVarName(secretRef));

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
        => new(ResolveInternal(secretRef));

    public string? ResolveSync(string secretRef) => ResolveInternal(secretRef);

    private static string? ResolveInternal(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        var envValue = Environment.GetEnvironmentVariable(secretRef);
        return envValue ?? secretRef;
    }

    private static bool LooksLikeEnvVarName(string value)
        => value.Length >= 3 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
}
