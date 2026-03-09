using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PublicCompatibilitySmokeTests : IDisposable
{
    private const string SmokeEnvVar = "OPENCLAW_PUBLIC_SMOKE";
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

        var manifest = LoadManifest();
        foreach (var entry in manifest.Entries)
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

    private async Task VerifyClawHubSkillAsync(SmokeEntry entry)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.Slug), $"Smoke entry '{entry.Id}' must declare a skill slug.");
        Assert.False(string.IsNullOrWhiteSpace(entry.Version), $"Smoke entry '{entry.Id}' must declare a skill version.");
        Assert.False(string.IsNullOrWhiteSpace(entry.ExpectedRelativePath), $"Smoke entry '{entry.Id}' must declare expectedRelativePath.");

        var workdir = CreateScenarioDirectory(entry.Id);
        await RunCommandAsync(
            ResolveCommand("npx"),
            workdir,
            "-y", "clawhub",
            "--workdir", workdir,
            "--dir", "skills",
            "--no-input",
            "install", entry.Slug!,
            "--version", entry.Version!);

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

    private async Task VerifyNpmPluginAsync(SmokeEntry entry)
    {
        Assert.False(string.IsNullOrWhiteSpace(entry.Spec), $"Smoke entry '{entry.Id}' must declare an npm spec.");
        Assert.False(string.IsNullOrWhiteSpace(entry.PackageName), $"Smoke entry '{entry.Id}' must declare packageName.");
        Assert.False(string.IsNullOrWhiteSpace(entry.PluginId), $"Smoke entry '{entry.Id}' must declare pluginId.");
        Assert.False(string.IsNullOrWhiteSpace(entry.ExpectedStatus), $"Smoke entry '{entry.Id}' must declare expectedStatus.");

        var scenarioDir = CreateScenarioDirectory(entry.Id);
        var installDir = Path.Combine(scenarioDir, "npm");
        Directory.CreateDirectory(installDir);

        var packages = new List<string> { entry.Spec! };
        if (entry.InstallExtraPackages is { Length: > 0 })
            packages.AddRange(entry.InstallExtraPackages);
        await InstallPackagesAsync(installDir, packages);

        var packageDir = Path.Combine(installDir, "node_modules", entry.PackageName!
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(packageDir), $"Installed package directory '{packageDir}' was not found.");

        var workspaceDir = Path.Combine(scenarioDir, "workspace");
        Directory.CreateDirectory(workspaceDir);

        await using var host = CreateHost(BuildPluginConfig(entry, packageDir));
        var tools = await host.LoadAsync(workspaceDir, CancellationToken.None);
        var report = host.Reports.LastOrDefault(r => string.Equals(r.PluginId, entry.PluginId, StringComparison.Ordinal));
        Assert.NotNull(report);

        if (string.Equals(entry.ExpectedStatus, "compatible", StringComparison.Ordinal))
        {
            Assert.True(report!.Loaded, $"Expected plugin '{entry.Id}' to load. Error: {report.Error}");

            foreach (var toolName in entry.ExpectedToolNames ?? [])
                Assert.Contains(tools, tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));

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
        else if (string.Equals(entry.ExpectedStatus, "incompatible", StringComparison.Ordinal))
        {
            Assert.False(report!.Loaded, $"Expected plugin '{entry.Id}' to fail compatibility checks.");
            foreach (var diagnosticCode in entry.ExpectedDiagnosticCodes ?? [])
            {
                Assert.True(
                    report.Diagnostics.Any(diag => string.Equals(diag.Code, diagnosticCode, StringComparison.Ordinal)),
                    $"Expected plugin '{entry.Id}' to report diagnostic '{diagnosticCode}', but got: [{string.Join(", ", report.Diagnostics.Select(d => d.Code))}]");
            }
        }
        else
        {
            throw new Xunit.Sdk.XunitException($"Unsupported expectedStatus '{entry.ExpectedStatus}' for smoke entry '{entry.Id}'.");
        }
    }

    private static PluginsConfig BuildPluginConfig(SmokeEntry entry, string packageDir)
    {
        var config = new PluginsConfig
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [packageDir] }
        };

        if (!string.IsNullOrWhiteSpace(entry.ConfigJson))
        {
            config.Entries[entry.PluginId!] = new PluginEntryConfig
            {
                Config = JsonDocument.Parse(entry.ConfigJson).RootElement.Clone()
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

    private string CreateScenarioDirectory(string id)
    {
        var path = Path.Combine(_tempDir, id);
        Directory.CreateDirectory(path);
        return path;
    }

    private PluginHost CreateHost(PluginsConfig config)
        => new(config, GetBridgeScriptPath(), new TestLogger());

    private static SmokeManifest LoadManifest()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "compat", "public-smoke.json"));
        Assert.True(File.Exists(manifestPath), $"Public smoke manifest not found at {manifestPath}");

        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize(stream, SmokeJsonContext.Default.SmokeManifest);
        Assert.NotNull(manifest);
        return manifest!;
    }

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

public sealed class SmokeManifest
{
    public int Version { get; set; }
    public SmokeEntry[] Entries { get; set; } = [];
}

public sealed class SmokeEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Spec { get; set; }
    public string? PackageName { get; set; }
    public string? PluginId { get; set; }
    public string? Slug { get; set; }
    public string? Version { get; set; }
    public string? ExpectedStatus { get; set; }
    public string? ExpectedRelativePath { get; set; }
    public string? ConfigJson { get; set; }
    public string[]? InstallExtraPackages { get; set; }
    public string[]? ExpectedToolNames { get; set; }
    public string[]? ExpectedSkillNames { get; set; }
    public string[]? ExpectedDiagnosticCodes { get; set; }
}

[JsonSerializable(typeof(SmokeManifest))]
[JsonSerializable(typeof(SmokeEntry[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SmokeJsonContext : JsonSerializerContext
{
}
