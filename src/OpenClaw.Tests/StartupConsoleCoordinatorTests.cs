using Microsoft.Extensions.Configuration;
using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

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
        coordinator.WriteConfigurationSummary(configuration, "Production", session, output);

        var text = output.ToString();
        Assert.Contains("Startup environment: Production", text, StringComparison.Ordinal);
        Assert.Contains("- appsettings.json", text, StringComparison.Ordinal);
        Assert.Contains("- appsettings.Production.json", text, StringComparison.Ordinal);
        Assert.Contains("- custom/openclaw.settings.json", text, StringComparison.Ordinal);
        Assert.Contains("- Session-only overrides: active (quickstart)", text, StringComparison.Ordinal);
    }
}
