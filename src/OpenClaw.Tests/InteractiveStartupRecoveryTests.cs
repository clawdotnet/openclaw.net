using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class InteractiveStartupRecoveryTests
{
    [Fact]
    public void TryRecover_MissingAuthToken_AppliesLocalSessionOverrides()
    {
        var startup = CreateStartupContext(config =>
        {
            config.BindAddress = "0.0.0.0";
            config.Port = 18789;
            config.Llm.Provider = "openai";
            config.Llm.Model = "gpt-4o";
        });

        var root = CreateTempRoot();
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previous = CaptureEnvironment();

        try
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", "test-key");
            var input = new StringReader("y\n\n\n\n");
            using var output = new StringWriter();

            var result = InteractiveStartupRecovery.TryRecover(
                new InvalidOperationException("OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address."),
                startup,
                environmentName: "Production",
                currentDirectory: root,
                canPrompt: true,
                stateStore: stateStore,
                suggestQuickstart: true,
                input: input,
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result.Result);
            Assert.NotNull(result.Session);
            Assert.Equal("recovery", result.Session!.Mode);
            Assert.Equal("Development", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
            Assert.Equal("127.0.0.1", Environment.GetEnvironmentVariable("OpenClaw__BindAddress"));
            Assert.Equal("18789", Environment.GetEnvironmentVariable("OpenClaw__Port"));
            Assert.Equal("file", Environment.GetEnvironmentVariable("OpenClaw__Memory__Provider"));
            Assert.Equal(Path.Combine(root, "memory"), Environment.GetEnvironmentVariable("OpenClaw__Memory__StoragePath"));
            Assert.Equal("false", Environment.GetEnvironmentVariable("OpenClaw__Memory__Retention__Enabled"));
            Assert.Equal(root, Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE"));
            Assert.Contains("--quickstart", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void TryRecover_PortConflict_OffersNextPortAndUsesIt()
    {
        var startup = CreateStartupContext(config =>
        {
            config.BindAddress = "127.0.0.1";
            config.Port = 18789;
            config.Llm.Provider = "openai";
            config.Llm.Model = "gpt-4o";
        });

        var root = CreateTempRoot();
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previous = CaptureEnvironment();

        try
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", "test-key");
            var input = new StringReader("y\ny\n\n\n\n");
            using var output = new StringWriter();

            var result = InteractiveStartupRecovery.TryRecover(
                new IOException("address already in use"),
                startup,
                environmentName: "Development",
                currentDirectory: root,
                canPrompt: true,
                stateStore: stateStore,
                suggestQuickstart: false,
                input: input,
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result.Result);
            Assert.Equal("18790", Environment.GetEnvironmentVariable("OpenClaw__Port"));
            Assert.Contains("Port 18789 is busy. Use 18790 instead?", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void TryRecover_MissingProviderKey_UsesOpenAiApiKeyFromEnvironment()
    {
        var startup = CreateStartupContext(config =>
        {
            config.Llm.Provider = "openai";
            config.Llm.Model = "gpt-4o";
        });

        var root = CreateTempRoot();
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previous = CaptureEnvironment();

        try
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-from-openai-env");
            var input = new StringReader("y\n\n\n\n");
            using var output = new StringWriter();

            var result = InteractiveStartupRecovery.TryRecover(
                new InvalidOperationException(
                    "Configured provider 'openai' is not available. Built-in provider initialization failed: MODEL_PROVIDER_KEY must be set for the OpenAI provider.. Register it as the built-in provider or via a compatible plugin."),
                startup,
                environmentName: "Development",
                currentDirectory: root,
                canPrompt: true,
                stateStore: stateStore,
                suggestQuickstart: false,
                input: input,
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result.Result);
            Assert.Equal("env:OPENAI_API_KEY", result.Session!.ApiKeyReference);
            Assert.Equal("sk-test-from-openai-env", Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY"));
            Assert.Contains("Using OPENAI_API_KEY from the current environment", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void TryQuickstart_UsesRememberedDefaults()
    {
        var root = CreateTempRoot();
        var stateStore = new LocalStartupStateStore(Path.Combine(root, "state.json"));
        var previous = CaptureEnvironment();

        try
        {
            Assert.True(stateStore.TrySave(new LocalStartupState
            {
                WorkspacePath = Path.Combine(root, "remembered-workspace"),
                MemoryPath = Path.Combine(root, "remembered-memory"),
                Port = 18888,
                Provider = "openai",
                Model = "gpt-4.1",
                BrowserPromptShown = false
            }, out var saveError), saveError);

            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-remembered");
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", null);
            using var output = new StringWriter();

            var result = InteractiveStartupRecovery.TryQuickstart(
                currentDirectory: root,
                stateStore: stateStore,
                input: new StringReader(string.Empty),
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result.Result);
            Assert.Equal("quickstart", result.Session!.Mode);
            Assert.Equal(Path.Combine(root, "remembered-workspace"), result.Session.WorkspacePath);
            Assert.Equal(Path.Combine(root, "remembered-memory"), result.Session.MemoryPath);
            Assert.Equal(18888, result.Session.Port);
            Assert.Equal("gpt-4.1", result.Session.Model);
            Assert.Equal("env:OPENAI_API_KEY", result.Session.ApiKeyReference);
            Assert.Equal("18888", Environment.GetEnvironmentVariable("OpenClaw__Port"));
        }
        finally
        {
            RestoreEnvironment(previous);
            DeleteDirectoryBestEffort(root);
        }
    }

    private static Dictionary<string, string?> CaptureEnvironment()
        => new()
        {
            ["MODEL_PROVIDER_KEY"] = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY"),
            ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            ["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            ["OPENCLAW_WORKSPACE"] = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE"),
            ["OpenClaw__BindAddress"] = Environment.GetEnvironmentVariable("OpenClaw__BindAddress"),
            ["OpenClaw__Port"] = Environment.GetEnvironmentVariable("OpenClaw__Port"),
            ["OpenClaw__Memory__Provider"] = Environment.GetEnvironmentVariable("OpenClaw__Memory__Provider"),
            ["OpenClaw__Memory__StoragePath"] = Environment.GetEnvironmentVariable("OpenClaw__Memory__StoragePath"),
            ["OpenClaw__Memory__Retention__Enabled"] = Environment.GetEnvironmentVariable("OpenClaw__Memory__Retention__Enabled"),
            ["MODEL_PROVIDER_ENDPOINT"] = Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT")
        };

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> snapshot)
    {
        foreach (var (key, value) in snapshot)
            Environment.SetEnvironmentVariable(key, value);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-recovery-tests", Guid.NewGuid().ToString("N"));
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

    private static GatewayStartupContext CreateStartupContext(Action<GatewayConfig>? configure = null)
    {
        var config = new GatewayConfig();
        configure?.Invoke(config);
        return new GatewayStartupContext
        {
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "auto",
                EffectiveMode = GatewayRuntimeMode.Aot,
                DynamicCodeSupported = false
            },
            IsNonLoopbackBind = !OpenClaw.Core.Security.BindAddressClassifier.IsLoopbackBind(config.BindAddress),
            WorkspacePath = null
        };
    }
}
