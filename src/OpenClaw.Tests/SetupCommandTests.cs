using System.Text.Json;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SetupCommandTests
{
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
            Assert.Contains("--doctor", stdout, StringComparison.Ordinal);
            Assert.Contains("admin posture", stdout, StringComparison.Ordinal);
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

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-setup-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
