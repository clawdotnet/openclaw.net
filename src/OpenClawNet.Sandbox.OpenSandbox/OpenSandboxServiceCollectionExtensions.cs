using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClawNet.Sandbox.OpenSandbox;

public static class OpenSandboxServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSandboxIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var sandboxSection = configuration.GetSection("OpenClaw").GetSection("Sandbox");
        if (!string.Equals(
                SandboxProviderNames.Normalize(sandboxSection["Provider"]),
                SandboxProviderNames.OpenSandbox,
                StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var options = new OpenSandboxOptions
        {
            Endpoint = sandboxSection["Endpoint"] ?? string.Empty,
            ApiKey = ResolveSecretRefOrValue(sandboxSection["ApiKey"]),
            DefaultTTL = int.TryParse(sandboxSection["DefaultTTL"], out var defaultTtl)
                ? defaultTtl
                : 300
        };

        services.AddSingleton(options);
        services.AddHttpClient(nameof(OpenSandboxToolSandbox), client =>
        {
            client.BaseAddress = options.GetApiBaseUri();
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IToolSandbox>(sp =>
            new OpenSandboxToolSandbox(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenSandboxToolSandbox)),
                sp.GetRequiredService<OpenSandboxOptions>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<OpenSandboxToolSandbox>>()));

        return services;
    }

    private static string? ResolveSecretRefOrValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase)
            ? SecretResolver.Resolve(value)
            : value;
    }
}
