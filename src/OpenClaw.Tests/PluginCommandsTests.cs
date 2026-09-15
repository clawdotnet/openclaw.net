using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PluginCommandsTests
{
    [Fact]
    public void InspectCandidate_WithManifest_ReturnsUpstreamCompatibleSummary()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "sample-plugin",
                  "name": "Sample Plugin",
                  "version": "1.2.3",
                  "description": "Test plugin",
                  "channels": ["telegram"],
                  "providers": ["sample-provider"],
                  "skills": ["skills"]
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");
            var skillsDir = Path.Combine(root, "skills");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "# Sample skill");

            var inspection = PluginCommands.InspectCandidate(root, "./sample-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("sample-plugin", inspection.PluginId);
            Assert.Equal("upstream-compatible", inspection.TrustLevel);
            Assert.Equal("manifest-valid", inspection.CompatibilityStatus);
            Assert.Contains("channels=telegram", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("providers=sample-provider", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("skills=1", inspection.DeclaredSurface, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithRegisterCli_AllowsInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"cli-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(
                Path.Combine(root, "index.js"),
                "module.exports = api => api.registerCli(({ program }) => program.command('fixture'), { commands: ['fixture'] });");

            var inspection = PluginCommands.InspectCandidate(root, "./cli-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("manifest-valid", inspection.CompatibilityStatus);
            Assert.DoesNotContain(
                inspection.Diagnostics,
                item => string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithRegisterGatewayMethod_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.js"), "module.exports = api => api.registerGatewayMethod('unsafe', () => {});");

            var inspection = PluginCommands.InspectCandidate(root, "./gateway-method-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.False(inspection.CanInstall);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "unsupported_gateway_method");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_DoesNotFollowSourceDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.js"), "module.exports = () => {};");
            File.WriteAllText(
                Path.Combine(outside, "outside.js"),
                "module.exports = api => api.registerGatewayMethod('outside', () => {});");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-source"), outside);

            var inspection = PluginCommands.InspectCandidate(root, "./symlink-safe-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.DoesNotContain(inspection.Diagnostics, item => item.Code == "unsupported_gateway_method");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithNewerPluginApiFloor_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"future-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(Path.Combine(root, "dist.js"), "module.exports = () => {};");
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                """
                {
                  "name": "future-plugin",
                  "openclaw": {
                    "runtimeExtensions": ["./dist.js"],
                    "compat": { "pluginApi": ">=2026.7.1" }
                  }
                }
                """);

            var inspection = PluginCommands.InspectCandidate(root, "./future-plugin", sourceIsNpm: false);

            Assert.False(inspection.CanInstall);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "plugin_api_version_unsupported");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectRuntimeAsync_WithRegisterCli_ReturnsDescriptorCount()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        try
        {
            var entryPath = Path.Combine(root, "index.js");
            File.WriteAllText(
                entryPath,
                "module.exports = api => api.registerCli(({ program }) => program.command('fixture').description('Fixture commands'), { commands: ['fixture'] });");

            var inspection = await PluginCommands.InspectRuntimeAsync(
                entryPath,
                "runtime-cli",
                TestContext.Current.CancellationToken);

            Assert.True(inspection.Compatible);
            Assert.Equal(1, inspection.CliCommandCount);
            Assert.DoesNotContain(inspection.Diagnostics, item => item.Code == "unsupported_cli_registration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginCliCommands_ExecutesNestedCommandWithArgumentsAndOptions()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        try
        {
            var markerPath = Path.Combine(root, "result.txt");
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"cli-execution-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(
                Path.Combine(root, "index.js"),
                $$"""
                const fs = require("node:fs");
                console.log("plugin registration noise");
                module.exports = api => api.registerCli(({ program }) => {
                  const root = program.command("fixture").description("Fixture commands");
                  root.command("write")
                    .argument("<value>", "Value to write")
                    .option("--upper", "Uppercase the value")
                    .requiredOption("--threshold <value>", "Threshold")
                    .action(async (value, options) => {
                      const output = options.upper ? value.toUpperCase() : value;
                      fs.writeFileSync({{JsonSerializer.Serialize(markerPath)}}, `${output}:${options.threshold}`);
                    });
                }, { commands: ["fixture"] });
                """);

            var bridgeScript = PluginCommands.ResolveBridgeScriptPath();
            Assert.NotNull(bridgeScript);
            var result = await PluginCliCommands.TryRunAsync(
                "fixture",
                ["write", "hello", "--upper", "--threshold", "-1"],
                new PluginsConfig { Load = new PluginLoadConfig { Paths = [root] } },
                workspacePath: null,
                new HashSet<string>(StringComparer.Ordinal),
                bridgeScript,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result);
            Assert.Equal("HELLO:-1", File.ReadAllText(markerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginCliCommands_DescribeRejectsOversizedOutput()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        try
        {
            var entryPath = Path.Combine(root, "index.js");
            File.WriteAllText(
                entryPath,
                "process.stdout.write('x'.repeat(1100000)); module.exports = api => api.registerCli(({ program }) => program.command('fixture')); ");
            var bridgeScript = PluginCommands.ResolveBridgeScriptPath();
            Assert.NotNull(bridgeScript);

            var description = await PluginCliCommands.DescribeAsync(
                entryPath,
                "oversized-cli-plugin",
                pluginConfig: null,
                bridgeScript,
                TestContext.Current.CancellationToken);

            Assert.False(description.Success);
            Assert.Contains("1 MiB", description.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PluginCliCommands_LoadBlockedPluginIds_ReadsQuarantineState()
    {
        var root = CreateTempRoot();
        try
        {
            var adminDir = Path.Combine(root, "admin");
            Directory.CreateDirectory(adminDir);
            File.WriteAllText(
                Path.Combine(adminDir, "plugin-state.json"),
                """[{"pluginId":"blocked-plugin","quarantined":true}]""");

            var blocked = PluginCliCommands.LoadBlockedPluginIds(root);

            Assert.Contains("blocked-plugin", blocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithStandaloneEntry_ReturnsUntrustedWarning()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./standalone-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("untrusted", inspection.TrustLevel);
            Assert.Equal("entry-only", inspection.DeclaredSurface);
            Assert.Contains(inspection.Warnings, warning => warning.Contains("No openclaw.plugin.json manifest", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithCompatOnlyOpenClawMetadata_UsesConventionalEntry()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                """{"name":"compat-only","openclaw":{"compat":{"pluginApi":"^2026.5.0"}}}""");
            File.WriteAllText(Path.Combine(root, "index.js"), "module.exports = () => {};");

            var inspection = PluginCommands.InspectCandidate(root, "./compat-only", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.EndsWith("index.js", inspection.EntryPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithCompatibleBundle_ReportsMappedAndDetectedCapabilities()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
            Directory.CreateDirectory(Path.Combine(root, "commands"));
            Directory.CreateDirectory(Path.Combine(root, "agents"));
            File.WriteAllText(
                Path.Combine(root, ".claude-plugin", "plugin.json"),
                "{\"name\":\"claude-value-bundle\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(root, "commands", "summarize.md"), "Summarize the current task.");

            var inspection = PluginCommands.InspectCandidate(root, "./claude-value-bundle", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal(PluginFormats.Bundle, inspection.Format);
            Assert.Equal("claude", inspection.BundleFormat);
            Assert.Contains("mapped=commands", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("detected_only=agents", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "bundle_capability_detected_only");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_BundleDoesNotRunNpmLifecycleScripts()
    {
        var root = CreateTempRoot();
        var targetParent = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codex-plugin"));
            Directory.CreateDirectory(Path.Combine(root, "skills", "safe-bundle"));
            File.WriteAllText(Path.Combine(root, ".codex-plugin", "plugin.json"), "{\"name\":\"safe-bundle\"}");
            File.WriteAllText(
                Path.Combine(root, "skills", "safe-bundle", "SKILL.md"),
                "---\nname: safe-bundle\ndescription: Safe bundle\n---\nUse safe content.");
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                "{\"name\":\"safe-bundle\",\"scripts\":{\"install\":\"node -e \\\"require('fs').writeFileSync('lifecycle-ran','yes')\\\"\"}}");
            var target = Path.Combine(targetParent, "safe-bundle");

            var result = await PluginCommands.InstallPreparedDirectoryAsync(
                root,
                target,
                "./safe-bundle",
                sourceIsNpm: false);

            Assert.True(result.Success, result.Error);
            Assert.True(Directory.Exists(target));
            Assert.False(File.Exists(Path.Combine(target, "lifecycle-ran")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetParent, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_NativePluginDoesNotRunNpmLifecycleScripts()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        var targetParent = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"safe-native-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(
                Path.Combine(root, "index.js"),
                "module.exports = () => {};");
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                """{"name":"safe-native-plugin","scripts":{"install":"node -e \"require('fs').writeFileSync('lifecycle-ran','yes')\""}}""");
            var target = Path.Combine(targetParent, "safe-native-plugin");

            var result = await PluginCommands.InstallPreparedDirectoryAsync(
                root,
                target,
                "./safe-native-plugin",
                sourceIsNpm: false);

            Assert.True(result.Success, result.Error);
            Assert.True(Directory.Exists(target));
            Assert.False(File.Exists(Path.Combine(target, "lifecycle-ran")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetParent, recursive: true);
        }
    }

    [Fact]
    public void ResolveNpmCmdPath_FindsNpmCmdOnPath()
    {
        var fakeDir = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(fakeDir, "npm.cmd"), "@echo off");
            var pathEnv = fakeDir + Path.PathSeparator + "C:\\definitely-not-a-real-dir";

            Assert.Equal(Path.Combine(fakeDir, "npm.cmd"), PluginCommands.ResolveNpmCmdPath(pathEnv));
        }
        finally { Directory.Delete(fakeDir, recursive: true); }
    }

    [Fact]
    public void ResolveNpmCmdPath_SkipsEmptyPathEntries()
    {
        var fakeDir = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(fakeDir, "npm.cmd"), "@echo off");
            var pathEnv = ";" + fakeDir + ";;";

            Assert.Equal(Path.Combine(fakeDir, "npm.cmd"), PluginCommands.ResolveNpmCmdPath(pathEnv));
        }
        finally { Directory.Delete(fakeDir, recursive: true); }
    }

    [Fact]
    public void ResolveNpmCmdPath_NotFound_FallsBackToBareName()
    {
        Assert.Equal("npm.cmd", PluginCommands.ResolveNpmCmdPath("C:\\definitely-not-a-real-dir"));
        Assert.Equal("npm.cmd", PluginCommands.ResolveNpmCmdPath(null));
        Assert.Equal("npm.cmd", PluginCommands.ResolveNpmCmdPath(""));
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_InvalidBundlePreservesExistingInstall()
    {
        var root = CreateTempRoot();
        var targetParent = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
            File.WriteAllText(
                Path.Combine(root, ".claude-plugin", "plugin.json"),
                "{\"name\":\"preserved-bundle\",\"skills\":[\"missing-skills\"]}");
            var target = Path.Combine(targetParent, "preserved-bundle");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "working-version");

            var result = await PluginCommands.InstallPreparedDirectoryAsync(
                root,
                target,
                "./preserved-bundle",
                sourceIsNpm: false);

            Assert.False(result.Success);
            Assert.Equal("working-version", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetParent, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithInvalidConfigSchema_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "schema-plugin",
                  "name": "Schema Plugin",
                  "configSchema": {
                    "type": "object",
                    "$ref": "#/definitions/unsupported"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./schema-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.False(inspection.CanInstall);
            Assert.Equal("errors", inspection.CompatibilityStatus);
            Assert.True(inspection.ErrorCount > 0);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "unsupported_schema_keyword");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-plugin-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool HasNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
