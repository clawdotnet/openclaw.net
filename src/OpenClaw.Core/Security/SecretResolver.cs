using Microsoft.Extensions.Logging;

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
        => Resolve(secretRef, logger: null);

    /// <summary>
    /// Resolve a secret reference to its actual value, logging a warning when a bare
    /// string that looks like an environment variable name falls back to a literal.
    /// </summary>
    public static string? Resolve(string? secretRef, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        // Default: treat as env var name, fall back to literal
        var envValue = Environment.GetEnvironmentVariable(secretRef);
        if (envValue is not null)
            return envValue;

        if (logger is not null && LooksLikeEnvVarName(secretRef))
            logger.LogWarning(
                "Secret ref '{SecretRef}' looks like an environment variable name but no such variable is set. " +
                "Using the literal value. Prefix with 'env:' for strict resolution.",
                secretRef);

        return secretRef;
    }

    /// <summary>
    /// Returns true when the reference uses the <c>raw:</c> prefix.
    /// Used by the public-bind hardening check.
    /// </summary>
    public static bool IsRawRef(string? secretRef)
        => secretRef is not null && secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeEnvVarName(string value)
        => value.Length >= 3 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
}
