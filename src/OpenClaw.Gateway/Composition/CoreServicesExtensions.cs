using Microsoft.Extensions.AI;
using OpenClaw.Channels;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.PromptCaching;

namespace OpenClaw.Gateway.Composition;

internal static class CoreServicesExtensions
{
    public static IServiceCollection AddOpenClawCoreServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var config = startup.Config;

        services.AddSingleton(config);
        services.AddSingleton(config.Learning);
        services.AddSingleton(typeof(AllowlistSemantics), AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics));
        services.AddSingleton(sp =>
            new RecentSendersStore(config.Memory.StoragePath, sp.GetRequiredService<ILogger<RecentSendersStore>>()));
        services.AddSingleton(sp =>
            new AllowlistManager(config.Memory.StoragePath, sp.GetRequiredService<ILogger<AllowlistManager>>()));

        services.AddSingleton<RuntimeMetrics>();
        services.AddSingleton<IMemoryStore>(sp => CreateMemoryStore(config, sp.GetRequiredService<RuntimeMetrics>()));
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
                sp.GetService<ILoggerFactory>()?.CreateLogger<ExecutionProcessService>());
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
        services.AddSingleton(new WebSocketChannel(config.WebSocket));
        services.AddSingleton<ChatCommandProcessor>();
        services.AddSingleton<GatewayLlmExecutionService>();
        services.AddSingleton<PromptCacheWarmService>();
        services.AddHostedService(sp => sp.GetRequiredService<PromptCacheWarmService>());
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

    private static IMemoryStore CreateMemoryStore(OpenClaw.Core.Models.GatewayConfig config, RuntimeMetrics metrics)
    {
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

            if (embeddingGen is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await store.BackfillEmbeddingsAsync(); }
                    catch { /* fire-and-forget */ }
                });
            }

            return store;
        }

        return new FileMemoryStore(
            config.Memory.StoragePath,
            config.Memory.MaxCachedSessions ?? config.MaxConcurrentSessions,
            metrics: metrics);
    }
}
