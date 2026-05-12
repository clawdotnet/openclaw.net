using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
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
        services.AddSingleton<OpenClawA2AAgent>();
        services.AddSingleton<OpenClawA2AAgentHandler>();
        services.AddSingleton<OpenClawAgentCardFactory>();
        services.AddAIAgent(
            OpenClawA2ANames.AgentName,
            (sp, _) => sp.GetRequiredService<OpenClawA2AAgent>())
            .WithInMemorySessionStore()
            .AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
        services.AddKeyedSingleton<IAgentHandler>(
            OpenClawA2ANames.AgentName,
            (sp, _) => sp.GetRequiredService<OpenClawA2AAgentHandler>());
        services.AddKeyedSingleton<ITaskStore>(
            OpenClawA2ANames.AgentName,
            (_, _) => new InMemoryTaskStore());

        return services;
    }
}
#pragma warning restore MEAI001
