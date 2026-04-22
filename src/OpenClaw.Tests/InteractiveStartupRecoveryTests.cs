using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

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

        var root = Path.Combine(Path.GetTempPath(), "openclaw-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousModelProviderKey = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousBind = Environment.GetEnvironmentVariable("OpenClaw__BindAddress");
        var previousPort = Environment.GetEnvironmentVariable("OpenClaw__Port");
        var previousMemoryProvider = Environment.GetEnvironmentVariable("OpenClaw__Memory__Provider");
        var previousMemoryPath = Environment.GetEnvironmentVariable("OpenClaw__Memory__StoragePath");
        var previousRetention = Environment.GetEnvironmentVariable("OpenClaw__Memory__Retention__Enabled");

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
                input: input,
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result);
            Assert.Equal("Development", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
            Assert.Equal("127.0.0.1", Environment.GetEnvironmentVariable("OpenClaw__BindAddress"));
            Assert.Equal("18789", Environment.GetEnvironmentVariable("OpenClaw__Port"));
            Assert.Equal("file", Environment.GetEnvironmentVariable("OpenClaw__Memory__Provider"));
            Assert.Equal(Path.Combine(root, "memory"), Environment.GetEnvironmentVariable("OpenClaw__Memory__StoragePath"));
            Assert.Equal("false", Environment.GetEnvironmentVariable("OpenClaw__Memory__Retention__Enabled"));
            Assert.Equal(root, Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", previousModelProviderKey);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Environment.SetEnvironmentVariable("OpenClaw__BindAddress", previousBind);
            Environment.SetEnvironmentVariable("OpenClaw__Port", previousPort);
            Environment.SetEnvironmentVariable("OpenClaw__Memory__Provider", previousMemoryProvider);
            Environment.SetEnvironmentVariable("OpenClaw__Memory__StoragePath", previousMemoryPath);
            Environment.SetEnvironmentVariable("OpenClaw__Memory__Retention__Enabled", previousRetention);
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

        var root = Path.Combine(Path.GetTempPath(), "openclaw-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousModelProviderKey = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        var previousOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

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
                input: input,
                output: output);

            Assert.Equal(StartupRecoveryResult.Recovered, result);
            Assert.Equal("sk-test-from-openai-env", Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY"));
            Assert.Contains("Using OPENAI_API_KEY from the current environment", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", previousModelProviderKey);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
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
