using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class LocalStartupPostReadyActionsTests
{
    [Fact]
    public async Task RunAsync_SaveAccepted_WritesConfigAndEnvExampleAndMarksPromptShown()
    {
        var root = CreateTempRoot();
        var savePath = Path.Combine(root, "config", "openclaw.settings.json");
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var startup = CreateStartupContext();
        var session = CreateLocalSession();
        var browserOpenCount = 0;
        var previousConfigPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");

        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", null);
            var launchOptions = StartupLaunchOptions.Parse([]);
            using var output = new StringWriter();
            await LocalStartupPostReadyActions.RunAsync(
                startup,
                launchOptions,
                session,
                stateStore,
                NullLogger.Instance,
                CancellationToken.None,
                input: new StringReader("n\ny\n"),
                output: output,
                openBrowser: _ => browserOpenCount++,
                saveConfigPathOverride: savePath);

            Assert.Equal(0, browserOpenCount);
            Assert.True(File.Exists(savePath));
            Assert.True(File.Exists(Path.Combine(root, "config", "openclaw.settings.env.example")));

            var state = stateStore.Load();
            Assert.True(state.BrowserPromptShown);
            Assert.Equal(savePath, state.LastSavedConfigPath);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(savePath));
            var llm = document.RootElement.GetProperty("OpenClaw").GetProperty("llm");
            Assert.Equal("env:OPENAI_API_KEY", llm.GetProperty("apiKey").GetString());

            var envExample = await File.ReadAllTextAsync(Path.Combine(root, "config", "openclaw.settings.env.example"));
            Assert.Contains("OPENAI_API_KEY=replace-me", envExample, StringComparison.Ordinal);
            Assert.Contains("OPENCLAW_WORKSPACE=/tmp/workspace", envExample, StringComparison.Ordinal);
            Assert.Contains("Saved config:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", previousConfigPath);
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task RunAsync_SaveDeclined_DoesNotWriteConfig()
    {
        var root = CreateTempRoot();
        var savePath = Path.Combine(root, "config", "openclaw.settings.json");
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previousConfigPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");

        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", null);
            await LocalStartupPostReadyActions.RunAsync(
                CreateStartupContext(),
                StartupLaunchOptions.Parse([]),
                CreateLocalSession(),
                stateStore,
                NullLogger.Instance,
                CancellationToken.None,
                input: new StringReader("n\nn\n"),
                output: new StringWriter(),
                openBrowser: _ => { },
                saveConfigPathOverride: savePath);

            Assert.False(File.Exists(savePath));
            Assert.True(stateStore.Load().BrowserPromptShown);
            Assert.Null(stateStore.Load().LastSavedConfigPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", previousConfigPath);
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task RunAsync_BrowserPromptIsOnlyShownOnce()
    {
        var root = CreateTempRoot();
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previousConfigPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        Assert.True(stateStore.TrySave(new LocalStartupState
        {
            WorkspacePath = "/tmp/workspace",
            MemoryPath = "/tmp/memory",
            Port = 18789,
            Provider = "openai",
            Model = "gpt-4o",
            BrowserPromptShown = true
        }, out var saveError), saveError);
        var browserOpenCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", null);
            await LocalStartupPostReadyActions.RunAsync(
                CreateStartupContext(),
                StartupLaunchOptions.Parse(["--config", "./existing.json"]),
                CreateLocalSession(),
                stateStore,
                NullLogger.Instance,
                CancellationToken.None,
                input: new StringReader(string.Empty),
                output: new StringWriter(),
                openBrowser: _ => browserOpenCount++);

            Assert.Equal(0, browserOpenCount);
            Assert.True(stateStore.Load().BrowserPromptShown);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", previousConfigPath);
            DeleteDirectoryBestEffort(root);
        }
    }

    private static GatewayStartupContext CreateStartupContext()
        => new()
        {
            Config = new GatewayConfig
            {
                Llm = new LlmProviderConfig
                {
                    Provider = "openai",
                    Model = "gpt-4o"
                }
            },
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "auto",
                EffectiveMode = GatewayRuntimeMode.Aot,
                DynamicCodeSupported = false
            },
            IsNonLoopbackBind = false
        };

    private static LocalStartupSession CreateLocalSession()
        => new(
            Mode: "quickstart",
            WorkspacePath: "/tmp/workspace",
            MemoryPath: "/tmp/memory",
            Port: 18789,
            Provider: "openai",
            Model: "gpt-4o",
            ApiKeyReference: "env:OPENAI_API_KEY",
            Endpoint: null);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-post-ready-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
