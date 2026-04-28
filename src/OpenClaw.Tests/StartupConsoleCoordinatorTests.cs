using Microsoft.Extensions.Configuration;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class StartupConsoleCoordinatorTests
{
    [Fact]
    public void WriteConfigurationSummary_IncludesEnvironmentSourcesAndSessionOverrides()
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile("appsettings.json", optional: true);
        configuration.AddJsonFile("appsettings.Production.json", optional: true);
        configuration.AddJsonFile("custom/openclaw.settings.json", optional: true);
        var coordinator = new StartupConsoleCoordinator();
        var session = new LocalStartupSession(
            Mode: "quickstart",
            WorkspacePath: "/tmp/workspace",
            MemoryPath: "/tmp/memory",
            Port: 18789,
            Provider: "openai",
            Model: "gpt-4o",
            ApiKeyReference: "env:OPENAI_API_KEY",
            Endpoint: null);

        using var output = new StringWriter();
        coordinator.WriteConfigurationSummary(configuration, new GatewayConfig(), "Production", session, output);

        var text = output.ToString();
        Assert.Contains("Startup environment: Production", text, StringComparison.Ordinal);
        Assert.Contains("- appsettings.json", text, StringComparison.Ordinal);
        Assert.Contains("- appsettings.Production.json", text, StringComparison.Ordinal);
        Assert.Contains("- custom/openclaw.settings.json", text, StringComparison.Ordinal);
        Assert.Contains("- Session-only overrides: active (quickstart)", text, StringComparison.Ordinal);
        Assert.Contains("Effective configuration winners:", text, StringComparison.Ordinal);
        Assert.Contains("- Bind address:", text, StringComparison.Ordinal);
        Assert.Contains("- API key: not configured", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationSourceDiagnostics_RedactsApiKeyAndReportsCompatibilityOverride()
    {
        var previousKey = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        try
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", "sk-test-secret");

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:BindAddress"] = "0.0.0.0",
                ["OpenClaw:Llm:Provider"] = "openai",
                ["OpenClaw:Llm:Model"] = "gpt-4o"
            });

            var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);
            var diagnostics = ConfigurationSourceDiagnosticsBuilder.Build(configuration, config);
            var text = ConfigurationSourceDiagnosticsBuilder.Render(diagnostics);

            Assert.Contains("Bind address: 0.0.0.0", text, StringComparison.Ordinal);
            Assert.Contains("Provider: openai", text, StringComparison.Ordinal);
            Assert.Contains("API key: configured (redacted)", text, StringComparison.Ordinal);
            Assert.Contains("MODEL_PROVIDER_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-test-secret", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", previousKey);
        }
    }
}
