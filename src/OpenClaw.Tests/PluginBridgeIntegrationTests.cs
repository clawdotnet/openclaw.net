using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.Tests;

public sealed class PluginBridgeIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public PluginBridgeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-plugin-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task MemoryProfile_ReportsBaselineVsCompatiblePlugins()
    {
        if (!HasNode()) return;

        var jsPluginDir = CreatePlugin(
            "memory-js-tool",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "memory_js_echo",
                description: "JS echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        var tsPluginDir = CreatePlugin(
            "memory-ts-tool",
            "index.ts",
            """
            export default function(api) {
              api.registerTool({
                name: "memory_ts_echo",
                description: "TS echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            }
            """);
        CreateFakeJiti(tsPluginDir);

        ForceGc();
        var baselineHost = CaptureHostMemory();

        var jsMeasurement = await MeasureBridgeMemoryAsync(jsPluginDir, "memory-js-tool");
        var tsMeasurement = await MeasureBridgeMemoryAsync(tsPluginDir, "memory-ts-tool");

        _output.WriteLine(
            $"Baseline host: working_set={ToMb(baselineHost.WorkingSetBytes):F1} MB private={ToMb(baselineHost.PrivateMemoryBytes):F1} MB");
        _output.WriteLine(
            $"JS plugin: host_delta_ws={ToMb(jsMeasurement.Host.WorkingSetBytes - baselineHost.WorkingSetBytes):F1} MB host_delta_private={ToMb(jsMeasurement.Host.PrivateMemoryBytes - baselineHost.PrivateMemoryBytes):F1} MB child_ws={ToMb(jsMeasurement.Child.WorkingSetBytes):F1} MB child_private={ToMb(jsMeasurement.Child.PrivateMemoryBytes):F1} MB");
        _output.WriteLine(
            $"TS plugin: host_delta_ws={ToMb(tsMeasurement.Host.WorkingSetBytes - baselineHost.WorkingSetBytes):F1} MB host_delta_private={ToMb(tsMeasurement.Host.PrivateMemoryBytes - baselineHost.PrivateMemoryBytes):F1} MB child_ws={ToMb(tsMeasurement.Child.WorkingSetBytes):F1} MB child_private={ToMb(tsMeasurement.Child.PrivateMemoryBytes):F1} MB");

        Assert.True(jsMeasurement.Child.WorkingSetBytes > 1_000_000, "Expected JS bridge child process memory usage to be measurable.");
        Assert.True(tsMeasurement.Child.WorkingSetBytes > 1_000_000, "Expected TS bridge child process memory usage to be measurable.");
        Assert.InRange(jsMeasurement.Child.WorkingSetBytes, 1_000_000, 256L * 1024 * 1024);
        Assert.InRange(tsMeasurement.Child.WorkingSetBytes, 1_000_000, 256L * 1024 * 1024);
        Assert.InRange(jsMeasurement.Host.WorkingSetBytes - baselineHost.WorkingSetBytes, -64L * 1024 * 1024, 128L * 1024 * 1024);
        Assert.InRange(tsMeasurement.Host.WorkingSetBytes - baselineHost.WorkingSetBytes, -64L * 1024 * 1024, 128L * 1024 * 1024);
    }

    [Fact]
    public async Task LoadAsync_JsPlugin_RegistersToolAndExecutes()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "js-tool",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "js_echo",
                description: "JS echo",
                parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                execute: async (_pluginId, params) => ({ content: [{ type: "text", text: `JS:${params.text}` }] })
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("JS:hello", result);
        Assert.Single(host.Reports, r => r.PluginId == "js-tool" && r.Loaded);
    }

    [Fact]
    public async Task LoadAsync_StandaloneMjsPlugin_IsDiscoveredFromWorkspaceExtensions()
    {
        if (!HasNode()) return;

        var workspace = Path.Combine(_tempDir, "workspace");
        var extensionsDir = Path.Combine(workspace, ".openclaw", "extensions");
        Directory.CreateDirectory(extensionsDir);
        File.WriteAllText(Path.Combine(extensionsDir, "hello.mjs"),
            """
            export default function(api) {
              api.registerTool({
                name: "mjs_echo",
                description: "MJS echo",
                parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                execute: async (_pluginId, params) => `MJS:${params.text}`
              });
            }
            """);

        await using var host = CreateHost(new PluginsConfig { Enabled = true });

        var tools = await host.LoadAsync(workspace, CancellationToken.None);

        var tool = Assert.Single(tools);
        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("MJS:hello", result);
    }

    [Fact]
    public async Task LoadAsync_TsPluginWithLocalJiti_LoadsSuccessfully()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "ts-tool",
            "index.ts",
            """
            export default function(api) {
              api.registerTool({
                name: "ts_echo",
                description: "TS echo",
                parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                execute: async (_pluginId, params) => ({ text: `TS:${params.text}` })
              });
            }
            """);
        CreateFakeJiti(pluginDir);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("TS:hello", result);
    }

    [Fact]
    public async Task LoadAsync_TsPluginWithoutJiti_FailsWithActionableError()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "ts-no-jiti",
            "index.ts",
            """
            export default function(api) {
              api.registerTool({
                name: "ts_missing_jiti",
                description: "TS tool",
                parameters: { type: "object", properties: {} },
                execute: async () => "nope"
              });
            }
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
        var report = Assert.Single(host.Reports, r => r.PluginId == "ts-no-jiti" && !r.Loaded);
        Assert.Contains("npm install jiti", report.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RegisterService_StartsAndStopsService()
    {
        if (!HasNode()) return;

        var startPath = Path.Combine(_tempDir, "service.start");
        var stopPath = Path.Combine(_tempDir, "service.stop");
        var pluginDir = CreatePlugin(
            "service-plugin",
            "index.js",
            $$"""
            const { writeFileSync } = require("node:fs");

            module.exports = function(api) {
              api.registerService({
                id: "svc",
                start: async () => writeFileSync({{ToJsString(startPath)}}, "started"),
                stop: async () => writeFileSync({{ToJsString(stopPath)}}, "stopped")
              });
              api.registerTool({
                name: "service_echo",
                description: "Service echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using (var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        }))
        {
            var tools = await host.LoadAsync(null, CancellationToken.None);
            Assert.Single(tools);
            Assert.True(File.Exists(startPath));
        }

        Assert.True(File.Exists(stopPath));
    }

    [Fact]
    public async Task LoadAsync_PluginSkills_AreLoadedAndWorkspaceWinsOnCollision()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "plugin-with-skills",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "plugin_skill_echo",
                description: "Echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """,
            manifestExtras: """
              ,
              "skills": ["skills"]
            """);
        var pluginSkillDir = Path.Combine(pluginDir, "skills", "shared-skill");
        Directory.CreateDirectory(pluginSkillDir);
        File.WriteAllText(Path.Combine(pluginSkillDir, "SKILL.md"),
            """
            ---
            name: shared-skill
            description: Plugin skill
            ---
            Use the plugin implementation.
            """);

        var workspaceDir = Path.Combine(_tempDir, "workspace");
        var workspaceSkillDir = Path.Combine(workspaceDir, "skills", "shared-skill");
        Directory.CreateDirectory(workspaceSkillDir);
        File.WriteAllText(Path.Combine(workspaceSkillDir, "SKILL.md"),
            """
            ---
            name: shared-skill
            description: Workspace skill
            ---
            Use the workspace implementation.
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });
        _ = await host.LoadAsync(workspaceDir, CancellationToken.None);

        var skillConfig = new SkillsConfig
        {
            Enabled = true,
            Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
        };
        var logger = new TestLogger();
        var skills = SkillLoader.LoadAll(skillConfig, workspaceDir, logger, host.SkillRoots);

        var skill = Assert.Single(skills);
        Assert.Equal("shared-skill", skill.Name);
        Assert.Equal("Workspace skill", skill.Description);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public async Task LoadAsync_ConfigSchema_ValidatesBeforeBridgeStartup()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "schema-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "schema_echo",
                description: "Schema echo",
                parameters: { type: "object", properties: {} },
                execute: async (_pluginId, params) => api.config.mode
              });
            };
            """,
            manifestExtras: """
              ,
              "configSchema": {
                "type": "object",
                "properties": {
                  "mode": { "type": "string", "enum": ["safe", "fast"] }
                },
                "required": ["mode"],
                "additionalProperties": false
              }
            """);

        await using var validHost = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new(StringComparer.Ordinal)
            {
                ["schema-plugin"] = new PluginEntryConfig
                {
                    Config = JsonDocument.Parse("""{"mode":"safe"}""").RootElement.Clone()
                }
            }
        });

        var validTools = await validHost.LoadAsync(null, CancellationToken.None);
        var validTool = Assert.Single(validTools);
        Assert.Equal("safe", await validTool.ExecuteAsync("{}", CancellationToken.None));

        await using var invalidHost = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new(StringComparer.Ordinal)
            {
                ["schema-plugin"] = new PluginEntryConfig
                {
                    Config = JsonDocument.Parse("""{"mode":"invalid"}""").RootElement.Clone()
                }
            }
        });

        var invalidTools = await invalidHost.LoadAsync(null, CancellationToken.None);
        Assert.Empty(invalidTools);
        var report = Assert.Single(invalidHost.Reports, r => r.PluginId == "schema-plugin" && !r.Loaded);
        Assert.Contains(report.Diagnostics, d => d.Code == "config_enum_mismatch");
    }

    [Fact]
    public async Task LoadAsync_ConfigSchemaOneOf_AndPluginConfigAlias_AreSupported()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "oneof-plugin",
            "index.js",
            """
            module.exports = {
              register(api) {
                api.registerTool({
                  name: "oneof_echo",
                  description: "OneOf echo",
                  parameters: { type: "object", properties: {} },
                  execute: async () => api.pluginConfig.answerMode
                });
              }
            };
            """,
            manifestExtras: """
              ,
              "configSchema": {
                "type": "object",
                "properties": {
                  "answerMode": {
                    "oneOf": [
                      { "type": "boolean" },
                      { "type": "string", "enum": ["basic", "advanced"] }
                    ]
                  }
                },
                "required": ["answerMode"],
                "additionalProperties": false
              }
            """);

        await using var validHost = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new(StringComparer.Ordinal)
            {
                ["oneof-plugin"] = new PluginEntryConfig
                {
                    Config = JsonDocument.Parse("""{"answerMode":"advanced"}""").RootElement.Clone()
                }
            }
        });

        var validTools = await validHost.LoadAsync(null, CancellationToken.None);
        var validTool = Assert.Single(validTools);
        Assert.Equal("advanced", await validTool.ExecuteAsync("{}", CancellationToken.None));

        await using var invalidHost = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new(StringComparer.Ordinal)
            {
                ["oneof-plugin"] = new PluginEntryConfig
                {
                    Config = JsonDocument.Parse("""{"answerMode":123}""").RootElement.Clone()
                }
            }
        });

        var invalidTools = await invalidHost.LoadAsync(null, CancellationToken.None);
        Assert.Empty(invalidTools);
        var report = Assert.Single(invalidHost.Reports, r => r.PluginId == "oneof-plugin" && !r.Loaded);
        Assert.Contains(report.Diagnostics, d => d.Code == "config_one_of_mismatch");
    }

    [Fact]
    public async Task LoadAsync_UnsupportedRegistration_FailsWithStructuredDiagnostics()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "unsupported-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerChannel({ id: "telegram" });
              api.registerTool({
                name: "unsupported_echo",
                description: "Should never load",
                parameters: { type: "object", properties: {} },
                execute: async () => "bad"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
        var report = Assert.Single(host.Reports, r => r.PluginId == "unsupported-plugin" && !r.Loaded);
        Assert.Contains(report.Diagnostics, d => d.Code == "unsupported_channel_registration");
    }

    [Fact]
    public async Task LoadAsync_DuplicateToolNames_AreReportedDeterministically()
    {
        if (!HasNode()) return;

        var root = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(root);
        CreatePlugin(
            "alpha",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "duplicate_tool",
                description: "Alpha",
                parameters: { type: "object", properties: {} },
                execute: async () => "alpha"
              });
            };
            """,
            rootOverride: Path.Combine(root, "alpha"));
        CreatePlugin(
            "beta",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "duplicate_tool",
                description: "Beta",
                parameters: { type: "object", properties: {} },
                execute: async () => "beta"
              });
            };
            """,
            rootOverride: Path.Combine(root, "beta"));

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [root] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Single(tools);
        Assert.Contains(host.Reports, r => r.Diagnostics.Any(d => d.Code == "duplicate_tool_name"));
    }

    [Fact]
    public void DiscoverWithDiagnostics_ManifestWithoutEntry_ProducesStructuredFailure()
    {
        var pluginDir = Path.Combine(_tempDir, "broken-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"),
            """{"id":"broken-plugin","name":"Broken"}""");

        var result = PluginDiscovery.DiscoverWithDiagnostics(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        Assert.Empty(result.Plugins);
        var report = Assert.Single(result.Reports);
        Assert.Equal("broken-plugin", report.PluginId);
        Assert.Contains(report.Diagnostics, d => d.Code == "entry_not_found");
    }

    private PluginHost CreateHost(PluginsConfig config)
        => new(config, GetBridgeScriptPath(), new TestLogger());

    private string CreatePlugin(
        string id,
        string entryFileName,
        string entryContent,
        string manifestExtras = "",
        string? rootOverride = null)
    {
        var pluginDir = rootOverride ?? Path.Combine(_tempDir, id);
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"),
            $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0"{{manifestExtras}}
            }
            """);
        File.WriteAllText(Path.Combine(pluginDir, entryFileName), entryContent);
        return pluginDir;
    }

    private static void CreateFakeJiti(string pluginDir)
    {
        var jitiDir = Path.Combine(pluginDir, "node_modules", "jiti", "dist");
        Directory.CreateDirectory(jitiDir);
        File.WriteAllText(Path.Combine(jitiDir, "jiti.mjs"),
            """
            import { readFileSync } from "node:fs";

            export default function createJiti() {
              return async function(file) {
                const source = readFileSync(file, "utf8");
                const encoded = Buffer.from(source, "utf8").toString("base64");
                const mod = await import(`data:text/javascript;base64,${encoded}`);
                return mod.default ?? mod;
              };
            }
            """);
    }

    private static bool HasNode()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            return process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetBridgeScriptPath()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OpenClaw.Agent", "Plugins", "plugin-bridge.mjs"));
        Assert.True(File.Exists(path), $"Bridge script not found at {path}");
        return path;
    }

    private static string ToJsString(string value)
        => JsonSerializer.Serialize(value);

    private async Task<BridgeMemoryMeasurement> MeasureBridgeMemoryAsync(string pluginDir, string pluginId)
    {
        await using var bridge = new PluginBridgeProcess(GetBridgeScriptPath(), new TestLogger());
        var entryFile = Directory.EnumerateFiles(pluginDir)
            .Select(Path.GetFileName)
            .FirstOrDefault(f => f is "index.js" or "index.ts");
        Assert.False(string.IsNullOrWhiteSpace(entryFile));

        var init = await bridge.StartAsync(Path.Combine(pluginDir, entryFile!), pluginId, null, CancellationToken.None);
        Assert.True(init.Compatible);
        Assert.NotEmpty(init.Tools);

        await Task.Delay(250);
        ForceGc();
        var host = CaptureHostMemory();
        var child = bridge.GetMemorySnapshot();
        Assert.NotNull(child);

        return new BridgeMemoryMeasurement
        {
            Host = host,
            Child = child!
        };
    }

    private static ProcessMemorySnapshot CaptureHostMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemorySnapshot
        {
            WorkingSetBytes = process.WorkingSet64,
            PrivateMemoryBytes = process.PrivateMemorySize64
        };
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double ToMb(long bytes) => bytes / 1024d / 1024d;

    private sealed class ProcessMemorySnapshot
    {
        public long WorkingSetBytes { get; init; }
        public long PrivateMemoryBytes { get; init; }
    }

    private sealed class BridgeMemoryMeasurement
    {
        public required ProcessMemorySnapshot Host { get; init; }
        public required PluginBridgeMemorySnapshot Child { get; init; }
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
