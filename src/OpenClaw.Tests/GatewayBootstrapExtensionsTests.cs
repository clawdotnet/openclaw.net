using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

[Collection(ResolverAccessorCollection.Name)]
public sealed class GatewayBootstrapExtensionsTests
{
    [Fact]
    public void LoadGatewayConfig_AuthTokenEnvironmentFallback_UsesExplicitReference()
    {
        const string environmentValue = "gateway-token-marker";
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", environmentValue);
        try
        {
            var config = GatewayBootstrapExtensions.LoadGatewayConfig(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                loadPersistedSettings: false);

            Assert.Null(config.AuthToken);
            Assert.Equal("env:OPENCLAW_AUTH_TOKEN", config.AuthTokenRef);

            var explicitConfig = GatewayBootstrapExtensions.LoadGatewayConfig(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OpenClaw:AuthToken"] = "explicit-token",
                        ["OpenClaw:AuthTokenRef"] = "vault:secret/data/test#auth_token"
                    })
                    .Build(),
                loadPersistedSettings: false);

            Assert.Equal("explicit-token", explicitConfig.AuthToken);
            Assert.Equal("vault:secret/data/test#auth_token", explicitConfig.AuthTokenRef);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", previous);
        }
    }

    [Fact]
    public void LoadGatewayConfig_LegacyAdminCredentialsRemainLocalAndAreRejectedOnPublicBind()
    {
        const string verifyToken = "legacy-verify-marker";
        const string cloudToken = "legacy-cloud-marker";
        const string proxyPassword = "legacy-proxy-password-marker";
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-legacy-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        try
        {
            var filePath = AdminSettingsService.GetSettingsPath(new GatewayConfig
            {
                Memory = new MemoryConfig { StoragePath = storagePath }
            });
            var legacySnapshot = new AdminSettingsSnapshot
            {
                WhatsAppWebhookVerifyToken = verifyToken,
                WhatsAppCloudApiToken = cloudToken,
                WhatsAppFirstPartyWorker = new WhatsAppFirstPartyWorkerConfig
                {
                    Proxy = $"http://user:{proxyPassword}@proxy.example.test:8080"
                }
            };
            var legacyFields = AdminSettingsService.FindLegacyInlineCredentialPaths(legacySnapshot);
            Assert.Contains("OpenClaw:Channels:WhatsApp:WebhookVerifyToken", legacyFields);
            Assert.Contains("OpenClaw:Channels:WhatsApp:FirstPartyWorker:Proxy", legacyFields);
            Assert.DoesNotContain(verifyToken, string.Join(", ", legacyFields), StringComparison.Ordinal);
            Assert.DoesNotContain(proxyPassword, string.Join(", ", legacyFields), StringComparison.Ordinal);
            var originalJson = JsonSerializer.Serialize(legacySnapshot, CoreJsonContext.Default.AdminSettingsSnapshot);
            File.WriteAllText(filePath, originalJson);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenClaw:BindAddress"] = "127.0.0.1",
                    ["OpenClaw:Memory:StoragePath"] = storagePath
                })
                .Build();

            var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

            Assert.Equal(verifyToken, config.Channels.WhatsApp.WebhookVerifyToken);
            Assert.Equal(cloudToken, config.Channels.WhatsApp.CloudApiToken);
            Assert.Equal($"http://user:{proxyPassword}@proxy.example.test:8080", config.Channels.WhatsApp.FirstPartyWorker.Proxy);
            Assert.Equal(originalJson, File.ReadAllText(filePath));

            var error = Assert.Throws<InvalidOperationException>(
                () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));
            Assert.DoesNotContain(verifyToken, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(cloudToken, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(proxyPassword, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void LoadGatewayConfig_RedactedAdminCredentials_PreservesBaseConfigurationValues()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-redacted-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        try
        {
            var filePath = AdminSettingsService.GetSettingsPath(new GatewayConfig
            {
                Memory = new MemoryConfig { StoragePath = storagePath }
            });
            var persistedSnapshot = AdminSettingsService.SanitizeSnapshot(new AdminSettingsSnapshot
            {
                WhatsAppWebhookVerifyToken = "persisted-verify",
                WhatsAppWebhookVerifyTokenRef = "",
                WhatsAppWebhookAppSecret = "persisted-app-secret",
                WhatsAppCloudApiToken = "persisted-cloud-token",
                WhatsAppBridgeToken = "persisted-bridge-token",
                WhatsAppFirstPartyWorker = new WhatsAppFirstPartyWorkerConfig
                {
                    Proxy = "http://persisted-user:persisted-password@proxy.example.test:8080",
                    Accounts =
                    [
                        new WhatsAppWorkerAccountConfig
                        {
                            AccountId = "base-account",
                            Proxy = "http://persisted-user:persisted-password@proxy.example.test:8080"
                        }
                    ]
                }
            });
            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(persistedSnapshot, CoreJsonContext.Default.AdminSettingsSnapshot));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenClaw:BindAddress"] = "127.0.0.1",
                    ["OpenClaw:Memory:StoragePath"] = storagePath,
                    ["OpenClaw:Channels:WhatsApp:WebhookVerifyToken"] = "base-verify",
                    ["OpenClaw:Channels:WhatsApp:WebhookAppSecret"] = "base-app-secret",
                    ["OpenClaw:Channels:WhatsApp:CloudApiToken"] = "base-cloud-token",
                    ["OpenClaw:Channels:WhatsApp:BridgeToken"] = "base-bridge-token",
                    ["OpenClaw:Channels:WhatsApp:FirstPartyWorker:Proxy"] = "env:BASE_PROXY",
                    ["OpenClaw:Channels:WhatsApp:FirstPartyWorker:Accounts:0:AccountId"] = "base-account",
                    ["OpenClaw:Channels:WhatsApp:FirstPartyWorker:Accounts:0:Proxy"] = "env:ACCOUNT_PROXY"
                })
                .Build();

            var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

            Assert.Equal("base-verify", config.Channels.WhatsApp.WebhookVerifyToken);
            Assert.Equal("base-app-secret", config.Channels.WhatsApp.WebhookAppSecret);
            Assert.Equal("base-cloud-token", config.Channels.WhatsApp.CloudApiToken);
            Assert.Equal("base-bridge-token", config.Channels.WhatsApp.BridgeToken);
            Assert.Equal("env:BASE_PROXY", config.Channels.WhatsApp.FirstPartyWorker.Proxy);
            Assert.Equal("env:ACCOUNT_PROXY", Assert.Single(config.Channels.WhatsApp.FirstPartyWorker.Accounts).Proxy);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void LoadGatewayConfig_ModelProviderEnvironmentFallback_UsesExplicitReference()
    {
        const string environmentValue = "model-key-marker";
        var previous = Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", environmentValue);
        try
        {
            var config = GatewayBootstrapExtensions.LoadGatewayConfig(
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                loadPersistedSettings: false);

            Assert.Equal("env:MODEL_PROVIDER_KEY", config.Llm.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_PROVIDER_KEY", previous);
        }
    }

    [Fact]
    public void EnforcePublicBindAuthTokenSource_InlineValue_ThrowsWithoutEchoingValue()
    {
        const string token = "inline-token-marker";
        var directOnly = new OpenClaw.Core.Models.GatewayConfig { AuthToken = token };
        var directAndReference = new OpenClaw.Core.Models.GatewayConfig
        {
            AuthToken = token,
            AuthTokenRef = "vault:secret/data/test#auth_token"
        };

        foreach (var config in new[] { directOnly, directAndReference })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => GatewayBootstrapExtensions.EnforcePublicBindAuthTokenSource(config, isNonLoopbackBind: true));

            Assert.Contains("OpenClaw:AuthToken", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
        }

        GatewayBootstrapExtensions.EnforcePublicBindAuthTokenSource(
            new OpenClaw.Core.Models.GatewayConfig { AuthTokenRef = "vault:secret/data/test#auth_token" },
            isNonLoopbackBind: true);
        GatewayBootstrapExtensions.EnforcePublicBindAuthTokenSource(
            new OpenClaw.Core.Models.GatewayConfig { Security = new() { AuthMode = "oidc" } },
            isNonLoopbackBind: true);
        GatewayBootstrapExtensions.EnforcePublicBindAuthTokenSource(directOnly, isNonLoopbackBind: false);
    }

    [Fact]
    public void CreateHealthCheckRequest_ReferenceToken_UsesReadinessProbe()
    {
        var config = new OpenClaw.Core.Models.GatewayConfig
        {
            AuthTokenRef = "vault:secret/data/test#auth_token"
        };

        using var request = GatewayBootstrapExtensions.CreateHealthCheckRequest(config, isNonLoopbackBind: true);

        Assert.Equal("/health/ready", request.RequestUri!.AbsolutePath);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void CreateHealthCheckRequest_DirectToken_UsesAuthenticatedHealthEndpoint()
    {
        var config = new OpenClaw.Core.Models.GatewayConfig { AuthToken = "direct-token" };

        using var request = GatewayBootstrapExtensions.CreateHealthCheckRequest(config, isNonLoopbackBind: true);

        Assert.Equal("/health", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("direct-token", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData("AllowViewerAgentExecution", "true")]
    [InlineData("AllowViewerAgentExecution", "false")]
    [InlineData("AllowViewerAgentExecution", "")]
    [InlineData("AllowViewerAgentExecution", null)]
    [InlineData("allowvieweragentexecution", "true")]
    [InlineData("AllowViewerAgentExecution", "invalid-setting-value")]
    public void LoadGatewayConfig_RemovedViewerExecutionSetting_ThrowsWithMigrationInstructions(string key, string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"OpenClaw:Security:{key}"] = value
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => GatewayBootstrapExtensions.LoadGatewayConfig(configuration));

        Assert.Contains("OpenClaw:Security:AllowViewerAgentExecution has been removed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Remove this key", error.Message, StringComparison.Ordinal);
        Assert.Contains("grant the operator role", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-setting-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadGatewayConfig_RemovedViewerExecutionEnvironmentVariable_Throws()
    {
        var prefix = $"OPENCLAW_TEST_{Guid.NewGuid():N}_";
        var variable = prefix + "OpenClaw__Security__AllowViewerAgentExecution";
        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            var error = Assert.Throws<InvalidOperationException>(() => GatewayBootstrapExtensions.LoadGatewayConfig(configuration));

            Assert.Contains("OpenClaw:Security:AllowViewerAgentExecution has been removed", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void LoadGatewayConfig_LegacyTelegramConfigWithoutUpdateMode_DefaultsToWebhook()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:Channels:Telegram:Enabled"] = "true",
                ["OpenClaw:Channels:Telegram:BotTokenRef"] = "env:TELEGRAM_BOT_TOKEN",
                ["OpenClaw:Channels:Telegram:WebhookPath"] = "/telegram/custom-inbound",
                ["OpenClaw:Channels:Telegram:WebhookPublicBaseUrl"] = "https://bot.example.com",
                ["OpenClaw:Channels:Telegram:ValidateSignature"] = "true",
                ["OpenClaw:Channels:Telegram:WebhookSecretTokenRef"] = "env:TELEGRAM_WEBHOOK_SECRET"
            })
            .Build();

        var telegram = GatewayBootstrapExtensions.LoadGatewayConfig(configuration).Channels.Telegram;

        Assert.Equal("webhook", telegram.UpdateMode);
        Assert.Equal("/telegram/custom-inbound", telegram.WebhookPath);
        Assert.Equal("https://bot.example.com", telegram.WebhookPublicBaseUrl);
        Assert.True(telegram.ValidateSignature);
        Assert.Equal("env:TELEGRAM_WEBHOOK_SECRET", telegram.WebhookSecretTokenRef);
    }

    [Fact]
    public void LoadGatewayConfig_ConfiguredTelegramPollingSettings_AreBound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:Channels:Telegram:UpdateMode"] = " Long-Polling ",
                ["OpenClaw:Channels:Telegram:PollingTimeoutSeconds"] = "45",
                ["OpenClaw:Channels:Telegram:PollingRetryDelaySeconds"] = "12",
                ["OpenClaw:Channels:Telegram:DropPendingUpdatesOnStart"] = "true"
            })
            .Build();

        var telegram = GatewayBootstrapExtensions.LoadGatewayConfig(configuration).Channels.Telegram;

        Assert.True(telegram.UsesLongPolling());
        Assert.False(telegram.UsesWebhook());
        Assert.Equal(45, telegram.PollingTimeoutSeconds);
        Assert.Equal(12, telegram.PollingRetryDelaySeconds);
        Assert.True(telegram.DropPendingUpdatesOnStart);
    }

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
