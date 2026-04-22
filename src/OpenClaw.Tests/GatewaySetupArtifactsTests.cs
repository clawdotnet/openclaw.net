using OpenClaw.Core.Setup;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewaySetupArtifactsTests
{
    [Fact]
    public void BuildEnvExample_MatchesCliFormat()
    {
        var text = GatewaySetupArtifacts.BuildEnvExample(
            apiKeyRef: "env:OPENAI_API_KEY",
            authToken: "oc_test_token",
            workspacePath: "/tmp/workspace",
            baseUrl: "http://127.0.0.1:18789");

        Assert.Equal(
            "OPENAI_API_KEY=replace-me" + Environment.NewLine +
            "OPENCLAW_AUTH_TOKEN=oc_test_token" + Environment.NewLine +
            "OPENCLAW_BASE_URL=http://127.0.0.1:18789" + Environment.NewLine +
            "OPENCLAW_WORKSPACE=/tmp/workspace" + Environment.NewLine,
            text);
    }

    [Fact]
    public void CreateProfileConfig_PublicProfileKeepsExistingHardenedDefaults()
    {
        var warnings = new List<string>();
        var config = GatewaySetupProfileFactory.CreateProfileConfig(
            profile: "public",
            bindAddress: "0.0.0.0",
            port: 18789,
            authToken: "oc_test",
            workspacePath: "/tmp/workspace",
            memoryPath: "/tmp/memory",
            provider: "openai",
            model: "gpt-4o",
            apiKey: "env:OPENAI_API_KEY",
            warnings: warnings);

        Assert.False(config.Tooling.AllowShell);
        Assert.True(config.Tooling.RequireToolApproval);
        Assert.True(config.Security.TrustForwardedHeaders);
        Assert.True(config.Security.RequireRequesterMatchForHttpToolApproval);
        Assert.False(config.Plugins.Enabled);
        Assert.Contains(warnings, warning => warning.Contains("Public profile disables third-party bridge plugins", StringComparison.Ordinal));
    }
}
