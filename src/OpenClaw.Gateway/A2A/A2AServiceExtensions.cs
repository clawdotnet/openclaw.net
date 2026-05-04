#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.A2A;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenClaw.Gateway.Mcp;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

#pragma warning disable MEAI001
internal static class A2AServiceExtensions
{
    public static IServiceCollection AddOpenClawA2AServices(this IServiceCollection services)
    {
        services.TryAddSingleton<GatewayRuntimeHolder>();
        services.AddSingleton<IOpenClawA2AExecutionBridge, OpenClawA2AExecutionBridge>();
        services.AddSingleton<OpenClawA2AAgentHandler>();
        services.AddSingleton<OpenClawAgentCardFactory>();
        services.AddKeyedSingleton<AIAgent>(
            OpenClawA2ANames.AgentName,
            (_, _) => new OpenClawA2ARegistrationAgent());
        services.AddKeyedSingleton<IAgentHandler>(
            OpenClawA2ANames.AgentName,
            (sp, _) => sp.GetRequiredService<OpenClawA2AAgentHandler>());
        services.AddKeyedSingleton<ITaskStore>(
            OpenClawA2ANames.AgentName,
            (_, _) => new InMemoryTaskStore());
        services.AddA2AServer(OpenClawA2ANames.AgentName);

        return services;
    }
}
#pragma warning restore MEAI001
#endif
