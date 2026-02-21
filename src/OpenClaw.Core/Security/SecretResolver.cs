namespace OpenClaw.Core.Security;

/// <summary>
/// Centralized secret resolution. Supports:
/// <list type="bullet">
///   <item><c>env:VAR_NAME</c> — read from environment variable</item>
///   <item><c>raw:literal</c>  — literal value (not recommended for production)</item>
///   <item><c>bare string</c>  — treated as env var name, falls back to literal</item>
/// </list>
/// All tools and the gateway share this single implementation.
/// </summary>
public static class SecretResolver
{
    private const string EnvPrefix = "env:";
    private const string RawPrefix = "raw:";

    /// <summary>
    /// Resolve a secret reference to its actual value.
    /// Returns null when <paramref name="secretRef"/> is null/blank or the
    /// referenced environment variable is unset.
    /// </summary>
    public static string? Resolve(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        // Default: treat as env var name, fall back to literal
        return Environment.GetEnvironmentVariable(secretRef) ?? secretRef;
    }

    /// <summary>
    /// Returns true when the reference uses the <c>raw:</c> prefix.
    /// Used by the public-bind hardening check.
    /// </summary>
    public static bool IsRawRef(string? secretRef)
        => secretRef is not null && secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase);
}
