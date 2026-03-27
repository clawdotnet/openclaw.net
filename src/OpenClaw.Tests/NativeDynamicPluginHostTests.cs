using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.TestPluginFixtures;
using Xunit;

namespace OpenClaw.Tests;

public sealed class NativeDynamicPluginHostTests : IDisposable
{
    private readonly string _tempDir;

    public NativeDynamicPluginHostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-native-dynamic-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadAsync_JitMode_LoadsNativeDynamicPluginAndSkills()
    {
        var startPath = Path.Combine(_tempDir, "service.start");
        var stopPath = Path.Combine(_tempDir, "service.stop");
        var pluginDir = CreateNativePlugin(
            "native-dynamic-fixture",
            typeof(ToolAndCommandPlugin).Assembly.Location,
            typeof(ToolAndCommandPlugin).FullName!,
            ["tools", "services", "commands"],
            includeSkills: true);

        var config = new NativeDynamicPluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new Dictionary<string, PluginEntryConfig>(StringComparer.Ordinal)
            {
                ["native-dynamic-fixture"] = new()
                {
                    Config = JsonSerializer.SerializeToElement(new { startPath, stopPath })
                }
            }
        };

        await using var host = new NativeDynamicPluginHost(
            config,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "jit" }, dynamicCodeSupported: true),
            new TestLogger());

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("native:hello", await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None));
        Assert.True(File.Exists(startPath));

        var sessionManager = new SessionManager(Substitute.For<IMemoryStore>(), new GatewayConfig());
        var processor = new ChatCommandProcessor(sessionManager);
        host.RegisterCommandsWith(processor);
        var session = new Session
        {
            Id = "session-1",
            ChannelId = "test",
            SenderId = "user"
        };

        var (handled, response) = await processor.TryProcessCommandAsync(session, "/native_dynamic_echo hi", CancellationToken.None);
        Assert.True(handled);
        Assert.Equal("cmd:hi", response);

        var report = Assert.Single(host.Reports, r => r.PluginId == "native-dynamic-fixture" && r.Loaded);
        Assert.Equal("native_dynamic", report.Origin);
        Assert.Contains(PluginCapabilityPolicy.NativeDynamic, report.RequestedCapabilities);
        Assert.Contains(PluginCapabilityPolicy.Commands, report.RequestedCapabilities);
        Assert.Contains(PluginCapabilityPolicy.Skills, report.RequestedCapabilities);

        var skills = SkillLoader.LoadAll(new SkillsConfig(), null, new TestLogger(), host.SkillRoots);
        Assert.Contains(skills, s => s.Name == "native-fixture-skill");

        await host.DisposeAsync();
        Assert.True(File.Exists(stopPath));
    }

    [Fact]
    public async Task LoadAsync_AotMode_RejectsNativeDynamicPluginsBeforeLoad()
    {
        var pluginDir = CreateNativePlugin(
            "native-dynamic-blocked",
            typeof(ToolAndCommandPlugin).Assembly.Location,
            typeof(ToolAndCommandPlugin).FullName!,
            ["tools", "commands"]);

        var config = new NativeDynamicPluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        await using var host = new NativeDynamicPluginHost(
            config,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "aot" }, dynamicCodeSupported: true),
            new TestLogger());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(null, CancellationToken.None));

        Assert.Contains("AOT", ex.Message, StringComparison.OrdinalIgnoreCase);
        var report = Assert.Single(host.Reports, r => r.PluginId == "native-dynamic-blocked");
        Assert.False(report.Loaded);
        Assert.True(report.BlockedByRuntimeMode);
        Assert.Equal("aot", report.EffectiveRuntimeMode);
        Assert.Contains(report.Diagnostics, d => d.Code == "jit_mode_required");
        Assert.Contains(PluginCapabilityPolicy.NativeDynamic, report.RequestedCapabilities);
    }

    [Fact]
    public async Task LoadAsync_AssemblyPathOutsideRoot_IsRejected()
    {
        var pluginDir = Path.Combine(_tempDir, "native-dynamic-escape");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "openclaw.native-plugin.json"),
            $$"""
            {
              "id": "native-dynamic-escape",
              "name": "native-dynamic-escape",
              "version": "1.0.0",
              "assemblyPath": "../outside.dll",
              "typeName": {{JsonSerializer.Serialize(typeof(ToolAndCommandPlugin).FullName!)}}
            }
            """);

        var config = new NativeDynamicPluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        await using var host = new NativeDynamicPluginHost(
            config,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "jit" }, dynamicCodeSupported: true),
            new TestLogger());

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
        var report = Assert.Single(host.Reports);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "assembly_outside_root");
    }

    private string CreateNativePlugin(
        string id,
        string assemblyPath,
        string typeName,
        string[] capabilities,
        bool includeSkills = false)
    {
        var pluginDir = Path.Combine(_tempDir, id);
        Directory.CreateDirectory(pluginDir);
        var localAssemblyName = Path.GetFileName(assemblyPath);
        var localAssemblyPath = Path.Combine(pluginDir, localAssemblyName);
        File.Copy(assemblyPath, localAssemblyPath, overwrite: true);

        if (includeSkills)
        {
            var skillRoot = Path.Combine(pluginDir, "skills", "native-helper");
            Directory.CreateDirectory(skillRoot);
            File.WriteAllText(
                Path.Combine(skillRoot, "SKILL.md"),
                """
                ---
                name: native-fixture-skill
                description: skill from native dynamic plugin
                ---
                Use the native fixture plugin.
                """);
        }

        var manifest = $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "version": "1.0.0",
          "assemblyPath": {{JsonSerializer.Serialize(localAssemblyName)}},
          "typeName": {{JsonSerializer.Serialize(typeName)}},
          "capabilities": {{JsonSerializer.Serialize(capabilities)}}{{(includeSkills ? ",\n  \"skills\": [\"skills\"]" : "")}},
          "jitOnly": true
        }
        """;
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.native-plugin.json"), manifest);
        return pluginDir;
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
