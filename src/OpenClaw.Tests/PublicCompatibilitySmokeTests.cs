using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Compatibility;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PublicCompatibilitySmokeTests : IDisposable
{
    private const string SmokeEnvVar = "OPENCLAW_PUBLIC_SMOKE";
    private const string LatestCanaryEnvVar = "OPENCLAW_LATEST_CANARY";
    private readonly string _tempDir;

    public PublicCompatibilitySmokeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-public-smoke", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    [Trait("Category", "PublicSmoke")]
    public async Task PublicPackages_MatchPinnedCompatibilityManifest()
    {
        if (!HasNode() || !IsSmokeEnabled())
            return;

        var manifest = PublicCompatibilityCatalog.GetCatalog();
        foreach (var entry in manifest.Items)
        {
            switch (entry.Kind)
            {
                case "clawhub-skill":
                    await VerifyClawHubSkillAsync(entry);
                    break;
                case "npm-plugin":
                    await VerifyNpmPluginAsync(entry);
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"Unsupported smoke entry kind '{entry.Kind}'.");
            }
        }
    }

    public static IEnumerable<object[]> LatestNpmScenarioIds()
        => PublicCompatibilityCatalog.GetCatalog().Items
            .Where(static entry => string.Equals(entry.Kind, "npm-plugin", StringComparison.Ordinal))
            .GroupBy(static entry => entry.PackageName, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(entry => string.Equals(entry.CompatibilityStatus, "compatible", StringComparison.Ordinal))
                .First())
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .Select(static entry => new object[] { entry.Id });

    [Theory]
    [MemberData(nameof(LatestNpmScenarioIds))]
    [Trait("Category", "LatestCanary")]
    public async Task LatestNpmPackage_ReportsCompatibilityDrift(string scenarioId)
    {
        if (!IsLatestCanaryEnabled())
            return;
        Assert.True(HasNode(), "OPENCLAW_LATEST_CANARY is enabled, but Node.js is unavailable.");

        var entry = PublicCompatibilityCatalog.GetCatalog().Items.Single(item => item.Id == scenarioId);
        Assert.False(string.IsNullOrWhiteSpace(entry.PackageName));
        await VerifyNpmPluginAsync(
            entry,
            packageSpecOverride: $"{entry.PackageName}@latest",
            scenarioIdOverride: $"latest-{entry.Id}",
            installExtraPackagesOverride: entry.LatestCanaryInstallExtraPackages is { Length: > 0 }
                ? entry.LatestCanaryInstallExtraPackages
                : null);
    }

    private async Task VerifyClawHubSkillAsync(CompatibilityCatalogEntry entry)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.SkillSlug), $"Smoke entry '{entry.Id}' must declare a skill slug.");
        Assert.False(string.IsNullOrWhiteSpace(entry.SkillRef), $"Smoke entry '{entry.Id}' must declare an owner-qualified skill ref.");
        Assert.False(string.IsNullOrWhiteSpace(entry.PackageVersion), $"Smoke entry '{entry.Id}' must declare a skill version.");
        Assert.False(string.IsNullOrWhiteSpace(entry.ExpectedRelativePath), $"Smoke entry '{entry.Id}' must declare expectedRelativePath.");

        var workdir = CreateScenarioDirectory(entry.Id);
        await RunCommandAsync(
            ResolveCommand("npx"),
            workdir,
            "-y", "clawhub",
            "--workdir", workdir,
            "--dir", "skills",
            "--no-input",
            "install", entry.SkillRef!,
            "--version", entry.PackageVersion!);

        var expectedPath = Path.Combine(workdir, entry.ExpectedRelativePath!
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(expectedPath), $"Expected skill file '{expectedPath}' for smoke entry '{entry.Id}'.");

        var skills = SkillLoader.LoadAll(
            new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            },
            workdir,
            new TestLogger());

        Assert.NotEmpty(skills);
    }

    private async Task VerifyNpmPluginAsync(
        CompatibilityCatalogEntry entry,
        string? packageSpecOverride = null,
        string? scenarioIdOverride = null,
        IReadOnlyList<string>? installExtraPackagesOverride = null)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.PackageSpec), $"Smoke entry '{entry.Id}' must declare an npm spec.");
        Assert.False(string.IsNullOrWhiteSpace(entry.PackageName), $"Smoke entry '{entry.Id}' must declare packageName.");
        Assert.False(string.IsNullOrWhiteSpace(entry.PluginId), $"Smoke entry '{entry.Id}' must declare pluginId.");
        Assert.False(string.IsNullOrWhiteSpace(entry.CompatibilityStatus), $"Smoke entry '{entry.Id}' must declare expectedStatus.");

        var scenarioDir = CreateScenarioDirectory(scenarioIdOverride ?? entry.Id);
        var installDir = Path.Combine(scenarioDir, "npm");
        Directory.CreateDirectory(installDir);

        var packages = new List<string> { packageSpecOverride ?? entry.PackageSpec! };
        var extraPackages = installExtraPackagesOverride ?? entry.InstallExtraPackages;
        if (extraPackages.Count > 0)
            packages.AddRange(extraPackages);
        await InstallPackagesAsync(installDir, packages);

        var packageDir = Path.Combine(installDir, "node_modules", entry.PackageName!
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(packageDir), $"Installed package directory '{packageDir}' was not found.");
        var installedVersion = ReadInstalledPackageVersion(packageDir);
        var scenarioLabel = $"{entry.PackageName}@{installedVersion ?? "unknown"}";

        var workspaceDir = Path.Combine(scenarioDir, "workspace");
        Directory.CreateDirectory(workspaceDir);

        await using var host = CreateHost(BuildPluginConfig(entry, packageDir));
        var tools = await host.LoadAsync(workspaceDir, TestContext.Current.CancellationToken);
        var report = host.Reports.LastOrDefault(r => string.Equals(r.PluginId, entry.PluginId, StringComparison.Ordinal));
        Assert.NotNull(report);

        if (string.Equals(entry.CompatibilityStatus, "compatible", StringComparison.Ordinal))
        {
            Assert.True(report!.Loaded, $"Expected plugin '{entry.Id}' ({scenarioLabel}) to load. Error: {report.Error}. Diagnostics: [{string.Join(", ", report.Diagnostics.Select(d => d.Code))}]");

            foreach (var toolName in entry.ExpectedToolNames ?? [])
                Assert.Contains(tools, tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));

            foreach (var commandName in entry.ExpectedCliCommandNames ?? [])
                Assert.Contains(report.CliCommandNames, name => string.Equals(name, commandName, StringComparison.Ordinal));

            if (entry.ExpectedSkillNames is { Length: > 0 })
            {
                var skills = SkillLoader.LoadAll(
                    new SkillsConfig
                    {
                        Enabled = true,
                        Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
                    },
                    workspaceDir,
                    new TestLogger(),
                    host.SkillRoots);

                foreach (var skillName in entry.ExpectedSkillNames)
                    Assert.Contains(skills, skill => string.Equals(skill.Name, skillName, StringComparison.Ordinal));
            }
        }
        else if (string.Equals(entry.CompatibilityStatus, "incompatible", StringComparison.Ordinal))
        {
            Assert.False(report!.Loaded, $"Expected plugin '{entry.Id}' to fail compatibility checks.");
            foreach (var diagnosticCode in entry.ExpectedDiagnosticCodes ?? [])
            {
                Assert.True(
                    report.Diagnostics.Any(diag => string.Equals(diag.Code, diagnosticCode, StringComparison.Ordinal)),
                    $"Expected plugin '{entry.Id}' ({scenarioLabel}) to report diagnostic '{diagnosticCode}', but got: [{string.Join(", ", report.Diagnostics.Select(d => d.Code))}]");
            }
        }
        else
        {
            throw new Xunit.Sdk.XunitException($"Unsupported expectedStatus '{entry.CompatibilityStatus}' for smoke entry '{entry.Id}'.");
        }
    }

    private static PluginsConfig BuildPluginConfig(CompatibilityCatalogEntry entry, string packageDir)
    {
        var config = new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [packageDir] }
        };

        if (!string.IsNullOrWhiteSpace(entry.ConfigJsonExample))
        {
            config.Entries[entry.PluginId!] = new PluginEntryConfig
            {
                Config = JsonDocument.Parse(entry.ConfigJsonExample).RootElement.Clone()
            };
        }

        return config;
    }

    private static async Task InstallPackagesAsync(string workdir, IReadOnlyList<string> packages)
    {
        var args = new List<string>
        {
            "install",
            "--no-package-lock",
            "--no-save",
            "--ignore-scripts",
            "--silent"
        };
        args.AddRange(packages);

        await RunCommandAsync(
            ResolveCommand("npm"),
            workdir,
            args.ToArray());
    }

    private static string? ReadInstalledPackageVersion(string packageDir)
    {
        var packageJson = Path.Combine(packageDir, "package.json");
        if (!File.Exists(packageJson))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
        return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
    }

    private string CreateScenarioDirectory(string id)
    {
        var path = Path.Combine(_tempDir, id);
        Directory.CreateDirectory(path);
        return path;
    }

    private PluginHost CreateHost(PluginsConfig config)
        => new(config, GetBridgeScriptPath(), new TestLogger());

    private static async Task RunCommandAsync(string fileName, string workdir, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"Command '{fileName} {string.Join(" ", args)}' failed with exit code {process.ExitCode} in '{workdir}'.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
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

    private static bool IsSmokeEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(SmokeEnvVar), "1", StringComparison.Ordinal);

    private static bool IsLatestCanaryEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(LatestCanaryEnvVar), "1", StringComparison.Ordinal);

    private static string ResolveCommand(string name)
        => OperatingSystem.IsWindows() ? $"{name}.cmd" : name;

    private static string GetBridgeScriptPath()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "OpenClaw.Agent", "Plugins", "plugin-bridge.mjs"));
        Assert.True(File.Exists(path), $"Bridge script not found at {path}");
        return path;
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
