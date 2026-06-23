using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayBootstrapExtensionsTests
{
    [Fact]
    public void LoadGatewayConfig_ConfiguredToolRootsReplaceWildcardDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:BindAddress"] = "0.0.0.0",
                ["OpenClaw:Tooling:AllowShell"] = "false",
                ["OpenClaw:Tooling:AllowedReadRoots:0"] = "/app/workspace",
                ["OpenClaw:Tooling:AllowedWriteRoots:0"] = "/app/workspace",
                ["OpenClaw:Plugins:Enabled"] = "false",
                ["OpenClaw:Canvas:Enabled"] = "false"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.Equal(["/app/workspace"], config.Tooling.AllowedReadRoots);
        Assert.Equal(["/app/workspace"], config.Tooling.AllowedWriteRoots);
        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void LoadGatewayConfig_HydratesPluginEntryConfigJson()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:Plugins:Entries:bridge-plugin:Enabled"] = "true",
                ["OpenClaw:Plugins:Entries:bridge-plugin:Config:apiKey"] = "secret",
                ["OpenClaw:Plugins:Entries:bridge-plugin:Config:retries"] = "3"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.True(config.Plugins.Entries.TryGetValue("bridge-plugin", out var entry));
        Assert.NotNull(entry!.Config);
        var element = entry.Config!.Value;
        Assert.Equal("secret", element.GetProperty("apiKey").GetString());
        Assert.Equal(3, element.GetProperty("retries").GetInt32());
    }

    [Fact]
    public void LoadGatewayConfig_HydratesDynamicNativePluginEntryConfigJson()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:Plugins:DynamicNative:Enabled"] = "true",
                ["OpenClaw:Plugins:DynamicNative:Entries:native-plugin:Enabled"] = "true",
                ["OpenClaw:Plugins:DynamicNative:Entries:native-plugin:Config:endpoint"] = "https://example.com",
                ["OpenClaw:Plugins:DynamicNative:Entries:native-plugin:Config:maxItems"] = "42"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.True(config.Plugins.DynamicNative.Entries.TryGetValue("native-plugin", out var entry));
        Assert.True(entry!.Enabled);
        Assert.NotNull(entry.Config);
        var element = entry.Config!.Value;
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("https://example.com", element.GetProperty("endpoint").GetString());
        Assert.Equal(42, element.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void ValidateOptionalFeatureCompatibility_OpenSandboxProviderConfigured_MatchesBuildFlag()
    {
        var config = new OpenClaw.Core.Models.GatewayConfig
        {
            Sandbox = new OpenClaw.Core.Models.SandboxConfig
            {
                Provider = OpenClaw.Core.Models.SandboxProviderNames.OpenSandbox,
                Endpoint = "http://127.0.0.1:5000"
            }
        };

        var errors = GatewayBootstrapExtensions.ValidateOptionalFeatureCompatibility(config);

        if (OptionalFeatureSupport.OpenSandboxEnabled)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(errors, error => error.Contains("OpenClawEnableOpenSandbox", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("Sandbox.Provider='None'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ValidateOptionalFeatureCompatibility_SandboxDisabled_ReturnsNoError()
    {
        var config = new OpenClaw.Core.Models.GatewayConfig
        {
            Sandbox = new OpenClaw.Core.Models.SandboxConfig
            {
                Provider = OpenClaw.Core.Models.SandboxProviderNames.None
            }
        };

        var errors = GatewayBootstrapExtensions.ValidateOptionalFeatureCompatibility(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void CheckedInGatewayConfigs_DisableSandboxByDefault()
    {
        var repoRoot = FindRepositoryRoot();
        var appSettings = File.ReadAllText(Path.Combine(repoRoot, "src", "OpenClaw.Gateway", "appsettings.json"));
        var productionSettings = File.ReadAllText(Path.Combine(repoRoot, "src", "OpenClaw.Gateway", "appsettings.Production.json"));

        Assert.Contains(@"""Provider"": ""None""", appSettings, StringComparison.Ordinal);
        Assert.Contains(@"""Provider"": ""None""", productionSettings, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
