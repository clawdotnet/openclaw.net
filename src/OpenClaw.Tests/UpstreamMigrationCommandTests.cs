using System.Text.Json;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class UpstreamMigrationCommandTests
{
    [Fact]
    public async Task RunAsync_DryRun_WritesReportWithoutApplyingFiles()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "upstream");
            var targetConfig = Path.Combine(root, "target", "config", "openclaw.settings.json");
            var reportPath = Path.Combine(root, "migration-report.json");
            CreateUpstreamFixture(source);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpstreamMigrationCommand.RunAsync(
                ["--source", source, "--target-config", targetConfig, "--report", reportPath],
                output,
                error,
                root);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(File.Exists(reportPath));
            Assert.False(File.Exists(targetConfig));

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.False(report.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(source, report.RootElement.GetProperty("sourcePath").GetString());
            Assert.Equal(targetConfig, report.RootElement.GetProperty("targetConfigPath").GetString());
            Assert.False(string.IsNullOrWhiteSpace(report.RootElement.GetProperty("discoveredConfigPath").GetString()));
            Assert.Single(report.RootElement.GetProperty("skills").EnumerateArray());
            Assert.Single(report.RootElement.GetProperty("plugins").EnumerateArray());
            Assert.Contains(
                report.RootElement.GetProperty("skippedSettings").EnumerateArray().Select(static item => item.GetString()).OfType<string>(),
                value => value.Contains("customSetting", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Apply_WritesConfigManagedSkillsAndPluginPlan()
    {
        var root = CreateTempRoot();
        try
        {
            var source = Path.Combine(root, "upstream");
            var targetConfig = Path.Combine(root, "target", "config", "openclaw.settings.json");
            var reportPath = Path.Combine(root, "apply-report.json");
            CreateUpstreamFixture(source);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await UpstreamMigrationCommand.RunAsync(
                ["--source", source, "--target-config", targetConfig, "--report", reportPath, "--apply"],
                output,
                error,
                root);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(File.Exists(targetConfig));
            Assert.True(File.Exists(reportPath));

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.True(report.RootElement.GetProperty("applied").GetBoolean());
            var managedSkillRoot = report.RootElement.GetProperty("managedSkillRootPath").GetString();
            var pluginPlanPath = report.RootElement.GetProperty("pluginReviewPlanPath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(managedSkillRoot));
            Assert.False(string.IsNullOrWhiteSpace(pluginPlanPath));
            Assert.True(File.Exists(Path.Combine(managedSkillRoot!, "release-notes", "SKILL.md")));
            Assert.True(File.Exists(pluginPlanPath));

            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(targetConfig));
            var openClaw = config.RootElement.GetProperty("OpenClaw");
            Assert.Equal("0.0.0.0", openClaw.GetProperty("bindAddress").GetString());
            Assert.Equal(18889, openClaw.GetProperty("port").GetInt32());
            Assert.Equal(Path.Combine(source, "workspace"), openClaw.GetProperty("tooling").GetProperty("workspaceRoot").GetString());

            using var pluginPlan = JsonDocument.Parse(await File.ReadAllTextAsync(pluginPlanPath));
            var items = pluginPlan.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Contains("@example/sample-plugin@1.0.0", items[0].GetProperty("packageSpec").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-upstream-migration-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateUpstreamFixture(string sourceRoot)
    {
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "workspace"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "memory"));

        File.WriteAllText(
            Path.Combine(sourceRoot, "openclaw.json"),
            """
            {
              "OpenClaw": {
                "bindAddress": "0.0.0.0",
                "port": 18889,
                "authToken": "legacy-token",
                "llm": {
                  "provider": "openai",
                  "model": "gpt-4o-mini",
                  "apiKey": "env:OPENAI_API_KEY"
                },
                "tooling": {
                  "workspaceOnly": true,
                  "allowShell": true,
                  "workspaceRoot": "./workspace"
                },
                "memory": {
                  "provider": "file",
                  "storagePath": "./memory",
                  "projectId": "demo"
                },
                "channels": {
                  "telegram": { "enabled": true },
                  "discord": { "enabled": false }
                },
                "customSetting": "skip-me"
              }
            }
            """);

        var skillDir = Path.Combine(sourceRoot, "skills", "release-notes");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: Release Notes
            description: Summarize release notes for operators.
            ---

            Follow the documented process.
            """);

        var pluginDir = Path.Combine(sourceRoot, "plugins", "sample-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "openclaw.plugin.json"),
            """
            {
              "id": "sample-plugin",
              "name": "Sample Plugin",
              "version": "1.0.0",
              "channels": ["telegram"],
              "skills": ["skills"]
            }
            """);
        File.WriteAllText(
            Path.Combine(pluginDir, "package.json"),
            """
            {
              "name": "@example/sample-plugin",
              "version": "1.0.0"
            }
            """);
        File.WriteAllText(Path.Combine(pluginDir, "index.js"), "export default {};");
        var pluginSkillsDir = Path.Combine(pluginDir, "skills");
        Directory.CreateDirectory(pluginSkillsDir);
        File.WriteAllText(Path.Combine(pluginSkillsDir, "SKILL.md"), "# bundled skill");
    }
}
