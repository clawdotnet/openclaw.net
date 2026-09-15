using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public static class VaultServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawVaultSecrets(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = new VaultSecurityOptions();
        config.GetSection("Security:Vault").Bind(opts);

        if (!opts.Enabled)
            return services; // No-op when vault disabled

        services.AddSingleton(opts);

        // Core abstractions. EnvRaw must be registered first: bare/env/raw refs
        // resolve through it; vault refs fall through to the vault provider.
        services.AddSingleton<ISecretProvider, EnvRawSecretProvider>();
        services.AddSingleton<ISecretProvider, VaultSecretProvider>();

        // Resolve token via the secret resolver facade (env:/raw:) at client construction.
        services.AddSingleton<IVaultClient>(sp =>
        {
            var token = SecretResolver.Resolve(opts.TokenRef)
                ?? throw new VaultAuthException("Vault token ref resolved to null.");
            var client = new VaultSharpClient(
                opts.Address ?? throw new VaultAuthException("Vault address missing."),
                token,
                string.IsNullOrEmpty(opts.Namespace) ? null : opts.Namespace,
                opts.Tls);
            return client;
        });

        services.AddSingleton<VaultRefCache>(sp =>
            new VaultRefCache(
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<VaultRefCache>>(),
                opts.CacheTtl));

        services.AddSingleton<ISecretResolver>(sp =>
            new CompositeSecretResolver(
                sp.GetServices<ISecretProvider>(),
                sp.GetRequiredService<ILogger<CompositeSecretResolver>>()));

        services.AddHostedService<VaultRefPrewarmService>();

        return services;
    }
}
