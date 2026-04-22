using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

public sealed class StartupFailureReporterTests
{
    [Fact]
    public void Render_MissingAuthToken_ShowsHelpfulBindGuidance()
    {
        var text = StartupFailureReporter.Render(
            new InvalidOperationException("OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address."),
            startup: null,
            environmentName: "Production",
            isDoctorMode: false);

        Assert.Contains("Startup failed: authentication is required for a non-loopback bind.", text, StringComparison.Ordinal);
        Assert.Contains("Current ASPNETCORE_ENVIRONMENT: Production", text, StringComparison.Ordinal);
        Assert.Contains("OpenClaw__BindAddress to 127.0.0.1", text, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT to Development", text, StringComparison.Ordinal);
        Assert.Contains("OPENCLAW_AUTH_TOKEN", text, StringComparison.Ordinal);
        Assert.Contains("--doctor", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ReadOnlyStorage_ShowsWritablePathGuidance()
    {
        var startup = CreateStartupContext(config =>
        {
            config.Memory.Provider = "sqlite";
            config.Memory.StoragePath = "/app/memory";
            config.Memory.Sqlite.DbPath = "/app/memory/openclaw.db";
            config.Memory.Retention.Enabled = true;
            config.Memory.Retention.ArchivePath = "/app/memory/archive";
        });

        var text = StartupFailureReporter.Render(
            new InvalidOperationException(
                "Memory.StoragePath '/app/memory' is not writable. Failed to create '/app/memory/admin/keys'.",
                new IOException("Read-only file system : '/app'")),
            startup,
            environmentName: "Production",
            isDoctorMode: false);

        Assert.Contains("Startup failed: the configured memory storage path is not writable.", text, StringComparison.Ordinal);
        Assert.Contains("Memory.StoragePath: /app/memory", text, StringComparison.Ordinal);
        Assert.Contains("Memory.Sqlite.DbPath: /app/memory/openclaw.db", text, StringComparison.Ordinal);
        Assert.Contains("Memory.Retention.ArchivePath: /app/memory/archive", text, StringComparison.Ordinal);
        Assert.Contains("OpenClaw__Memory__StoragePath", text, StringComparison.Ordinal);
        Assert.Contains("OpenClaw__Memory__Sqlite__DbPath", text, StringComparison.Ordinal);
        Assert.Contains("OpenClaw__Memory__Retention__ArchivePath", text, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT to Development", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MissingProviderKey_ShowsModelProviderGuidance()
    {
        var startup = CreateStartupContext(config =>
        {
            config.Llm.Provider = "openai";
            config.Llm.Model = "gpt-4o";
        });

        var text = StartupFailureReporter.Render(
            new InvalidOperationException(
                "Configured provider 'openai' is not available. Built-in provider initialization failed: MODEL_PROVIDER_KEY must be set for the OpenAI provider.. Register it as the built-in provider or via a compatible plugin."),
            startup,
            environmentName: "Development",
            isDoctorMode: false);

        Assert.Contains("Startup failed: OpenAI provider credentials are missing.", text, StringComparison.Ordinal);
        Assert.Contains("Provider: openai", text, StringComparison.Ordinal);
        Assert.Contains("Model: gpt-4o", text, StringComparison.Ordinal);
        Assert.Contains("MODEL_PROVIDER_KEY", text, StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY", text, StringComparison.Ordinal);
        Assert.Contains("--doctor", text, StringComparison.Ordinal);
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
