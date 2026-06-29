using OpenClaw.Agent.Plugins;
using OpenClaw.Gateway.Bootstrap;
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

            return registry;
        });
        services.AddSingleton(sp =>
            new McpServerToolRegistry(
                startup.Config.Plugins.Mcp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpServerToolRegistry>()));

        // MCP App support — discovery and hosting
        services.AddOpenClawMcpAppServices(startup.Config.McpApps);

        return services;
    }
}
