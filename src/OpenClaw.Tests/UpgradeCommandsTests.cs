using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Models;
using GatewaySetupArtifacts = OpenClaw.Core.Setup.GatewaySetupArtifacts;
using UpgradeRollbackSnapshotStore = OpenClaw.Core.Setup.UpgradeRollbackSnapshotStore;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
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
            Assert.Contains("[pass] Rollback snapshot", text, StringComparison.Ordinal);
            Assert.Contains("Provider smoke was skipped because offline mode is enabled.", text, StringComparison.Ordinal);

            var snapshot = new UpgradeRollbackSnapshotStore(configPath).Load();
            Assert.NotNull(snapshot);
            Assert.Equal(configPath, snapshot!.ConfigPath);
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
            Assert.Null(new UpgradeRollbackSnapshotStore(configPath).Load());
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
    public async Task RunAsync_Rollback_RestoresSavedConfigAndArtifacts()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            var envExamplePath = GatewaySetupArtifacts.BuildEnvExamplePath(configPath);
            var deployDirectory = SetupLifecycleCommand.GetDeployDirectory(configPath);
            Directory.CreateDirectory(deployDirectory);
            var deployFile = Path.Combine(deployDirectory, "run-gateway.sh");
            await File.WriteAllTextAsync(deployFile, "#!/usr/bin/env bash\necho original\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    deployFile,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }

            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                var checkExitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);
                Assert.Equal(0, checkExitCode);
                Assert.Equal(string.Empty, error.ToString());
            }

            var originalConfigText = await File.ReadAllTextAsync(configPath);
            var originalEnvText = await File.ReadAllTextAsync(envExamplePath);
            var originalDeployText = await File.ReadAllTextAsync(deployFile);

            var config = GatewayConfigFile.Load(configPath);
            config.BindAddress = "0.0.0.0";
            config.Port = 19999;
            await GatewayConfigFile.SaveAsync(config, configPath);
            await File.WriteAllTextAsync(envExamplePath, "BROKEN=1\n");
            await File.WriteAllTextAsync(deployFile, "#!/usr/bin/env bash\necho broken\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(deployFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            using var rollbackOutput = new StringWriter();
            using var rollbackError = new StringWriter();
            var rollbackExitCode = await UpgradeCommands.RunAsync(["rollback", "--config", configPath, "--offline"], rollbackOutput, rollbackError, root);

            Assert.Equal(0, rollbackExitCode);
            Assert.Equal(string.Empty, rollbackError.ToString());
            Assert.Equal(originalConfigText, await File.ReadAllTextAsync(configPath));
            Assert.Equal(originalEnvText, await File.ReadAllTextAsync(envExamplePath));
            Assert.Equal(originalDeployText, await File.ReadAllTextAsync(deployFile));
            var text = rollbackOutput.ToString();
            Assert.Contains("Restored last-known-good setup snapshot.", text, StringComparison.Ordinal);
            Assert.Contains("Rollback completed successfully.", text, StringComparison.Ordinal);
            Assert.Contains("Verification result: pass", text, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                var restoredMode = File.GetUnixFileMode(deployFile);
                Assert.True((restoredMode & UnixFileMode.UserExecute) != 0, $"Expected '{deployFile}' to be executable after rollback, but mode was {restoredMode}.");
            }
        });
    }

    [Fact]
    public async Task RunAsync_Rollback_WithoutSnapshot_Fails()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpgradeCommands.RunAsync(["rollback", "--config", configPath, "--offline"], output, error, root);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("No rollback snapshot was found", error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Rollback_WithoutSnapshot_QuotesConfigPathWithSpaces()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var configPath = Path.Combine(root, "config with spaces", "openclaw.settings.json");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            using var setupOutput = new StringWriter();
            using var setupError = new StringWriter();
            var setupExitCode = await SetupCommand.RunAsync(
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

            Assert.Equal(0, setupExitCode);
            Assert.Equal(string.Empty, setupError.ToString());

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await UpgradeCommands.RunAsync(["rollback", "--config", configPath, "--offline"], output, error, root);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains($"\"{configPath}\"", error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Rollback_WithCorruptSnapshot_ReportsManifestError()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            var store = new UpgradeRollbackSnapshotStore(configPath);
            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                var checkExitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);
                Assert.Equal(0, checkExitCode);
                Assert.Equal(string.Empty, error.ToString());
            }

            var manifestPath = Path.Combine(store.SnapshotDirectory, "snapshot.json");
            await File.WriteAllTextAsync(manifestPath, "{not-json");

            using var rollbackOutput = new StringWriter();
            using var rollbackError = new StringWriter();
            var rollbackExitCode = await UpgradeCommands.RunAsync(["rollback", "--config", configPath, "--offline"], rollbackOutput, rollbackError, root);

            Assert.Equal(1, rollbackExitCode);
            Assert.Equal(string.Empty, rollbackOutput.ToString());
            Assert.Contains("corrupt or invalid JSON", rollbackError.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("No rollback snapshot was found", rollbackError.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RunAsync_Rollback_WithTamperedManifest_FailsSafely()
    {
        await WithIsolatedHomeAsync(async root =>
        {
            var (configPath, _) = await CreateBaseConfigAsync(root);
            var store = new UpgradeRollbackSnapshotStore(configPath);
            using (var output = new StringWriter())
            using (var error = new StringWriter())
            {
                var checkExitCode = await UpgradeCommands.RunAsync(["check", "--config", configPath, "--offline"], output, error, root);
                Assert.Equal(0, checkExitCode);
                Assert.Equal(string.Empty, error.ToString());
            }

            Assert.True(store.TryLoad(out var snapshot, out var loadError), loadError);
            var tampered = new UpgradeRollbackSnapshot
            {
                SchemaVersion = snapshot!.SchemaVersion,
                SnapshotId = snapshot.SnapshotId,
                CreatedAtUtc = snapshot.CreatedAtUtc,
                CreatedByVersion = snapshot.CreatedByVersion,
                ConfigPath = snapshot.ConfigPath,
                WorkspacePath = snapshot.WorkspacePath,
                VerificationStatus = snapshot.VerificationStatus,
                Offline = snapshot.Offline,
                RequireProvider = snapshot.RequireProvider,
                Artifacts = snapshot.Artifacts
                    .Select(artifact => artifact.Kind == "deploy"
                        ? new UpgradeRollbackSnapshotArtifact
                        {
                            Kind = artifact.Kind,
                            TargetPath = Path.Combine(root, "evil"),
                            Exists = artifact.Exists,
                            IsDirectory = artifact.IsDirectory,
                            SnapshotRelativePath = ".." + Path.DirectorySeparatorChar + "payload"
                        }
                        : new UpgradeRollbackSnapshotArtifact
                        {
                            Kind = artifact.Kind,
                            TargetPath = artifact.TargetPath,
                            Exists = artifact.Exists,
                            IsDirectory = artifact.IsDirectory,
                            SnapshotRelativePath = artifact.SnapshotRelativePath
                        })
                    .ToArray()
            };
            var manifestPath = Path.Combine(store.SnapshotDirectory, "snapshot.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(tampered, CoreJsonContext.Default.UpgradeRollbackSnapshot));

            using var rollbackOutput = new StringWriter();
            using var rollbackError = new StringWriter();
            var rollbackExitCode = await UpgradeCommands.RunAsync(["rollback", "--config", configPath, "--offline"], rollbackOutput, rollbackError, root);

            Assert.Equal(1, rollbackExitCode);
            Assert.Equal(string.Empty, rollbackOutput.ToString());
            Assert.Contains("unexpected restore target path", rollbackError.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, "evil")));
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
        Assert.Contains("rollback", output.ToString(), StringComparison.Ordinal);
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
