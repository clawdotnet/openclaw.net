using Microsoft.Extensions.AI;
using OpenClaw.Channels;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway.Composition;

internal static class CoreServicesExtensions
{
    public static IServiceCollection AddOpenClawCoreServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var config = startup.Config;

        services.AddSingleton(config);
        services.AddSingleton(typeof(AllowlistSemantics), AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics));
        services.AddSingleton(sp =>
            new RecentSendersStore(config.Memory.StoragePath, sp.GetRequiredService<ILogger<RecentSendersStore>>()));
        services.AddSingleton(sp =>
            new AllowlistManager(config.Memory.StoragePath, sp.GetRequiredService<ILogger<AllowlistManager>>()));

        services.AddSingleton<IMemoryStore>(_ => CreateMemoryStore(config));
        services.AddSingleton<RuntimeMetrics>();
        services.AddSingleton<ProviderUsageTracker>();
        services.AddSingleton<ToolUsageTracker>();
        services.AddSingleton<LlmProviderRegistry>();
        services.AddSingleton<ProviderPolicyService>(sp =>
            new ProviderPolicyService(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ProviderPolicyService>>()));
        services.AddSingleton<SessionMetadataStore>(sp =>
            new SessionMetadataStore(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<SessionMetadataStore>>()));
        services.AddSingleton<HeartbeatService>();
        services.AddSingleton<ICronJobSource, GatewayCronJobSource>();
        services.AddSingleton<ActorRateLimitService>(sp =>
            new ActorRateLimitService(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ActorRateLimitService>>()));
        services.AddSingleton(sp =>
            new SessionManager(
                sp.GetRequiredService<IMemoryStore>(),
                config,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("SessionManager")));
        services.AddSingleton<MemoryRetentionSweeperService>();
        services.AddSingleton<IMemoryRetentionCoordinator>(sp => sp.GetRequiredService<MemoryRetentionSweeperService>());
        services.AddHostedService(sp => sp.GetRequiredService<MemoryRetentionSweeperService>());
        services.AddSingleton<MessagePipeline>();
        services.AddSingleton(new WebSocketChannel(config.WebSocket));
        services.AddSingleton<ChatCommandProcessor>();
        services.AddSingleton<GatewayLlmExecutionService>();
        services.AddSingleton<IAgentRuntimeFactory, NativeAgentRuntimeFactory>();

        services.AddOpenClawMcpSdkServices(startup);

        return services;
    }

    private static IMemoryStore CreateMemoryStore(OpenClaw.Core.Models.GatewayConfig config)
    {
        if (string.Equals(config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var dbPath = config.Memory.Sqlite.DbPath;
            if (!Path.IsPathRooted(dbPath))
            {
                if (dbPath.Contains(Path.DirectorySeparatorChar) || dbPath.Contains(Path.AltDirectorySeparatorChar))
                    dbPath = Path.Combine(Directory.GetCurrentDirectory(), dbPath);
                else
                    dbPath = Path.Combine(config.Memory.StoragePath, dbPath);
            }

            var sqliteConfig = config.Memory.Sqlite;
            IEmbeddingGenerator<string, Embedding<float>>? embeddingGen = null;
            if (sqliteConfig.EnableVectors && !string.IsNullOrWhiteSpace(sqliteConfig.EmbeddingModel))
            {
                embeddingGen = LlmClientFactory.CreateEmbeddingGenerator(config.Llm, sqliteConfig.EmbeddingModel);
            }

            var store = new SqliteMemoryStore(
                Path.GetFullPath(dbPath),
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
            config.Memory.MaxCachedSessions ?? config.MaxConcurrentSessions);
    }
}
