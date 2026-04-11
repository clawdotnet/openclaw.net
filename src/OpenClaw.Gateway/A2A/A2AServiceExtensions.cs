#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenClaw.Gateway.Mcp;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

internal static class A2AServiceExtensions
{
    public static IServiceCollection AddOpenClawA2AServices(this IServiceCollection services)
    {
        services.TryAddSingleton<GatewayRuntimeHolder>();
        services.AddSingleton<IOpenClawA2AExecutionBridge, OpenClawA2AExecutionBridge>();
        services.AddSingleton<OpenClawA2AAgentHandler>();
        services.AddSingleton<OpenClawAgentCardFactory>();
        services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        services.AddSingleton<ChannelEventNotifier>();
        services.AddSingleton<IA2ARequestHandler>(sp =>
        {
            var handler = sp.GetRequiredService<OpenClawA2AAgentHandler>();
            var store = sp.GetRequiredService<ITaskStore>();
            var notifier = sp.GetRequiredService<ChannelEventNotifier>();
            var logger = sp.GetRequiredService<ILogger<A2AServer>>();
            return new A2AServer(handler, store, notifier, logger);
        });

        return services;
    }
}
#endif
