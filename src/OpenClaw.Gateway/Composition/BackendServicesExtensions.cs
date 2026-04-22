using Microsoft.AspNetCore.DataProtection;
using OpenClaw.Core.Abstractions;
using OpenClaw.Gateway.Backends;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class BackendServicesExtensions
{
    public static IServiceCollection AddOpenClawBackendServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var rootedStorage = Path.IsPathRooted(startup.Config.Memory.StoragePath)
            ? startup.Config.Memory.StoragePath
            : Path.GetFullPath(startup.Config.Memory.StoragePath);
        var keysDir = new DirectoryInfo(Path.Combine(rootedStorage, "admin", "keys"));
        try
        {
            keysDir.Create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Memory.StoragePath '{rootedStorage}' is not writable. Failed to create '{keysDir.FullName}'.",
                ex);
        }

        services.AddDataProtection()
            .PersistKeysToFileSystem(keysDir)
            .SetApplicationName("OpenClaw.Gateway");

        services.AddSingleton<ConnectedAccountProtectionService>();
        services.AddSingleton<ConnectedAccountService>();
        services.AddSingleton<IBackendCredentialResolver, BackendCredentialResolver>();
        services.AddSingleton<BackendSessionEventStreamStore>();
        services.AddSingleton<CodingBackendProcessHost>();
        services.AddSingleton<ICodingAgentBackend, CodexCliBackend>();
        services.AddSingleton<ICodingAgentBackend, GeminiCliBackend>();
        services.AddSingleton<ICodingAgentBackend, GitHubCopilotCliBackend>();
        services.AddSingleton<ICodingAgentBackendRegistry, CodingAgentBackendRegistry>();
        services.AddSingleton<BackendSessionCoordinator>();

        return services;
    }
}
