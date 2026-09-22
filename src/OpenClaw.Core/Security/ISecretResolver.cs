namespace OpenClaw.Core.Security;

/// <summary>
/// Composite secret resolver exposing both a sync facade (cache hit or throw)
/// and an async entry point. Backed by one or more <see cref="ISecretProvider"/>.
/// </summary>
public interface ISecretResolver
{
    ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default);

    /// <summary>
    /// Sync facade. For <c>env:</c>/<c>raw:</c>/bare refs returns synchronously.
    /// For <c>vault:</c> refs returns the cached value if present, otherwise throws
    /// <see cref="SecretResolutionException"/> (no sync-over-async, no deadlock risk).
    /// </summary>
    string? Resolve(string? secretRef);

    bool IsRawRef(string? secretRef);
}
