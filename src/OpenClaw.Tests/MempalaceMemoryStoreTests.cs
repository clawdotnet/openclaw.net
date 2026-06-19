#if OPENCLAW_ENABLE_MEMPALACE
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Plugins.Mempalace;
using OpenClaw.PluginKit;
using MemPalace.KnowledgeGraph;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MempalaceMemoryStoreTests : IAsyncLifetime
{
    private readonly string _storagePath = Path.Join(Path.GetTempPath(), "openclaw-mempalace-tests", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_storagePath);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_storagePath, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NativeDynamicPlugin_RegistersMempalaceMemoryProvider()
    {
        var pluginDir = Path.GetDirectoryName(typeof(MempalaceMemoryPlugin).Assembly.Location)
            ?? throw new InvalidOperationException("Could not resolve MemPalace plugin assembly directory.");

        Assert.True(File.Exists(Path.Join(pluginDir, "openclaw.native-plugin.json")));

        var pluginConfig = new NativeDynamicPluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        await using var host = new NativeDynamicPluginHost(
            pluginConfig,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "jit" }, dynamicCodeSupported: true),
            new TestLogger());

        var providers = await host.LoadMemoryProvidersAsync(null, TestContext.Current.CancellationToken);
        var provider = Assert.Single(providers, static item => string.Equals(item.ProviderId, "mempalace", StringComparison.OrdinalIgnoreCase));

        var store = provider.Factory(new NativeDynamicMemoryProviderContext
        {
            PluginId = provider.PluginId,
            ProviderId = provider.ProviderId,
            Config = provider.Config,
            GatewayConfig = CreateConfig(_storagePath),
            Metrics = new RuntimeMetrics(),
            Logger = new TestLogger()
        });

        await using var disposableStore = store as IAsyncDisposable;

        await store.SaveNoteAsync("project:demo:plugin", "Loaded through INativeDynamicPlugin.", TestContext.Current.CancellationToken);

        var tool = Assert.Single(host.Tools, static item => item.Name == "mempalace_kg");
        var toolResult = await tool.ExecuteAsync(
            """{"action":"query","subject":"memory:project:demo:plugin","predicate":"stored-in"}""",
            TestContext.Current.CancellationToken);

        Assert.Equal("Loaded through INativeDynamicPlugin.", await store.LoadNoteAsync("project:demo:plugin", TestContext.Current.CancellationToken));
        Assert.Contains("memory:project:demo:plugin stored-in drawer:plugin", toolResult, StringComparison.Ordinal);
        Assert.Contains(host.Reports, report => report.PluginId == "openclaw-mempalace-memory" && report.Loaded);
    }

    [Fact]
    public async Task Notes_RoundTripThroughMempalaceCollection()
    {
        await using var store = CreateStore();

        await store.SaveNoteAsync("project:demo:cats", "Cats prefer quiet sunny rooms.", TestContext.Current.CancellationToken);
        await store.SaveNoteAsync("project:demo:dogs", "Dogs enjoy daily walks.", TestContext.Current.CancellationToken);

        var loaded = await store.LoadNoteAsync("project:demo:cats", TestContext.Current.CancellationToken);
        var hits = await store.SearchNotesAsync("cats sunny", "project:demo:", 5, TestContext.Current.CancellationToken);
        var entries = await store.ListNotesAsync("project:demo:", 10, TestContext.Current.CancellationToken);

        Assert.Equal("Cats prefer quiet sunny rooms.", loaded);
        Assert.Contains(hits, hit => hit.Key == "project:demo:cats");
        Assert.Equal(
            ["project:demo:cats", "project:demo:dogs"],
            entries.Select(static entry => entry.Key).OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveNote_RecordsTemporalKnowledgeGraphLocation()
    {
        await using var store = CreateStore();

        await store.SaveNoteAsync("project:demo:cats", "Cats prefer quiet sunny rooms.", TestContext.Current.CancellationToken);

        var triples = await store.KnowledgeGraph.QueryAsync(
            new TriplePattern(
                new EntityRef("memory", "project:demo:cats"),
                "stored-in",
                null!),
            ct: TestContext.Current.CancellationToken);

        var triple = Assert.Single(triples);
        Assert.Equal("drawer:cats", triple.Triple.Object.ToString());
    }

    [Fact]
    public async Task KnowledgeGraphTool_AddsAndQueriesTemporalTriples()
    {
        await using var store = CreateStore();
        var tool = new MempalaceKnowledgeGraphTool(store.KnowledgeGraph);

        var add = await tool.ExecuteAsync(
            """{"action":"add","subject":"agent:openclaw","predicate":"uses","object":"memory:mempalace"}""",
            TestContext.Current.CancellationToken);
        var query = await tool.ExecuteAsync(
            """{"action":"query","subject":"agent:openclaw","predicate":"uses"}""",
            TestContext.Current.CancellationToken);

        Assert.Contains("Added temporal triple", add, StringComparison.Ordinal);
        Assert.Contains("agent:openclaw uses memory:mempalace", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeGraphTool_InvalidJson_ReturnsToolError()
    {
        await using var store = CreateStore();
        var tool = new MempalaceKnowledgeGraphTool(store.KnowledgeGraph);

        var result = await tool.ExecuteAsync("{", TestContext.Current.CancellationToken);

        Assert.StartsWith("Error: invalid JSON arguments.", result, StringComparison.Ordinal);
    }

    private MempalaceMemoryStore CreateStore()
        => new(CreateConfig(_storagePath), new RuntimeMetrics());

    private static GatewayConfig CreateConfig(string storagePath)
    {
        return new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Provider = "mempalace",
                StoragePath = storagePath,
                Mempalace = new MemoryMempalaceConfig
                {
                    BasePath = Path.Join(storagePath, "palace"),
                    SessionDbPath = Path.Join(storagePath, "sessions.db"),
                    KnowledgeGraphDbPath = Path.Join(storagePath, "kg.db"),
                    PalaceId = "test",
                    CollectionName = "memories",
                    EmbeddingDimensions = 64
                }
            }
        };
    }

    private sealed class TestLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { }
    }
}
#endif
