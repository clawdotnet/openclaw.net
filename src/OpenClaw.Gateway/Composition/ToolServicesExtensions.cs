using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Mcp.Nacos;
using OpenClaw.McpApp;
using OpenClaw.Protocols.Mqtt.Tools;
using RedNb.Nacos.DependencyInjection;

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
                new ResolveCapabilityTool(sp.GetRequiredService<McpServerToolRegistry>()),
                pluginId: "agent.resolve-capability");

            return registry;
        });
        services.AddSingleton(sp =>
            new McpServerToolRegistry(
                startup.Config.Plugins.Mcp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpServerToolRegistry>()));

        // Capability binding cache (#232): session-scoped dynamic bindings, cleared
        // on workspace MCP reload by McpWorkspaceWatcherService.
        services.AddSingleton<CapabilityBindingCache>();

        // Capability slot executor (#231): executes capability_ref steps in the
        // meta DAG through the same Router registry as resolve_capability.
        services.AddSingleton(sp =>
            new CapabilitySlotExecutor(
                sp.GetRequiredService<McpServerToolRegistry>(),
                sp.GetRequiredService<CapabilityBindingCache>()));

        // Nacos config event subscription (#238): the SDK client is only built
        // when a Nacos server address is configured; otherwise the in-memory
        // no-op double keeps the subscription service inert (TTL/reload
        // fallback from #232 stays the only invalidation path).
        var nacosOptions = startup.Config.Nacos;
        services.AddSingleton(nacosOptions);
        if (!string.IsNullOrWhiteSpace(nacosOptions.ServerAddr))
        {
            services.AddNacosConfig(o =>
            {
                o.ServerAddresses = nacosOptions.ServerAddr!;
                o.Username = nacosOptions.Username;
                o.Password = nacosOptions.Password;
                o.LongPollTimeout = nacosOptions.LongPollingTimeoutMs;
                o.DefaultTimeout = nacosOptions.LongPollingTimeoutMs;
            });
            services.AddSingleton(sp => new RedNbNacosConfigService(
                sp.GetRequiredService<RedNb.Nacos.Config.IConfigService>(),
                nacosOptions,
                sp.GetRequiredService<ILogger<RedNbNacosConfigService>>()));
            services.AddSingleton<INacosConfigService>(sp => sp.GetRequiredService<RedNbNacosConfigService>());
        }
        else
        {
            services.AddSingleton<INacosConfigService, FakeNacosConfigService>();
        }
        services.AddSingleton<IMcpWorkspaceReloadTrigger>(sp =>
            sp.GetRequiredService<McpWatcherHolder>().Watcher
            ?? throw new InvalidOperationException("McpWorkspaceWatcherService has not been started."));
        services.AddSingleton(sp => new NacosConfigSubscriptionService(
            sp.GetRequiredService<INacosConfigService>(),
            nacosOptions,
            sp.GetRequiredService<IMcpWorkspaceReloadTrigger>(),
            sp.GetRequiredService<ILogger<NacosConfigSubscriptionService>>()));

        // MCP App support — discovery and hosting
        services.AddOpenClawMcpAppServices(startup.Config.McpApps);

        return services;
    }
}
