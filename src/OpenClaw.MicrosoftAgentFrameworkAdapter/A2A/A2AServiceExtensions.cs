using A2A;
using A2A.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

/// <summary>
/// DI registration and endpoint mapping helpers that wire the OpenClaw
/// A2A server into the ASP.NET Core pipeline using the official
/// <c>A2A</c> and <c>A2A.AspNetCore</c> SDK packages.
/// </summary>
public static class A2AServiceExtensions
{
    /// <summary>
    /// Registers A2A protocol services into the DI container:
    /// <see cref="ITaskStore"/>, <see cref="OpenClawA2AAgentHandler"/>,
    /// <see cref="OpenClawAgentCardFactory"/>, and the <see cref="A2AServer"/>.
    /// </summary>
    public static IServiceCollection AddOpenClawA2AServices(this IServiceCollection services)
    {
        services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        services.AddSingleton<OpenClawA2AAgentHandler>();
        services.AddSingleton<OpenClawAgentCardFactory>();
        services.AddSingleton<ChannelEventNotifier>();

        services.AddSingleton<IA2ARequestHandler>(sp =>
        {
            var store = sp.GetRequiredService<ITaskStore>();
            var handler = sp.GetRequiredService<OpenClawA2AAgentHandler>();
            var notifier = sp.GetRequiredService<ChannelEventNotifier>();
            var logger = sp.GetRequiredService<ILogger<A2AServer>>();

            return new A2AServer(handler, store, notifier, logger);
        });

        return services;
    }
}
