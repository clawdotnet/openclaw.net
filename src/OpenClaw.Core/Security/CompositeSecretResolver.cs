using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// Routes <see cref="ResolveAsync(string?, CancellationToken)"/> calls to the
/// first <see cref="ISecretProvider"/> whose <see cref="ISecretProvider.CanResolve"/>
/// returns true. Order of registration is the precedence order.
/// </summary>
public sealed class CompositeSecretResolver : ISecretResolver
{
    private readonly IReadOnlyList<ISecretProvider> _providers;
    private readonly ILogger<CompositeSecretResolver> _logger;

    public CompositeSecretResolver(IEnumerable<ISecretProvider> providers, ILogger<CompositeSecretResolver> logger)
    {
        _providers = providers.ToArray();
        _logger = logger;
    }

    public async ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        foreach (var provider in _providers)
        {
            if (provider.CanResolve(secretRef))
                return await provider.ResolveAsync(secretRef, ct);
        }

        if (secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new VaultNotConfiguredException(
                "Vault secret reference was used but no vault provider is configured (Security.Vault.Enabled).");

        // No provider claimed it — defer to the first provider that could plausibly handle
        // bare/env/raw, otherwise return the literal as a last-resort fallback (mirrors legacy).
        _logger.LogDebug("No provider claimed ref of length {Length}; returning literal fallback.", secretRef.Length);
        return secretRef;
    }

    public string? Resolve(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        foreach (var provider in _providers)
        {
            if (provider.CanResolve(secretRef))
            {
                // EnvRawSecretProvider is fully sync; other providers must implement a sync path.
                // VaultSecretProvider throws SecretResolutionException on cache miss.
                if (provider is ISyncSecretProvider sync)
                    return sync.ResolveSync(secretRef);
                throw new SecretResolutionException(
                    $"Provider '{provider.Scheme}' is async-only; call ResolveAsync or pre-warm.");
            }
        }

        if (secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new VaultNotConfiguredException(
                "Vault secret reference was used but no vault provider is configured (Security.Vault.Enabled).");

        return secretRef;
    }

    public bool IsRawRef(string? secretRef) =>
        secretRef is not null && secretRef.StartsWith("raw:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Optional sync path for providers that can resolve without I/O. Providers may
/// use this for cache-only lookups and must never block on network I/O.
/// </summary>
public interface ISyncSecretProvider : ISecretProvider
{
    string? ResolveSync(string secretRef);
}
