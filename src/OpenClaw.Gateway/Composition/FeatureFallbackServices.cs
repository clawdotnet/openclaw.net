using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class FeatureFallbackServices
{
    public static FileFeatureStore CreateFallbackFeatureStore(GatewayStartupContext startup)
        => new(startup.Config.Memory.StoragePath);

    public static ISessionSearchStore ResolveSessionSearchStore(IServiceProvider services)
    {
        var memoryStore = services.GetRequiredService<IMemoryStore>();
        return services.GetService<ISessionSearchStore>()
            ?? memoryStore as ISessionSearchStore
            ?? EmptySessionSearchStore.Instance;
    }

    public static GatewayAutomationService ResolveAutomationService(
        GatewayStartupContext startup,
        IServiceProvider services,
        HeartbeatService heartbeat,
        FileFeatureStore fallbackFeatureStore)
        => services.GetService<GatewayAutomationService>()
           ?? new GatewayAutomationService(
               startup.Config,
               services.GetService<IAutomationStore>() ?? fallbackFeatureStore,
               heartbeat,
               services.GetService<AutomationRunCoordinator>()
               ?? new AutomationRunCoordinator(
                   services.GetService<IAutomationStore>() ?? fallbackFeatureStore,
                   services.GetService<ContractGovernanceService>()
                   ?? new ContractGovernanceService(
                       startup,
                       new ContractStore(startup.Config.Memory.StoragePath, NullLogger<ContractStore>.Instance),
                       new RuntimeEventStore(startup.Config.Memory.StoragePath, NullLogger<RuntimeEventStore>.Instance),
                       services.GetService<ProviderUsageTracker>() ?? new ProviderUsageTracker(),
                       NullLogger<ContractGovernanceService>.Instance),
                   NullLogger<AutomationRunCoordinator>.Instance));

    public static LearningService ResolveLearningService(
        GatewayStartupContext startup,
        IServiceProvider services,
        FileFeatureStore fallbackFeatureStore)
        => services.GetService<LearningService>()
           ?? new LearningService(
               startup.Config.Learning,
               services.GetService<ILearningProposalStore>() ?? fallbackFeatureStore,
               services.GetService<IUserProfileStore>() ?? fallbackFeatureStore,
               services.GetService<IAutomationStore>() ?? fallbackFeatureStore,
               ResolveSessionSearchStore(services),
               NullLogger<LearningService>.Instance);
}

internal sealed class EmptySessionSearchStore : ISessionSearchStore
{
    public static EmptySessionSearchStore Instance { get; } = new();

    public ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct)
        => ValueTask.FromResult(new SessionSearchResult
        {
            Query = query,
            Items = []
        });
}
