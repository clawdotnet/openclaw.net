using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenClaw.Channels;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Gateway.PromptCaching;
using OpenClaw.Core.Validation;
using OpenClaw.PluginKit;
using TickerQ.DependencyInjection;

namespace OpenClaw.Gateway.Composition;

internal static class CoreServicesExtensions
{
    public static IServiceCollection AddOpenClawCoreServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var config = startup.Config;

        // TickerQ requires IConfiguration. WebApplicationBuilder already registers one,
        // so this is a no-op in production (TryAddSingleton respects existing registrations).
        // Tests that compose services from a bare ServiceCollection rely on this fallback.
        services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        services.AddSingleton(startup);
        services.AddSingleton(config);
        services.AddSingleton(config.Learning);
        services.AddSingleton(typeof(AllowlistSemantics), AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics));
        services.AddSingleton(sp =>
            new RecentSendersStore(config.Memory.StoragePath, sp.GetRequiredService<ILogger<RecentSendersStore>>()));
        services.AddSingleton(sp =>
            new AllowlistManager(config.Memory.StoragePath, sp.GetRequiredService<ILogger<AllowlistManager>>()));

        services.AddSingleton<RuntimeMetrics>();
        services.AddSingleton<IMemoryStore>(sp => CreateMemoryStore(
            startup,
            config,
            sp.GetRequiredService<RuntimeMetrics>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("MemoryStore"),
            ResolveStartupCancellationToken(sp),
            ResolveBlockedPluginIds(sp)));
        services.AddSingleton<ISessionAdminStore>(sp =>
        {
            var memory = sp.GetRequiredService<IMemoryStore>();
            return memory as ISessionAdminStore
                ?? throw new InvalidOperationException($"{memory.GetType().Name} must implement ISessionAdminStore.");
        });
        services.AddSingleton<ISessionSearchStore>(sp => (ISessionSearchStore)sp.GetRequiredService<IMemoryStore>());
        AddFeatureStores(services, config);
        services.AddSingleton<ProviderUsageTracker>();
        services.AddSingleton<ToolUsageTracker>();
        services.AddSingleton<ProviderSmokeRegistry>();
        services.AddSingleton<StartupNoticeCollector>();
        services.AddSingleton<IStartupNoticeSink>(sp => sp.GetRequiredService<StartupNoticeCollector>());
        services.AddSingleton(sp => new SetupVerificationSnapshotStore(config.Memory.StoragePath));
        services.AddSingleton(sp => new ToolAuditLog(
            Path.Combine(Path.GetFullPath(config.Memory.StoragePath), "audit", "tool-audit.jsonl"),
            sp.GetRequiredService<ILogger<ToolAuditLog>>()));
        services.AddSingleton<LlmProviderRegistry>();
        services.AddSingleton<ConfiguredModelProfileRegistry>();
        services.AddSingleton<IModelProfileRegistry>(sp => sp.GetRequiredService<ConfiguredModelProfileRegistry>());
        services.AddSingleton<IModelSelectionPolicy, DefaultModelSelectionPolicy>();
        services.AddSingleton<ModelEvaluationRunner>();
        services.AddSingleton<PromptCacheTraceWriter>();
        services.AddSingleton<PromptCacheCoordinator>();
        services.AddSingleton<PromptCacheWarmRegistry>();
        services.AddSingleton<ProviderPolicyService>(sp =>
            new ProviderPolicyService(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ProviderPolicyService>>()));
        services.AddSingleton<SessionMetadataStore>(sp =>
            new SessionMetadataStore(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<SessionMetadataStore>>()));
        services.AddSingleton(sp => new MediaCacheStore(config.Multimodal.MediaCachePath));
        services.AddSingleton<GeminiMultimodalService>();
        services.AddSingleton<GeminiAudioTranscriptionProvider>();
        services.AddSingleton<IAudioTranscriptionProvider>(sp => sp.GetRequiredService<GeminiAudioTranscriptionProvider>());
        services.AddSingleton<AudioTranscriptionService>();
        services.AddSingleton<GeminiLiveProxyService>();
        services.AddSingleton<ILiveSessionProvider>(sp => sp.GetRequiredService<GeminiLiveProxyService>());
        services.AddSingleton<GeminiTextToSpeechProvider>();
        services.AddSingleton<ITextToSpeechProvider>(sp => sp.GetRequiredService<GeminiTextToSpeechProvider>());
        services.AddSingleton<ElevenLabsTextToSpeechProvider>();
        services.AddSingleton<ITextToSpeechProvider>(sp => sp.GetRequiredService<ElevenLabsTextToSpeechProvider>());
        services.AddSingleton<TextToSpeechService>();
        services.AddSingleton<LiveSessionService>();
        services.AddSingleton<ToolPresetResolver>();
        services.AddSingleton<IToolPresetResolver>(sp => sp.GetRequiredService<ToolPresetResolver>());
        services.AddSingleton(sp =>
            new ToolExecutionRouter(
                config,
                sp.GetService<IToolSandbox>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ToolExecutionRouter>()));
        services.AddSingleton<ExecutionProcessService>(sp =>
        {
            var svc = new ExecutionProcessService(
                sp.GetRequiredService<ToolExecutionRouter>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<ExecutionProcessService>(),
                sp.GetService<RuntimeMetrics>());
            var eventStore = sp.GetService<RuntimeEventStore>();
            if (eventStore is not null)
            {
                svc.OnRuntimeEvent = (component, action, summary) =>
                    eventStore.Append(new RuntimeEventEntry
                    {
                        Id = $"evt_{Guid.NewGuid():N}"[..20],
                        Component = component,
                        Action = action,
                        Summary = summary,
                        Severity = action is "failed" or "timed_out" ? "warning" : "info"
                    });
            }

            return svc;
        });
        services.AddSingleton<HeartbeatService>();
        services.AddTickerQ();
        services.AddSingleton<CronSchedulerTickerFunction>();
        services.AddSingleton<AutomationRunCoordinator>();
        services.AddSingleton<IAutomationRunDispatcher>(sp => sp.GetRequiredService<AutomationRunCoordinator>());
        services.AddSingleton<GatewayAutomationService>();
        services.AddSingleton<LearningService>();
        services.AddSingleton<ICronJobSource, GatewayCronJobSource>();
        services.AddSingleton<ActorRateLimitService>(sp =>
            new ActorRateLimitService(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ActorRateLimitService>>()));
        services.AddSingleton(sp =>
            new SessionManager(
                sp.GetRequiredService<IMemoryStore>(),
                config,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("SessionManager"),
                sp.GetRequiredService<RuntimeMetrics>()));
        services.AddSingleton(sp => new MemoryRetentionSweeperService(
            config,
            sp.GetRequiredService<SessionManager>(),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<RuntimeMetrics>(),
            sp.GetRequiredService<ILogger<MemoryRetentionSweeperService>>(),
            sp.GetRequiredService<SessionMetadataStore>().GetAll));
        services.AddSingleton<IMemoryRetentionCoordinator>(sp => sp.GetRequiredService<MemoryRetentionSweeperService>());
        services.AddHostedService(sp => sp.GetRequiredService<MemoryRetentionSweeperService>());
        services.AddSingleton<MessagePipeline>();
        services.AddSingleton(sp =>
            new CronScheduler(
                sp.GetRequiredService<ICronJobSource>(),
                sp.GetRequiredService<ILogger<CronScheduler>>(),
                sp.GetRequiredService<IStartupNoticeSink>(),
                sp.GetRequiredService<MessagePipeline>().InboundWriter,
                sp.GetRequiredService<IAutomationRunDispatcher>()));
        services.AddSingleton<CronSchedulerStartupService>();
        services.AddHostedService(sp => sp.GetRequiredService<CronSchedulerStartupService>());
        services.AddSingleton(new WebSocketChannel(config.WebSocket));
        services.AddSingleton<CanvasCommandBroker>();
        services.AddSingleton<GatewayRuntimeShutdownCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayRuntimeShutdownCoordinator>());
        services.AddSingleton<ChatCommandProcessor>();
        services.AddSingleton<GatewayLlmExecutionService>();
        services.AddSingleton<PromptCacheWarmService>();
        services.AddHostedService(sp => sp.GetRequiredService<PromptCacheWarmService>());
        services.AddSingleton<SqliteEmbeddingBackfillService>();
        services.AddHostedService(sp => sp.GetRequiredService<SqliteEmbeddingBackfillService>());
        services.AddSingleton<IAgentRuntimeFactory, NativeAgentRuntimeFactory>();

        return services;
    }

    private static void AddFeatureStores(IServiceCollection services, GatewayConfig config)
    {
        if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<SqliteFeatureStore>(_ => new SqliteFeatureStore(ResolveSqliteDbPath(config)));
            services.AddSingleton<IAutomationStore>(sp => sp.GetRequiredService<SqliteFeatureStore>());
            services.AddSingleton<IUserProfileStore>(sp => sp.GetRequiredService<SqliteFeatureStore>());
            services.AddSingleton<ILearningProposalStore>(sp => sp.GetRequiredService<SqliteFeatureStore>());
            services.AddSingleton<IConnectedAccountStore>(sp => sp.GetRequiredService<SqliteFeatureStore>());
            services.AddSingleton<IBackendSessionStore>(sp => sp.GetRequiredService<SqliteFeatureStore>());
            return;
        }

        services.AddSingleton<FileFeatureStore>(_ => new FileFeatureStore(config.Memory.StoragePath));
        services.AddSingleton<IAutomationStore>(sp => sp.GetRequiredService<FileFeatureStore>());
        services.AddSingleton<IUserProfileStore>(sp => sp.GetRequiredService<FileFeatureStore>());
        services.AddSingleton<ILearningProposalStore>(sp => sp.GetRequiredService<FileFeatureStore>());
        services.AddSingleton<IConnectedAccountStore>(sp => sp.GetRequiredService<FileFeatureStore>());
        services.AddSingleton<IBackendSessionStore>(sp => sp.GetRequiredService<FileFeatureStore>());
    }

    private static string ResolveSqliteDbPath(GatewayConfig config)
    {
        var dbPath = config.Memory.Sqlite.DbPath;
        if (!Path.IsPathRooted(dbPath))
        {
            if (dbPath.Contains(Path.DirectorySeparatorChar) || dbPath.Contains(Path.AltDirectorySeparatorChar))
                dbPath = Path.Combine(Directory.GetCurrentDirectory(), dbPath);
            else
                dbPath = Path.Combine(config.Memory.StoragePath, dbPath);
        }

        return Path.GetFullPath(dbPath);
    }

    private static IMemoryStore CreateMemoryStore(
        GatewayStartupContext startup,
        GatewayConfig config,
        RuntimeMetrics metrics,
        ILogger logger,
        CancellationToken startupCancellationToken,
        IReadOnlyCollection<string> blockedPluginIds)
    {
        if (string.Equals(config.Memory.Provider, "mempalace", StringComparison.OrdinalIgnoreCase))
            return CreateDynamicNativeMemoryStore(startup, config, metrics, logger, startupCancellationToken, blockedPluginIds);

        if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var sqliteConfig = config.Memory.Sqlite;
            IEmbeddingGenerator<string, Embedding<float>>? embeddingGen = null;
            if (sqliteConfig.EnableVectors && !string.IsNullOrWhiteSpace(sqliteConfig.EmbeddingModel))
            {
                embeddingGen = LlmClientFactory.CreateEmbeddingGenerator(config.Llm, sqliteConfig.EmbeddingModel);
            }

            var store = new SqliteMemoryStore(
                ResolveSqliteDbPath(config),
                sqliteConfig.EnableFts,
                embeddingGenerator: embeddingGen,
                enableVectors: sqliteConfig.EnableVectors);

            return store;
        }

        return new FileMemoryStore(
            config.Memory.StoragePath,
            config.Memory.MaxCachedSessions ?? config.MaxConcurrentSessions,
            metrics: metrics);
    }

    private static IMemoryStore CreateDynamicNativeMemoryStore(
        GatewayStartupContext startup,
        GatewayConfig config,
        RuntimeMetrics metrics,
        ILogger logger,
        CancellationToken startupCancellationToken,
        IReadOnlyCollection<string> blockedPluginIds)
    {
        if (!config.Plugins.DynamicNative.Enabled)
        {
            throw new InvalidOperationException(
                "Memory.Provider 'mempalace' is provided by a JIT-only dynamic native plugin. " +
                "Enable OpenClaw:Plugins:DynamicNative:Enabled and load the OpenClaw.Plugins.Mempalace native plugin, or choose 'file' or 'sqlite'.");
        }

        var host = new NativeDynamicPluginHost(config.Plugins.DynamicNative, startup.RuntimeState, logger, blockedPluginIds);
        try
        {
            var providers = host.LoadMemoryProvidersAsync(startup.WorkspacePath, startupCancellationToken)
                .GetAwaiter()
                .GetResult();
            var provider = providers.FirstOrDefault(item => string.Equals(item.ProviderId, config.Memory.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider.Factory is null)
            {
                throw new InvalidOperationException(
                    "Memory.Provider 'mempalace' was requested, but no dynamic native memory provider registered 'mempalace'. " +
                    "Load the OpenClaw.Plugins.Mempalace native plugin via OpenClaw:Plugins:DynamicNative:Load:Paths.");
            }

            var memoryStore = provider.Factory(new NativeDynamicMemoryProviderContext
            {
                PluginId = provider.PluginId,
                ProviderId = provider.ProviderId,
                Config = provider.Config,
                GatewayConfig = config,
                Metrics = metrics,
                Logger = logger
            });

            startup.NativeDynamicPluginHost = host;
            return memoryStore;
        }
        catch
        {
            DisposeNativeDynamicPluginHostOnStartupFailure(host, logger);
            throw;
        }
    }

    private static void DisposeNativeDynamicPluginHostOnStartupFailure(NativeDynamicPluginHost host, ILogger logger)
    {
        try
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose dynamic native plugin host after memory provider startup failure");
        }
    }

    private static CancellationToken ResolveStartupCancellationToken(IServiceProvider services)
        => services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;

    private static IReadOnlyCollection<string> ResolveBlockedPluginIds(IServiceProvider services)
        => services.GetService<PluginHealthService>()?.GetBlockedPluginIds() ?? [];
}
