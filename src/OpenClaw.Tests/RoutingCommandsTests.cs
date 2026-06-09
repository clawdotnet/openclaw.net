using OpenClaw.Cli;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class RoutingCommandsTests
{
    [Fact]
    public async Task RoutingCommands_Help_ListsRequiredSubcommands()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await RoutingCommands.RunAsync(["--help"], output, error);

        var text = output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("onboard", text, StringComparison.Ordinal);
        Assert.Contains("configure", text, StringComparison.Ordinal);
        Assert.Contains("providers", text, StringComparison.Ordinal);
        Assert.Contains("status", text, StringComparison.Ordinal);
        Assert.Contains("diagnostics", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutingCommands_UnknownCommand_ReturnsTwo()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await RoutingCommands.RunAsync(["unknown"], output, error);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Program_Help_ListsRoutingCommand()
    {
        var previousOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var exitCode = await OpenClaw.Cli.Program.Main(["--help"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("openclaw routing <onboard|configure|providers|status|diagnostics> [options]", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
    }

    [Fact]
    public async Task RoutingCommands_DiagnosticsOn_PersistsConfigToggle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-routing-{Guid.NewGuid():N}.json");
        try
        {
            var config = new GatewayConfig();
            await GatewayConfigFile.SaveAsync(config, path);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await RoutingCommands.RunAsync(["diagnostics", "on", "--config", path], output, error);

            Assert.Equal(0, exit);
            var saved = GatewayConfigFile.Load(path);
            Assert.True(saved.DynamicTurnRouting.Policy.EnableDiagnostics);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RoutingCommands_ConfigureProviders_UpdatesTierTargetFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-routing-{Guid.NewGuid():N}.json");
        try
        {
            var config = new GatewayConfig();
            await GatewayConfigFile.SaveAsync(config, path);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await RoutingCommands.RunAsync(
            [
                "configure", "providers",
                "--config", path,
                "--tier", "T2",
                "--model-profile", "frontier-tools",
                "--fallback-profile", "frontier-fallback",
                "--reasoning-level", "high",
                "--response-policy", "detailed",
                "--image-model-profile", "vision-frontier",
                "--allowed-tools", "read_file,run_in_terminal",
                "--preferred-tags", "tools,fast"
            ], output, error);

            Assert.Equal(0, exit);
            var saved = GatewayConfigFile.Load(path);
            var tier = saved.DynamicTurnRouting.Policy.Tiers.T2;
            Assert.Equal("frontier-tools", tier.ModelProfileId);
            Assert.Equal("frontier-fallback", tier.DirectModelFallbackProfileId);
            Assert.Equal("high", tier.ReasoningLevel);
            Assert.Equal("detailed", tier.ResponsePolicy);
            Assert.Equal("vision-frontier", tier.ImageCapableModelProfileId);
            Assert.Equal(["read_file", "run_in_terminal"], tier.AllowedTools);
            Assert.Equal(["tools", "fast"], tier.PreferredTags);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RoutingCommands_ConfigureRouter_Disabled_TurnsRoutingOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-routing-{Guid.NewGuid():N}.json");
        try
        {
            var config = new GatewayConfig
            {
                DynamicTurnRouting = new DynamicTurnRoutingConfig
                {
                    Enabled = true
                }
            };
            await GatewayConfigFile.SaveAsync(config, path);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await RoutingCommands.RunAsync(["configure", "router", "--router", "disabled", "--config", path], output, error);

            Assert.Equal(0, exit);
            var saved = GatewayConfigFile.Load(path);
            Assert.False(saved.DynamicTurnRouting.Enabled);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RoutingCommands_ConfigureRouter_OpenRouterMix_AddsPreferredTags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-routing-{Guid.NewGuid():N}.json");
        try
        {
            var config = new GatewayConfig();
            await GatewayConfigFile.SaveAsync(config, path);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await RoutingCommands.RunAsync(["configure", "router", "--router", "openrouter-mix", "--config", path], output, error);

            Assert.Equal(0, exit);
            var saved = GatewayConfigFile.Load(path);
            Assert.True(saved.DynamicTurnRouting.Enabled);
            Assert.Contains("openrouter", saved.DynamicTurnRouting.Policy.Tiers.T0.PreferredTags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("openrouter", saved.DynamicTurnRouting.Policy.Tiers.T1.PreferredTags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("openrouter", saved.DynamicTurnRouting.Policy.Tiers.T2.PreferredTags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("openrouter", saved.DynamicTurnRouting.Policy.Tiers.T3.PreferredTags, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RoutingCommands_Onboard_DefaultsToRecommendedAndEnablesRouting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-routing-{Guid.NewGuid():N}.json");
        try
        {
            var config = new GatewayConfig
            {
                DynamicTurnRouting = new DynamicTurnRoutingConfig
                {
                    Enabled = false
                }
            };
            await GatewayConfigFile.SaveAsync(config, path);

            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await RoutingCommands.RunAsync(["onboard", "--config", path], output, error);

            Assert.Equal(0, exit);
            var saved = GatewayConfigFile.Load(path);
            Assert.True(saved.DynamicTurnRouting.Enabled);
            Assert.Contains("router=recommended", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
