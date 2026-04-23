using OpenClaw.Cli;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class UpgradeCommandsTests
{
    [Fact]
    public async Task RunAsync_Check_WithHealthyOfflineConfig_Passes()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            var text = output.ToString();
            Assert.Contains("OpenClaw upgrade preflight", text, StringComparison.Ordinal);
            Assert.Contains("Overall result: pass", text, StringComparison.Ordinal);
            Assert.Contains("[pass] Config and provider readiness", text, StringComparison.Ordinal);
            Assert.Contains("[pass] Plugin compatibility", text, StringComparison.Ordinal);
            Assert.Contains("[pass] Skill compatibility", text, StringComparison.Ordinal);
            Assert.Contains("[pass] Migration impact", text, StringComparison.Ordinal);
            Assert.Contains("Provider smoke was skipped because offline mode is enabled.", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Check_WithPluginCompatibilityErrors_Fails()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, workspace) = await CreateBaseConfigAsync(root);
            var pluginRoot = Path.Combine(workspace, ".openclaw", "extensions", "schema-plugin");
            Directory.CreateDirectory(pluginRoot);
            await File.WriteAllTextAsync(
                Path.Combine(pluginRoot, "openclaw.plugin.json"),
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
            await File.WriteAllTextAsync(Path.Combine(pluginRoot, "index.js"), "export default {};");

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            var text = output.ToString();
            Assert.Contains("Overall result: fail", text, StringComparison.Ordinal);
            Assert.Contains("[fail] Plugin compatibility", text, StringComparison.Ordinal);
            Assert.Contains("unsupported_schema_keyword", text, StringComparison.Ordinal);
            Assert.Contains("Upgrade preflight failed.", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Check_WithExpandedCompatibilitySurface_Warns()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            var config = GatewayConfigFile.Load(configPath);
            config.Plugins.Load.Paths = [Path.Combine(root, "plugins-extra")];
            config.Skills.Load.ExtraDirs = [Path.Combine(root, "skills-extra")];
            await GatewayConfigFile.SaveAsync(config, configPath);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            var text = output.ToString();
            Assert.Contains("Overall result: warn", text, StringComparison.Ordinal);
            Assert.Contains("[warn] Migration impact", text, StringComparison.Ordinal);
            Assert.Contains("Custom plugin load paths are configured", text, StringComparison.Ordinal);
            Assert.Contains("Extra skill directories are configured", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Help_PrintsUsage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await UpgradeCommands.RunAsync(["--help"], output, error, Directory.GetCurrentDirectory());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("openclaw upgrade", output.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(string ConfigPath, string Workspace)> CreateBaseConfigAsync(string root)
    {
        var configPath = Path.Combine(root, "config", "openclaw.settings.json");
        var workspace = Path.Combine(root, "workspace");
        using var setupOutput = new StringWriter();
        using var setupError = new StringWriter();
        var exitCode = await SetupCommand.RunAsync(
            [
                "--non-interactive",
                "--profile", "local",
                "--config", configPath,
                "--workspace", workspace,
                "--provider", "openai",
                "--model", "gpt-4o",
                "--api-key", "env:OPENAI_API_KEY"
            ],
            new StringReader(string.Empty),
            setupOutput,
            setupError,
            root,
            canPrompt: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, setupError.ToString());

        var config = GatewayConfigFile.Load(configPath);
        config.Skills.Load.IncludeManaged = false;
        await GatewayConfigFile.SaveAsync(config, configPath);
        return (configPath, workspace);
    }

    private static async Task WithIsolatedHomeAsync(Func<string, Task> callback)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-upgrade-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);

        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);
            await callback(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("USERPROFILE", previousUserProfile);
            Directory.Delete(root, recursive: true);
        }
    }
}
