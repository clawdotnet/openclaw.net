using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Extensions;
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
    public async Task LoadAsync_TsPluginWithLocalJiti_PassesFileUrlToJiti()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "ts-tool-file-url",
            "index.ts",
            """
            export default function(api) {
              api.registerTool({
                name: "ts_echo_file_url",
                description: "TS echo requiring file URL",
                parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                execute: async (_pluginId, params) => ({ text: `TSFILE:${params.text}` })
              });
            }
            """);
        CreateFakeJiti(pluginDir, requireFileUrl: true);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("TSFILE:hello", result);
    }

    [Fact]
    public async Task LoadAsync_TsPluginWithLocalJiti_WindowsAbsolutePath_LoadsSuccessfully()
    {
        if (!OperatingSystem.IsWindows() || !HasNode()) return;

        var pluginDir = CreatePlugin(
            "ts-tool-win-path",
            "index.ts",
            """
            export default function(api) {
              api.registerTool({
                name: "ts_echo_win_path",
                description: "TS echo on Windows absolute path",
                parameters: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                execute: async (_pluginId, params) => ({ text: `TSWIN:${params.text}` })
              });
            }
            """);
        CreateFakeJiti(pluginDir);

        Assert.True(Path.IsPathFullyQualified(pluginDir));
        Assert.Contains(":\\", pluginDir, StringComparison.Ordinal);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("TSWIN:hello", result);
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
    public async Task LoadAsync_UndefinedJsonElementConfig_IsTreatedAsMissing()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "undefined-config-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "undefined_config_echo",
                description: "Undefined config echo",
                parameters: { type: "object", properties: {} },
                execute: async () => String(api.config.mode ?? "unset")
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new(StringComparer.Ordinal)
            {
                ["undefined-config-plugin"] = new PluginEntryConfig
                {
                    Config = default
                }
            }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("unset", await tool.ExecuteAsync("{}", CancellationToken.None));
        Assert.Single(host.Reports, r => r.PluginId == "undefined-config-plugin" && r.Loaded);
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
              api.registerGatewayMethod("custom", () => {});
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
        Assert.Contains(report.Diagnostics, d => d.Code == "unsupported_gateway_method");
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

    private PluginHost CreateHost(PluginsConfig config, GatewayRuntimeState? runtimeState = null)
        => new(config, GetBridgeScriptPath(), new TestLogger(), runtimeState);

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

    private static void CreateFakeJiti(string pluginDir, bool requireFileUrl = false)
    {
        var jitiDir = Path.Combine(pluginDir, "node_modules", "jiti", "dist");
        Directory.CreateDirectory(jitiDir);
        File.WriteAllText(Path.Combine(jitiDir, "jiti.mjs"),
            $$"""
            import { readFileSync } from "node:fs";
            import { fileURLToPath } from "node:url";

            export default function createJiti(base) {
              if ({{requireFileUrl.ToString().ToLowerInvariant()}} && (typeof base !== "string" || !base.startsWith("file://"))) {
                throw new Error(`Expected file:// base URL but received ${base}`);
              }

              return async function(file) {
                if ({{requireFileUrl.ToString().ToLowerInvariant()}} && (typeof file !== "string" || !file.startsWith("file://"))) {
                  throw new Error(`Expected file:// plugin URL but received ${file}`);
                }

                const resolvedFile = typeof file === "string" && file.startsWith("file:")
                  ? fileURLToPath(file)
                  : file;
                const source = readFileSync(resolvedFile, "utf8");
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

    [Fact]
    public async Task LoadAsync_RegisterChannel_LoadsSuccessfully()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "channel-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerChannel({
                id: "test-channel",
                start: async () => {},
                send: async (msg) => {},
                stop: async () => {}
              });
              api.registerTool({
                name: "channel_echo",
                description: "Channel echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Single(tools);
        Assert.Single(host.ChannelAdapters);
        Assert.Equal("test-channel", host.ChannelAdapters[0].ChannelId);
        var report = Assert.Single(host.Reports, r => r.PluginId == "channel-plugin" && r.Loaded);
        Assert.Equal(1, report.ChannelCount);
        Assert.Contains(PluginCapabilityPolicy.Channels, report.RequestedCapabilities);
    }

    [Fact]
    public async Task LoadAsync_RegisterCommand_LoadsSuccessfully()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "command-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerCommand({
                name: "greet",
                description: "Greet user",
                handler: async (args) => `Hello, ${args || "world"}!`
              });
              api.registerTool({
                name: "command_echo",
                description: "Command echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Single(tools);
        var report = Assert.Single(host.Reports, r => r.PluginId == "command-plugin" && r.Loaded);
        Assert.Equal(1, report.CommandCount);
        Assert.Contains(PluginCapabilityPolicy.Commands, report.RequestedCapabilities);
    }

    [Fact]
    public async Task LoadAsync_RegisterProvider_LoadsSuccessfully()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "provider-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerProvider({
                id: "custom-llm",
                models: ["custom-1"],
                complete: async ({ messages }) => ({ text: "hello from custom" })
              });
              api.registerTool({
                name: "provider_echo",
                description: "Provider echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Single(tools);
        var report = Assert.Single(host.Reports, r => r.PluginId == "provider-plugin" && r.Loaded);
        Assert.Equal(1, report.ProviderCount);
        Assert.Contains(PluginCapabilityPolicy.Providers, report.RequestedCapabilities);
    }

    [Fact]
    public async Task LoadAsync_EventHook_LoadsSuccessfully()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "hook-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.on("tool:before", async (ctx) => true);
              api.on("tool:after", async (ctx) => {});
              api.registerTool({
                name: "hook_echo",
                description: "Hook echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Single(tools);
        Assert.Single(host.ToolHooks);
        var report = Assert.Single(host.Reports, r => r.PluginId == "hook-plugin" && r.Loaded);
        Assert.Equal(2, report.EventSubscriptionCount);
        Assert.Contains(PluginCapabilityPolicy.Hooks, report.RequestedCapabilities);
    }

    [Fact]
    public async Task LoadAsync_AotMode_BlocksJitOnlyBridgeCapabilities()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "aot-blocked-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerChannel({ id: "jit-only-channel" });
              api.registerCommand({
                name: "jitcmd",
                description: "jit command",
                handler: async () => "ok"
              });
              api.registerTool({
                name: "aot_blocked_echo",
                description: "AOT blocked echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "blocked"
              });
            };
            """);

        await using var host = CreateHost(
            new PluginsConfig
            {
                Enabled = true,
                Load = new PluginLoadConfig { Paths = [pluginDir] }
            },
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "aot" }, dynamicCodeSupported: true));

        var tools = await host.LoadAsync(null, CancellationToken.None);

        Assert.Empty(tools);
        var report = Assert.Single(host.Reports, r => r.PluginId == "aot-blocked-plugin");
        Assert.False(report.Loaded);
        Assert.True(report.BlockedByRuntimeMode);
        Assert.Equal("aot", report.EffectiveRuntimeMode);
        Assert.Contains(PluginCapabilityPolicy.Channels, report.RequestedCapabilities);
        Assert.Contains(PluginCapabilityPolicy.Commands, report.RequestedCapabilities);
        Assert.Contains(report.Diagnostics, d => d.Code == "jit_mode_required");
    }

    [Fact]
    public async Task LoadAsync_AotMode_AllowsAotSafeBridgeCapabilities()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "aot-safe-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerService({
                id: "svc",
                start: async () => {},
                stop: async () => {}
              });
              api.registerTool({
                name: "aot_safe_echo",
                description: "AOT safe echo",
                parameters: { type: "object", properties: { text: { type: "string" } } },
                execute: async (_pluginId, params) => `safe:${params.text ?? ""}`
              });
            };
            """);

        await using var host = CreateHost(
            new PluginsConfig
            {
                Enabled = true,
                Load = new PluginLoadConfig { Paths = [pluginDir] }
            },
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "aot" }, dynamicCodeSupported: true));

        var tools = await host.LoadAsync(null, CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("safe:hello", await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None));
        var report = Assert.Single(host.Reports, r => r.PluginId == "aot-safe-plugin" && r.Loaded);
        Assert.Contains(PluginCapabilityPolicy.Tools, report.RequestedCapabilities);
        Assert.Contains(PluginCapabilityPolicy.Services, report.RequestedCapabilities);
        Assert.DoesNotContain(report.RequestedCapabilities, capability => capability == PluginCapabilityPolicy.Channels);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("socket")]
    [InlineData("hybrid")]
    public async Task BridgeTransportModes_DeliverInboundNotifications_AndExecuteTools(string transportMode)
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            $"notify-plugin-{transportMode}",
            "index.js",
            """
            module.exports = function(api) {
              const channel = {
                id: "notify-channel",
                start: async () => {
                  if (channel.receive) {
                    setTimeout(() => {
                      channel.receive({ senderId: "user1", text: "hello from plugin", sessionId: "sess-123" });
                    }, 25);
                  }
                },
                send: async (msg) => {},
                stop: async () => {}
              };
              api.registerChannel(channel);
              api.registerTool({
                name: "notify_echo",
                description: "Notify echo",
                parameters: {
                  type: "object",
                  properties: {
                    text: { type: "string" }
                  }
                },
                execute: async (_pluginId, params) => `echo:${params.text ?? "ok"}`
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Transport = new BridgeTransportConfig { Mode = transportMode }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);
        var tool = Assert.Single(tools);
        var adapter = Assert.Single(host.ChannelAdapters);

        var inboundTcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.OnMessageReceived += (msg, _) =>
        {
            inboundTcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        };

        await adapter.StartAsync(CancellationToken.None);
        var inbound = await inboundTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("notify-channel", inbound.ChannelId);
        Assert.Equal("user1", inbound.SenderId);
        Assert.Equal("hello from plugin", inbound.Text);
        Assert.Equal("sess-123", inbound.SessionId);

        var result = await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None);
        Assert.Equal("echo:hello", result);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("socket")]
    [InlineData("hybrid")]
    public async Task BridgeTransportModes_RestartAfterChildExit(string transportMode)
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            $"restart-plugin-{transportMode}",
            "index.js",
            """
            module.exports = function(api) {
              api.registerTool({
                name: "restart_echo",
                description: "Restart echo",
                parameters: {
                  type: "object",
                  properties: {
                    text: { type: "string" },
                    kill: { type: "boolean" }
                  }
                },
                execute: async (_pluginId, params) => {
                  if (params.kill) {
                    setTimeout(() => process.exit(0), 20);
                    return "restarting";
                  }

                  return `echo:${params.text ?? "ok"}`;
                }
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Transport = new BridgeTransportConfig { Mode = transportMode }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);
        var tool = Assert.Single(tools);

        Assert.Equal("echo:first", await tool.ExecuteAsync("""{"text":"first"}""", CancellationToken.None));
        Assert.Equal("restarting", await tool.ExecuteAsync("""{"kill":true}""", CancellationToken.None));
        await Task.Delay(500);
        Assert.Equal("echo:second", await tool.ExecuteAsync("""{"text":"second"}""", CancellationToken.None));
    }

    [Fact]
    public async Task RegisterCommand_ExecutesThroughChatCommandProcessor()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "command-runtime-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerCommand({
                name: "greet",
                description: "Greet user",
                handler: async (args) => `Hello, ${args || "world"}!`
              });
              api.registerTool({
                name: "command_runtime_echo",
                description: "Command runtime echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        _ = await host.LoadAsync(null, CancellationToken.None);
        var memoryStore = Substitute.For<IMemoryStore>();
        var sessionManager = new SessionManager(memoryStore, new GatewayConfig());
        var processor = new ChatCommandProcessor(sessionManager);
        host.RegisterCommandsWith(processor);

        var session = new Session
        {
            Id = "session-1",
            ChannelId = "test",
            SenderId = "user"
        };

        var (handled, response) = await processor.TryProcessCommandAsync(session, "/greet Codex", CancellationToken.None);

        Assert.True(handled);
        Assert.Equal("Hello, Codex!", response);
    }

    [Fact]
    public async Task RegisterProvider_ReceivesSerializedChatOptions()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "provider-runtime-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerProvider({
                id: "custom-llm",
                models: ["custom-1", "custom-2"],
                complete: async ({ messages, options }) => ({
                  text: JSON.stringify({
                    modelId: options?.modelId ?? null,
                    temperature: options?.temperature ?? null,
                    maxOutputTokens: options?.maxOutputTokens ?? null,
                    responseFormatKind: options?.responseFormat?.kind ?? null,
                    responseFormatSchemaName: options?.responseFormat?.schemaName ?? null,
                    toolModeKind: options?.toolMode?.kind ?? null,
                    toolModeFunctionName: options?.toolMode?.functionName ?? null,
                    toolCount: options?.tools?.length ?? 0,
                    firstToolName: options?.tools?.[0]?.name ?? null,
                    firstToolHasSchema: !!options?.tools?.[0]?.inputSchema,
                    stopSequences: options?.stopSequences ?? [],
                    reasoningEffort: options?.reasoning?.effort ?? null,
                    additionalFoo: options?.additionalProperties?.foo ?? null,
                    messageCount: messages?.length ?? 0
                  })
                })
              });
              api.registerTool({
                name: "provider_runtime_echo",
                description: "Provider runtime echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        _ = await host.LoadAsync(null, CancellationToken.None);
        var registration = Assert.Single(host.ProviderRegistrations);
        var provider = new BridgedLlmProvider(registration.Bridge, registration.ProviderId, new TestLogger());

        using var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"number"}}}""");
        using var responseSchema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""");

        var options = new ChatOptions
        {
            ModelId = "custom-2",
            Temperature = 0.25f,
            MaxOutputTokens = 321,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(responseSchema.RootElement.Clone(), "provider_response"),
            ToolMode = ChatToolMode.RequireSpecific("math_tool"),
            Tools =
            [
                AIFunctionFactory.CreateDeclaration(
                    "math_tool",
                    "Math tool",
                    toolSchema.RootElement.Clone(),
                    returnJsonSchema: responseSchema.RootElement.Clone())
            ],
            StopSequences = ["END"],
            Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.Summary
            }
        };
        options.AdditionalProperties = new AdditionalPropertiesDictionary { ["foo"] = "bar" };

        var response = await provider.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            options,
            CancellationToken.None);

        var payload = JsonDocument.Parse(response.Text ?? "{}").RootElement;
        Assert.Equal("custom-2", payload.GetProperty("modelId").GetString());
        Assert.Equal(0.25, payload.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(321, payload.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("json", payload.GetProperty("responseFormatKind").GetString());
        Assert.Equal("provider_response", payload.GetProperty("responseFormatSchemaName").GetString());
        Assert.Equal("requireSpecific", payload.GetProperty("toolModeKind").GetString());
        Assert.Equal("math_tool", payload.GetProperty("toolModeFunctionName").GetString());
        Assert.Equal(1, payload.GetProperty("toolCount").GetInt32());
        Assert.Equal("math_tool", payload.GetProperty("firstToolName").GetString());
        Assert.True(payload.GetProperty("firstToolHasSchema").GetBoolean());
        Assert.Equal("END", payload.GetProperty("stopSequences")[0].GetString());
        Assert.Equal("High", payload.GetProperty("reasoningEffort").GetString());
        Assert.Equal("bar", payload.GetProperty("additionalFoo").GetString());
        Assert.Equal(1, payload.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task EventHooks_ForwardTypedRuntimePayloads()
    {
        if (!HasNode()) return;

        var beforePath = Path.Combine(_tempDir, "hook.before.json");
        var afterPath = Path.Combine(_tempDir, "hook.after.json");
        var pluginDir = CreatePlugin(
            "hook-runtime-plugin",
            "index.js",
            $$"""
            const { writeFileSync } = require("node:fs");

            module.exports = function(api) {
              api.on("tool:before", async (ctx) => {
                writeFileSync({{ToJsString(beforePath)}}, JSON.stringify(ctx));
                return { allow: false };
              });
              api.on("tool:after", async (ctx) => {
                writeFileSync({{ToJsString(afterPath)}}, JSON.stringify({
                  durationType: typeof ctx.duration,
                  failedType: typeof ctx.failed,
                  failed: ctx.failed,
                  duration: ctx.duration
                }));
              });
              api.registerTool({
                name: "hook_runtime_echo",
                description: "Hook runtime echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        _ = await host.LoadAsync(null, CancellationToken.None);
        var hook = Assert.Single(host.ToolHooks);

        var allowed = await hook.BeforeExecuteAsync("shell_exec", """{"cmd":"ls"}""", CancellationToken.None);
        await hook.AfterExecuteAsync("shell_exec", """{"cmd":"ls"}""", "done", TimeSpan.FromMilliseconds(123), failed: true, CancellationToken.None);

        Assert.False(allowed);

        var beforePayload = JsonDocument.Parse(await File.ReadAllTextAsync(beforePath)).RootElement;
        Assert.Equal("shell_exec", beforePayload.GetProperty("toolName").GetString());
        Assert.Equal("before", beforePayload.GetProperty("phase").GetString());

        var afterPayload = JsonDocument.Parse(await File.ReadAllTextAsync(afterPath)).RootElement;
        Assert.Equal("number", afterPayload.GetProperty("durationType").GetString());
        Assert.Equal("boolean", afterPayload.GetProperty("failedType").GetString());
        Assert.True(afterPayload.GetProperty("failed").GetBoolean());
        Assert.InRange(afterPayload.GetProperty("duration").GetDouble(), 100, 200);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("socket")]
    [InlineData("hybrid")]
    public async Task ChannelShutdown_InvokesStopOnDispose(string transportMode)
    {
        if (!HasNode()) return;

        var stopPath = Path.Combine(_tempDir, $"channel-stop-{transportMode}.txt");
        var pluginDir = CreatePlugin(
            $"channel-stop-plugin-{transportMode}",
            "index.js",
            $$"""
            const { writeFileSync } = require("node:fs");

            module.exports = function(api) {
              const channel = {
                id: "stop-channel",
                start: async () => {},
                send: async () => {},
                stop: async () => writeFileSync({{ToJsString(stopPath)}}, "stopped")
              };
              api.registerChannel(channel);
              api.registerTool({
                name: "channel_stop_echo",
                description: "Channel stop echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using (var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Transport = new BridgeTransportConfig { Mode = transportMode }
        }))
        {
            _ = await host.LoadAsync(null, CancellationToken.None);
            var adapter = Assert.Single(host.ChannelAdapters);
            await adapter.StartAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(stopPath));
    }

    [Fact]
    public async Task LoadAsync_SupportedSurfacesMixedWithUnsupported_OnlyUnsupportedFails()
    {
        if (!HasNode()) return;

        // A plugin that uses registerChannel (supported) AND registerGatewayMethod (unsupported)
        // should still fail because of the unsupported surface
        var pluginDir = CreatePlugin(
            "mixed-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.registerChannel({ id: "ok-channel" });
              api.registerGatewayMethod("bad", () => {});
              api.registerTool({
                name: "mixed_echo",
                description: "Mixed",
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
        var report = Assert.Single(host.Reports, r => r.PluginId == "mixed-plugin" && !r.Loaded);
        Assert.Contains(report.Diagnostics, d => d.Code == "unsupported_gateway_method");
    }

    [Fact]
    public async Task HybridTransport_AfterSocketSwitch_NotificationsAreNotDuplicated()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "hybrid-dedup-plugin",
            "index.js",
            """
            module.exports = function(api) {
              const channel = {
                id: "dedup-channel",
                start: async () => {
                  if (channel.receive) {
                    setTimeout(() => {
                      channel.receive({ senderId: "u1", text: "ping", sessionId: "s1" });
                    }, 50);
                  }
                },
                send: async () => {},
                stop: async () => {}
              };
              api.registerChannel(channel);
              api.registerTool({
                name: "hybrid_dedup_echo",
                description: "Hybrid dedup echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Transport = new BridgeTransportConfig { Mode = "hybrid" }
        });

        var tools = await host.LoadAsync(null, CancellationToken.None);
        Assert.Single(tools);
        var adapter = Assert.Single(host.ChannelAdapters);

        var receivedCount = 0;
        var inboundTcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.OnMessageReceived += (msg, _) =>
        {
            Interlocked.Increment(ref receivedCount);
            inboundTcs.TrySetResult(msg);
            return ValueTask.CompletedTask;
        };

        await adapter.StartAsync(CancellationToken.None);
        var inbound = await inboundTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait a bit to ensure no duplicate arrives
        await Task.Delay(200);

        Assert.Equal("ping", inbound.Text);
        Assert.Equal(1, receivedCount);
    }

    [Fact]
    public async Task HookTimeout_DefaultsToAllow_WhenPluginExceedsTimeout()
    {
        if (!HasNode()) return;

        var pluginDir = CreatePlugin(
            "slow-hook-plugin",
            "index.js",
            """
            module.exports = function(api) {
              api.on("tool:before", async (ctx) => {
                await new Promise(resolve => setTimeout(resolve, 10000));
                return { allow: false };
              });
              api.registerTool({
                name: "slow_hook_echo",
                description: "Slow hook echo",
                parameters: { type: "object", properties: {} },
                execute: async () => "ok"
              });
            };
            """);

        await using var host = CreateHost(new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        });

        _ = await host.LoadAsync(null, CancellationToken.None);
        var hook = Assert.Single(host.ToolHooks);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allowed = await hook.BeforeExecuteAsync("shell_exec", """{"cmd":"ls"}""", CancellationToken.None);
        sw.Stop();

        Assert.True(allowed, "Hook should default to allow on timeout");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"Hook should time out within ~5s, took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void ProviderOrdering_PluginRegisteredProvider_IsResolvedByFactory()
    {
        LlmClientFactory.ResetDynamicProviders();
        var providerName = $"test-provider-{Guid.NewGuid():N}";
        var mockClient = Substitute.For<IChatClient>();

        LlmClientFactory.RegisterProvider(providerName, mockClient);

        var resolved = LlmClientFactory.CreateChatClient(new LlmProviderConfig
        {
            Provider = providerName,
            Model = "test-model"
        });

        Assert.Same(mockClient, resolved);
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
