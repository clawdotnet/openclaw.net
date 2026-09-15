using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public static class VaultServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawVaultSecrets(
        this IServiceCollection services, VaultSecurityOptions? vaultOptions)
    {
        // Consume the options already bound by the gateway as GatewayConfig.Security.Vault —
        // the same instance the validator checks. Re-binding here via ConfigurationBinder.Bind
        // would require dynamic code (AOT/trimming unfriendly) and could drift from the bound copy.
        if (vaultOptions is null || !vaultOptions.Enabled)
            return services; // No-op when vault disabled

        services.AddSingleton(vaultOptions);

        // Core abstractions. EnvRaw must be registered first: bare/env/raw refs
        // resolve through it; vault refs fall through to the vault provider.
        services.AddSingleton<ISecretProvider, EnvRawSecretProvider>();
        services.AddSingleton<ISecretProvider, VaultSecretProvider>();

        // Resolve token via the secret resolver facade (env:/raw:) at client construction.
        services.AddSingleton<IVaultClient>(sp =>
        {
            var token = SecretResolver.Resolve(vaultOptions.TokenRef)
                ?? throw new VaultAuthException("Vault token ref resolved to null.");
            var client = new VaultSharpClient(
                vaultOptions.Address ?? throw new VaultAuthException("Vault address missing."),
                token,
                string.IsNullOrEmpty(vaultOptions.Namespace) ? null : vaultOptions.Namespace,
                vaultOptions.Tls);
            return client;
        });

        services.AddSingleton<VaultRefCache>(sp =>
            new VaultRefCache(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<VaultRefCache>>(),
                vaultOptions.CacheTtl));

        services.AddSingleton<ISecretResolver>(sp =>
            new CompositeSecretResolver(
                sp.GetServices<ISecretProvider>(),
                sp.GetRequiredService<ILogger<CompositeSecretResolver>>()));

        services.AddHostedService<VaultRefPrewarmService>();

        return services;
    }
}
