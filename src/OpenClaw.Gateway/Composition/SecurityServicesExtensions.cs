using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class SecurityServicesExtensions
{
    public static IServiceCollection AddOpenClawSecurityServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        services.AddSingleton<ToolApprovalService>();
        services.AddSingleton(sp =>
            new PairingManager(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PairingManager>>()));
        services.AddSingleton(sp => new BrowserSessionAuthService(startup.Config));
        services.AddSingleton(sp =>
            new AdminSettingsService(
                startup.Config,
                AdminSettingsService.CreateSnapshot(startup.Config),
                AdminSettingsService.GetSettingsPath(startup.Config),
                sp.GetRequiredService<ILogger<AdminSettingsService>>()));
        services.AddSingleton(sp =>
            new ApprovalAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ApprovalAuditStore>>()));
        services.AddSingleton(sp =>
            new RuntimeEventStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<RuntimeEventStore>>()));
        services.AddSingleton(sp =>
            new OperatorAuditStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<OperatorAuditStore>>()));
        services.AddSingleton(sp =>
            new ToolApprovalGrantStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ToolApprovalGrantStore>>()));
        services.AddSingleton(sp =>
            new WebhookDeliveryStore(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<WebhookDeliveryStore>>()));
        services.AddSingleton(sp =>
            new PluginHealthService(
                startup.Config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<PluginHealthService>>()));

        return services;
    }
}
