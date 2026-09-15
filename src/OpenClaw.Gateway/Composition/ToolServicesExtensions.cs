using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Core.Skills.Meta;
using OpenClaw.McpApp;
using OpenClaw.Protocols.Mqtt.Tools;


namespace OpenClaw.Gateway.Composition;

internal static class ToolServicesExtensions
{
    public static IServiceCollection AddOpenClawToolServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        services.AddSingleton(sp =>
        {
            var registry = new NativePluginRegistry(
                startup.Config.Plugins.Native,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<NativePluginRegistry>(),
                startup.Config.Tooling);

            if (startup.Config.Plugins.Native.Mqtt.Enabled)
            {
                registry.RegisterExternalTool(new MqttTool(startup.Config.Plugins.Native.Mqtt), "mqtt");
                registry.RegisterExternalTool(new MqttPublishTool(startup.Config.Plugins.Native.Mqtt, startup.Config.Tooling), "mqtt");
            }

            // Single registration: resolve_capability lives here (external, like the
            // mqtt tools above) and reaches the runtime table via ResolvePreference —
            // no built-in duplicate to shadow it (#230, pinned by
            // ResolvePreference_IncludesResolveCapabilityFromNativeRegistry).
            registry.RegisterExternalTool(
                new ResolveCapabilityTool(sp.GetRequiredService<CapabilityProviderRegistry>()),
                pluginId: "agent.resolve-capability");

            return registry;
        });
        services.AddSingleton(sp =>
            new McpServerToolRegistry(
                startup.Config.Plugins.Mcp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpServerToolRegistry>()));

        services.AddSingleton<CapabilityBindingCache>();
        services.AddSingleton<ICapabilityInvalidationSink>(sp => sp.GetRequiredService<CapabilityBindingCache>());
        services.AddSingleton<LocalCapabilityProvider>(sp => new(() => sp.GetRequiredService<NativePluginRegistry>().Tools));
        services.AddSingleton<ICapabilityProvider>(sp => sp.GetRequiredService<LocalCapabilityProvider>());
#if OPENCLAW_NACOS
        services.AddSingleton<ICapabilityProvider>(sp => new OpenClaw.Adapters.Nacos.NacosCapabilityProvider(sp.GetRequiredService<McpServerToolRegistry>()));
#endif
#if OPENCLAW_NACOS_EVENTS
        OpenClaw.Adapters.Nacos.Events.NacosEventRegistration.Add(services, startup.Config.AdapterSettings);
#endif
        services.AddSingleton(sp => new CapabilityProviderRegistry(sp.GetServices<ICapabilityProvider>()));
        services.AddSingleton<CapabilitySlotExecutor>();

        // MCP App support — discovery and hosting
        services.AddOpenClawMcpAppServices(startup.Config.McpApps);

        return services;
    }
}
