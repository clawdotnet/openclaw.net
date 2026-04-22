using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SetupCommandTests
{
    [Fact]
    public void BuildLaunchUrls_IncludesChatAndAdminRoutes()
    {
        var urls = SetupLifecycleCommand.BuildLaunchUrls("http://127.0.0.1:18789/");

        Assert.Equal(
            [
                ("Gateway URL", "http://127.0.0.1:18789"),
                ("Chat URL", "http://127.0.0.1:18789/chat"),
                ("Admin URL", "http://127.0.0.1:18789/admin")
            ],
            urls);
    }

    [Fact]
    public async Task RunAsync_NonInteractiveLocalProfile_WritesConfigAndEnvExample()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var workspace = Path.Combine(root, "workspace");
            using var output = new StringWriter();
            using var error = new StringWriter();

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
                output,
                error,
                root,
                canPrompt: false);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(File.Exists(configPath));
            Assert.True(File.Exists(Path.Combine(root, "config", "openclaw.settings.env.example")));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var openClaw = document.RootElement.GetProperty("OpenClaw");
            Assert.Equal("127.0.0.1", openClaw.GetProperty("bindAddress").GetString());

            var tooling = openClaw.GetProperty("tooling");
            Assert.True(tooling.GetProperty("workspaceOnly").GetBoolean());
            Assert.True(tooling.GetProperty("allowShell").GetBoolean());
            Assert.Equal(workspace, tooling.GetProperty("workspaceRoot").GetString());
            Assert.Equal(workspace, tooling.GetProperty("allowedReadRoots")[0].GetString());
            Assert.Equal(workspace, tooling.GetProperty("allowedWriteRoots")[0].GetString());

            var memory = openClaw.GetProperty("memory");
            Assert.Equal(Path.Combine(root, "config", "memory"), memory.GetProperty("storagePath").GetString());

            var envExample = await File.ReadAllTextAsync(Path.Combine(root, "config", "openclaw.settings.env.example"));
            Assert.Contains("OPENAI_API_KEY=replace-me", envExample, StringComparison.Ordinal);
            Assert.Contains($"OPENCLAW_WORKSPACE={workspace}", envExample, StringComparison.Ordinal);

            var stdout = output.ToString();
            Assert.Contains("Config validation: passed", stdout, StringComparison.Ordinal);
            Assert.Contains("openclaw setup verify", stdout, StringComparison.Ordinal);
            Assert.Contains("--doctor", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("OPENCLAW_AUTH_TOKEN=", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NonInteractivePublicProfile_WritesHardenedDefaults()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.public.json");
            var workspace = Path.Combine(root, "workspace");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SetupCommand.RunAsync(
                [
                    "--non-interactive",
                    "--profile", "public",
                    "--config", configPath,
                    "--workspace", workspace,
                    "--provider", "openai",
                    "--model", "gpt-4o",
                    "--api-key", "env:OPENAI_API_KEY"
                ],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var openClaw = document.RootElement.GetProperty("OpenClaw");
            Assert.Equal("0.0.0.0", openClaw.GetProperty("bindAddress").GetString());

            var tooling = openClaw.GetProperty("tooling");
            Assert.False(tooling.GetProperty("allowShell").GetBoolean());
            Assert.True(tooling.GetProperty("requireToolApproval").GetBoolean());

            var security = openClaw.GetProperty("security");
            Assert.True(security.GetProperty("trustForwardedHeaders").GetBoolean());
            Assert.True(security.GetProperty("requireRequesterMatchForHttpToolApproval").GetBoolean());

            var plugins = openClaw.GetProperty("plugins");
            Assert.False(plugins.GetProperty("enabled").GetBoolean());

            var stdout = output.ToString();
            Assert.Contains("Warnings:", stdout, StringComparison.Ordinal);
            Assert.Contains("Public profile disables third-party bridge plugins by default.", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NonInteractiveWithoutProfile_FailsFast()
    {
        var root = CreateTempRoot();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SetupCommand.RunAsync(
                ["--non-interactive"],
                new StringReader(string.Empty),
                output,
                error,
                root,
                canPrompt: false);

            Assert.Equal(2, exitCode);
            Assert.Contains("--profile is required when --non-interactive is set.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_InteractiveMode_UsesPromptedValues()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "wizard.json");
            var workspace = Path.Combine(root, "workspace");
            var inputText = string.Join(
                Environment.NewLine,
                [
                    "local",
                    configPath,
                    workspace,
                    "anthropic",
                    "claude-sonnet-4-5",
                    "env:ANTHROPIC_API_KEY",
                    "127.0.0.1",
                    "18801",
                    "oc_interactive_token",
                    "docker",
                    "python:3.12-slim"
                ]) + Environment.NewLine;

            using var input = new StringReader(inputText);
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SetupCommand.RunAsync(
                [],
                input,
                output,
                error,
                root,
                canPrompt: true);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var openClaw = document.RootElement.GetProperty("OpenClaw");
            var llm = openClaw.GetProperty("llm");
            Assert.Equal("anthropic", llm.GetProperty("provider").GetString());
            Assert.Equal("claude-sonnet-4-5", llm.GetProperty("model").GetString());
            Assert.Equal("env:ANTHROPIC_API_KEY", llm.GetProperty("apiKey").GetString());
            Assert.Equal("oc_interactive_token", openClaw.GetProperty("authToken").GetString());

            var execution = openClaw.GetProperty("execution");
            Assert.Equal("docker", execution.GetProperty("profiles").GetProperty("docker").GetProperty("type").GetString());
            Assert.Equal("docker", execution.GetProperty("tools").GetProperty("shell").GetProperty("backend").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ServiceAndStatus_WriteAndReportArtifacts()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var workspace = Path.Combine(root, "workspace");
            using var setupOutput = new StringWriter();
            using var setupError = new StringWriter();

            var setupExitCode = await SetupCommand.RunAsync(
                [
                    "--non-interactive",
                    "--profile", "public",
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

            using var serviceOutput = new StringWriter();
            using var serviceError = new StringWriter();
            var serviceExitCode = await SetupCommand.RunAsync(
                ["service", "--config", configPath, "--platform", "all"],
                new StringReader(string.Empty),
                serviceOutput,
                serviceError,
                LocateRepoRoot(),
                canPrompt: false);

            Assert.Equal(0, serviceExitCode);
            Assert.Equal(string.Empty, serviceError.ToString());

            var deployDir = Path.Combine(root, "config", "deploy");
            Assert.True(File.Exists(Path.Combine(deployDir, "openclaw-gateway.service")));
            Assert.True(File.Exists(Path.Combine(deployDir, "openclaw-companion.service")));
            Assert.True(File.Exists(Path.Combine(deployDir, "ai.openclaw.gateway.plist")));
            Assert.True(File.Exists(Path.Combine(deployDir, "ai.openclaw.companion.plist")));
            Assert.True(File.Exists(Path.Combine(deployDir, "Caddyfile")));

            using var statusOutput = new StringWriter();
            using var statusError = new StringWriter();
            var statusExitCode = await SetupCommand.RunAsync(
                ["status", "--config", configPath],
                new StringReader(string.Empty),
                statusOutput,
                statusError,
                root,
                canPrompt: false);

            Assert.Equal(0, statusExitCode);
            Assert.Equal(string.Empty, statusError.ToString());
            var statusText = statusOutput.ToString();
            Assert.Contains("Gateway systemd unit: present", statusText, StringComparison.Ordinal);
            Assert.Contains("Caddy reverse proxy recipe: present", statusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_VerifyOffline_SucceedsWithoutLiveProviderProbe()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var workspace = Path.Combine(root, "workspace");
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

            using var verifyOutput = new StringWriter();
            using var verifyError = new StringWriter();
            var verifyExitCode = await SetupCommand.RunAsync(
                ["verify", "--config", configPath, "--offline"],
                new StringReader(string.Empty),
                verifyOutput,
                verifyError,
                root,
                canPrompt: false);

            Assert.Equal(0, verifyExitCode);
            Assert.Equal(string.Empty, verifyError.ToString());
            Assert.Contains("Setup verification:", verifyOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Provider smoke skipped because offline mode is enabled.", verifyOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_VerifyJson_PersistsSnapshotIntoSetupStatus()
    {
        var root = CreateTempRoot();
        try
        {
            var configPath = Path.Combine(root, "config", "openclaw.settings.json");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
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

            using var verifyOutput = new StringWriter();
            using var verifyError = new StringWriter();
            var verifyExitCode = await SetupCommand.RunAsync(
                ["verify", "--config", configPath, "--offline", "--json"],
                new StringReader(string.Empty),
                verifyOutput,
                verifyError,
                root,
                canPrompt: false);

            Assert.Equal(0, verifyExitCode);
            Assert.Equal(string.Empty, verifyError.ToString());

            var verification = JsonSerializer.Deserialize(verifyOutput.ToString(), CoreJsonContext.Default.SetupVerificationResponse);
            Assert.NotNull(verification);
            var providerSmoke = Assert.Single(verification!.Checks, static item => item.Id == "provider_smoke");
            Assert.Equal(SetupCheckStates.Skip, providerSmoke.Status);

            var status = SetupLifecycleCommand.BuildStatus(configPath, GatewayConfigFile.Load(configPath));
            Assert.Equal(SetupVerificationSources.Cli, status.LastVerificationSource);
            Assert.Equal(verification.OverallStatus, status.LastVerificationStatus);
            Assert.Equal(SetupCheckStates.Skip, status.ProviderSmokeStatus);
            Assert.True(status.LastVerificationAtUtc.HasValue);
            Assert.False(status.LastVerificationHasFailures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ServiceWithConfigPathContainingSpaces_KeepsLaunchdArgumentsIntact()
    {
        var root = CreateTempRoot();
        try
        {
            var configDirectory = Path.Combine(root, "config dir");
            var configPath = Path.Combine(configDirectory, "openclaw settings.json");
            var workspace = Path.Combine(root, "workspace");
            using var setupOutput = new StringWriter();
            using var setupError = new StringWriter();

            var setupExitCode = await SetupCommand.RunAsync(
                [
                    "--non-interactive",
                    "--profile", "public",
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

            using var serviceOutput = new StringWriter();
            using var serviceError = new StringWriter();
            var serviceExitCode = await SetupCommand.RunAsync(
                ["service", "--config", configPath, "--platform", "macos"],
                new StringReader(string.Empty),
                serviceOutput,
                serviceError,
                LocateRepoRoot(),
                canPrompt: false);

            Assert.Equal(0, serviceExitCode);
            Assert.Equal(string.Empty, serviceError.ToString());

            var plistPath = Path.Combine(configDirectory, "deploy", "ai.openclaw.gateway.plist");
            var plist = await File.ReadAllTextAsync(plistPath);
            Assert.Contains("<string>--config</string>", plist, StringComparison.Ordinal);
            Assert.Contains($"<string>{System.Security.SecurityElement.Escape(configPath)}</string>", plist, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-setup-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "OpenClaw.Gateway", "OpenClaw.Gateway.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
