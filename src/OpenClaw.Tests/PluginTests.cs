using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Agent.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public class PluginDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public PluginDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Discover_FindsPluginWithManifest()
    {
        // Arrange – create a plugin directory with manifest + entry
        var pluginDir = Path.Combine(_tempDir, "test-plugin");
        Directory.CreateDirectory(pluginDir);

        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"),
            """{"id":"test-plugin","name":"Test Plugin","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(pluginDir, "index.ts"), "export default function() {}");

        var config = new PluginsConfig { Load = new PluginLoadConfig { Paths = [_tempDir] } };

        // Act
        var discovered = PluginDiscovery.Discover(config);

        // Assert
        Assert.Single(discovered);
        Assert.Equal("test-plugin", discovered[0].Manifest.Id);
        Assert.EndsWith("index.ts", discovered[0].EntryPath);
    }

    [Fact]
    public void Discover_FindsStandaloneFile()
    {
        // Arrange – a bare .ts file with no manifest
        var extDir = Path.Combine(_tempDir, ".openclaw", "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "my-tool.ts"), "// tool code");

        var config = new PluginsConfig();

        // Act
        var discovered = PluginDiscovery.Discover(config, workspacePath: _tempDir);

        // Assert
        Assert.Single(discovered);
        Assert.Equal("my-tool", discovered[0].Manifest.Id);
    }

    [Fact]
    public void Discover_IgnoresDuplicateIds()
    {
        // Arrange – two plugins with same manifest id
        var dir1 = Path.Combine(_tempDir, "plugins", "a");
        var dir2 = Path.Combine(_tempDir, "plugins", "b");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        foreach (var dir in new[] { dir1, dir2 })
        {
            File.WriteAllText(Path.Combine(dir, "openclaw.plugin.json"),
                """{"id":"duplicate-id"}""");
            File.WriteAllText(Path.Combine(dir, "index.js"), "");
        }

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [Path.Combine(_tempDir, "plugins")] }
        };

        // Act
        var discovered = PluginDiscovery.Discover(config);

        // Assert
        Assert.Single(discovered);
    }

    [Fact]
    public void Filter_DenyWinsOverAllow()
    {
        var plugin = MakePlugin("blocked");
        var config = new PluginsConfig
        {
            Allow = ["blocked"],
            Deny = ["blocked"]
        };

        var result = PluginDiscovery.Filter([plugin], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_AllowRestrictsToNamedPlugins()
    {
        var alpha = MakePlugin("alpha");
        var beta = MakePlugin("beta");
        var config = new PluginsConfig { Allow = ["alpha"] };

        var result = PluginDiscovery.Filter([alpha, beta], config);

        Assert.Single(result);
        Assert.Equal("alpha", result[0].Manifest.Id);
    }

    [Fact]
    public void Filter_PerPluginEnabled_False_Excludes()
    {
        var plugin = MakePlugin("off-plugin");
        var config = new PluginsConfig
        {
            Entries = new(StringComparer.Ordinal)
            {
                ["off-plugin"] = new PluginEntryConfig { Enabled = false }
            }
        };

        var result = PluginDiscovery.Filter([plugin], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_SlotExclusivity()
    {
        var memA = MakePlugin("mem-a", kind: "memory");
        var memB = MakePlugin("mem-b", kind: "memory");

        var config = new PluginsConfig
        {
            Slots = new(StringComparer.Ordinal) { ["memory"] = "mem-b" }
        };

        var result = PluginDiscovery.Filter([memA, memB], config);

        Assert.Single(result);
        Assert.Equal("mem-b", result[0].Manifest.Id);
    }

    [Fact]
    public void Filter_SlotNone_ExcludesAll()
    {
        var memA = MakePlugin("mem-a", kind: "memory");

        var config = new PluginsConfig
        {
            Slots = new(StringComparer.Ordinal) { ["memory"] = "none" }
        };

        var result = PluginDiscovery.Filter([memA], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_EmptyAllow_PassesAll()
    {
        var a = MakePlugin("a");
        var b = MakePlugin("b");
        var config = new PluginsConfig(); // Allow = []

        var result = PluginDiscovery.Filter([a, b], config);

        Assert.Equal(2, result.Count);
    }

    private static DiscoveredPlugin MakePlugin(string id, string? kind = null)
        => new()
        {
            Manifest = new PluginManifest { Id = id, Kind = kind },
            RootPath = "/fake",
            EntryPath = "/fake/index.ts"
        };
}

public class BridgedPluginToolTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var reg = new PluginToolRegistration
        {
            Name = "greet",
            Description = "Greets the user",
            Parameters = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""").RootElement
        };

        var tool = new BridgedPluginTool(null!, "test-plugin", reg);

        Assert.Equal("greet", tool.Name);
        Assert.Equal("Greets the user", tool.Description);
        Assert.Contains("\"name\"", tool.ParameterSchema);
        Assert.False(tool.Optional);
    }

    [Fact]
    public void Constructor_SetsOptional()
    {
        var reg = new PluginToolRegistration
        {
            Name = "opt-tool",
            Description = "Optional tool",
            Parameters = JsonDocument.Parse("{}").RootElement,
            Optional = true
        };

        var tool = new BridgedPluginTool(null!, "test-plugin", reg);

        Assert.True(tool.Optional);
    }
}

public class PluginHostTests
{
    [Fact]
    public async Task LoadAsync_DisabledConfig_ReturnsEmpty()
    {
        var config = new PluginsConfig { Enabled = false };
        var logger = new TestLogger();
        var host = new PluginHost(config, "/nonexistent/bridge.mjs", logger);

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task LoadAsync_NoPluginsFound_ReturnsEmpty()
    {
        var config = new PluginsConfig { Enabled = true };
        var logger = new TestLogger();
        var host = new PluginHost(config, "/nonexistent/bridge.mjs", logger);

        // No workspace, no global extensions — should discover nothing
        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
    }

    /// <summary>Minimal ILogger for testing without DI.</summary>
    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { }
    }
}
