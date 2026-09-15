namespace OpenClaw.Core.Security;

/// <summary>
/// Resolves secrets for a single scheme ("env", "raw", "vault", ...).
/// Implementations must be safe to register as singletons.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Prefix this provider claims, without trailing colon (e.g. "vault", "env", "raw").</summary>
    string Scheme { get; }

    /// <summary>True if <paramref name="secretRef"/> matches this provider's scheme.</summary>
    bool CanResolve(string secretRef);

    /// <summary>Resolve the secret reference asynchronously.</summary>
    ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
