using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenClaw.Core.Security;
using OpenClaw.StrategosWorkflowHost.Adapters;

using Strategos.Abstractions;
using Strategos.Infrastructure.Selection;
using Strategos.Selection;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

/// <summary>
/// Single wiring point for the sidecar's Thompson Sampling selector surface,
/// mirroring <see cref="OntologyServerBootstrap"/>. The two methods split
/// along ASP.NET Core's DI / routing seam:
/// <list type="bullet">
///   <item><see cref="AddSelectorServer"/> runs against the service collection
///   and registers the selector, cache, mapper, receiver, and (when enabled)
///   the <see cref="SelectorBackedChatClient"/> that replaces the direct
///   <see cref="IChatClient"/> registration.</item>
///   <item><see cref="MapSelectorEventEndpoint"/> runs after the container is
///   built and exposes POST /runtime-events only when the selector is enabled.</item>
/// </list>
/// Off by default — when <c>Strategos:Selector:Enabled</c> is false, the
/// sidecar behaves exactly as it did before this feature shipped.
/// </summary>
public static class SelectorServerBootstrap
{
    /// <summary>Path the gateway pushes runtime events to.</summary>
    public const string EventEndpointPath = "/runtime-events";

    /// <summary>
    /// Binds <see cref="SelectorOptions"/> and registers the selector
    /// surface. When <see cref="SelectorOptions.Enabled"/> is true, the
    /// returned <see cref="SelectorOptions"/> carries the decorator; the
    /// caller is expected to use it instead of the bare
    /// <paramref name="defaultClient"/> when registering <see cref="IChatClient"/>.
    /// </summary>
    public static SelectorOptions AddSelectorServer(
        IServiceCollection services,
        IConfiguration configuration,
        IChatClient defaultClient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(defaultClient);

        services.Configure<SelectorOptions>(configuration.GetSection(SelectorOptions.SectionName));
        var options = configuration.GetSection(SelectorOptions.SectionName).Get<SelectorOptions>() ?? new SelectorOptions();

        // Always register the cache, mapper, and selector — even when disabled —
        // so swapping to Enabled at runtime costs nothing. The receiver only
        // mounts the endpoint when Enabled=true (see MapSelectorEventEndpoint).
        services.AddSingleton(_ => new RunIdAgentSelectionCache(options.CacheSize));
        services.AddSingleton<AgentOutcomeMapper>();

        services.AddSingleton<IAgentSelector>(sp =>
        {
            var beliefLogger = sp.GetService<ILogger<InMemoryBeliefStore>>()
                ?? NullLogger<InMemoryBeliefStore>.Instance;
            var selectorLogger = sp.GetService<ILogger<ThompsonSamplingAgentSelector>>()
                ?? NullLogger<ThompsonSamplingAgentSelector>.Instance;
            var beliefStore = new InMemoryBeliefStore(beliefLogger);
            return new ThompsonSamplingAgentSelector(
                beliefStore,
                new TaskCategoryClassifier(),
                selectorLogger,
                randomSeed: 42);
        });

        if (!options.Enabled)
        {
            return options;
        }

        // Wire the decorator: the sidecar's IChatClient registration will
        // resolve to SelectorBackedChatClient, with the supplied defaultClient
        // as the fallback. InnerClients carry any additional agent-specific
        // clients the operator registered.
        services.AddSingleton(sp => new SelectorBackedChatClient(
            sp.GetRequiredService<IAgentSelector>(),
            sp.GetRequiredService<RunIdAgentSelectionCache>(),
            defaultClient,
            BuildInnerClients(options, defaultClient),
            options,
            sp.GetService<ILogger<SelectorBackedChatClient>>()
                ?? NullLogger<SelectorBackedChatClient>.Instance));

        // Receiver needs the expected bearer token. SecretResolver handles
        // "env:VAR", "raw:LITERAL", and bare env-var-name forms.
        services.AddSingleton(sp => new GatewayEventReceiver(
            sp.GetRequiredService<AgentOutcomeMapper>(),
            sp.GetRequiredService<IAgentSelector>(),
            expectedBearerToken: SecretResolver.Resolve(options.Webhook.TokenSecret),
            logger: sp.GetService<ILogger<GatewayEventReceiver>>()
                ?? NullLogger<GatewayEventReceiver>.Instance));

        return options;
    }

    /// <summary>
    /// Maps POST /runtime-events to <see cref="GatewayEventReceiver"/>. No-op
    /// when the selector is disabled, so a curl to that path returns 404
    /// rather than 401.
    /// </summary>
    public static void MapSelectorEventEndpoint(IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetValue($"{SelectorOptions.SectionName}:Enabled", false))
        {
            return;
        }

        endpoints.MapPost(EventEndpointPath, async (
            HttpContext ctx,
            GatewayEventReceiver receiver,
            CancellationToken ct) =>
        {
            await receiver.HandleAsync(ctx, ct);
        });
    }

    private static IReadOnlyDictionary<string, IChatClient> BuildInnerClients(
        SelectorOptions options,
        IChatClient defaultClient)
    {
        // Start with the operator-supplied InnerClients (if any). When no
        // explicit map is given, every AvailableAgent id maps to the default
        // client — keeps the mock dev path simple (one LlmMode client
        // answering for every "agent" the selector picks).
        var result = new Dictionary<string, IChatClient>(StringComparer.Ordinal);
        foreach (var (id, client) in options.InnerClients)
        {
            result[id] = client;
        }

        if (result.Count == 0)
        {
            foreach (var id in options.AvailableAgents)
            {
                result[id] = defaultClient;
            }
        }

        return result;
    }
}