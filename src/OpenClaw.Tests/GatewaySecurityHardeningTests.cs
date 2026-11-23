using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

[Collection(ResolverAccessorCollection.Name)]
public sealed class GatewaySecurityHardeningTests
{
    [Fact]
    public void EnforcePublicBindHardening_InlineCredential_ThrowsWithoutEchoingValue()
    {
        const string token = "inline-telegram-token-marker";
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Telegram.BotToken = token;

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("OpenClaw:Channels:Telegram:BotToken", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_SecretReferences_AreAllowed()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Llm.ApiKey = "env:MISSING_MODEL_KEY";
        config.Channels.Telegram.BotTokenRef = "vault:secret/data/test#telegram_bot_token";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_ResolvedAuthToken_IsNotReclassifiedAsInlineCredential()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.AuthToken = "resolved-runtime-token";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_WorkflowTokenEndpoint_IsNotClassifiedAsCredential()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Workflows.Enabled = true;
        config.Workflows.Backends["delegated"] = new WorkflowBackendConfig
        {
            BaseUrl = "https://workflow.example.test",
            Authentication = new WorkflowAuthenticationConfig
            {
                Mode = "token-exchange",
                TokenEndpoint = "https://identity.example.test/token",
                ClientId = "client",
                ClientSecretRef = "env:WORKFLOW_CLIENT_SECRET",
                Scopes = ["workflow.read"]
            }
        };

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_NonSecretIdentifier_IsAllowed()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Teams.AppId = "teams-app-id";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_WhatsAppProxyUserInfo_ThrowsWithoutEchoingValue()
    {
        const string password = "proxy-password-marker";
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.FirstPartyWorker.Proxy = $"http://user:{password}@proxy.example.test:8080";

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Channels:WhatsApp:FirstPartyWorker:Proxy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_WhatsAppAccountProxyUserInfo_ThrowsWithoutEchoingValue()
    {
        const string password = "account-proxy-password-marker";
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.FirstPartyWorker.Accounts.Add(new WhatsAppWorkerAccountConfig
        {
            AccountId = "account-a",
            Proxy = $"http://user:{password}@proxy.example.test:8080"
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Channels:WhatsApp:FirstPartyWorker:Accounts[0]:Proxy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_WhatsAppProxyWithoutUserInfo_IsAllowed()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.FirstPartyWorker.Proxy = "http://proxy.example.test:8080";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_StripeLinkEnvironment_UsesSecretNameClassifier()
    {
        const string apiKey = "stripe-key-marker";
        var config = CreatePublicBindSafeBaseConfig();
        config.Payments.StripeLink.EnvironmentVariables["STRIPE_API_KEY"] = apiKey;
        config.Payments.StripeLink.EnvironmentVariables["STRIPE_MODE"] = "test";

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("STRIPE_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);

        config.Payments.StripeLink.EnvironmentVariables["STRIPE_API_KEY"] = "vault:secret/data/test#stripe_api_key";
        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_VaultReferences_AreAllowedBeforePrewarm()
    {
        ResolverAccessor.Reset();
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.Enabled = true;
        config.Channels.WhatsApp.Type = "official";
        config.Channels.WhatsApp.ValidateSignature = true;
        config.Channels.WhatsApp.WebhookVerifyTokenRef = "vault:secret/data/test#whatsapp_verify_token";
        config.Channels.WhatsApp.WebhookAppSecretRef = "vault:secret/data/test#whatsapp_app_secret";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_RawWhatsAppProxyReference_RequiresOptIn()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.FirstPartyWorker.Proxy = "raw:http://user:password@proxy.example.test:8080";

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("raw: secret ref", error.Message, StringComparison.OrdinalIgnoreCase);
        config.Security.AllowRawSecretRefsOnPublicBind = true;
        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_WhatsAppOfficialWithoutSignatureValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.Enabled = true;
        config.Channels.WhatsApp.Type = "official";
        config.Channels.WhatsApp.ValidateSignature = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("ValidateSignature=true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_WhatsAppBridgeWithoutToken_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.WhatsApp.Enabled = true;
        config.Channels.WhatsApp.Type = "bridge";
        config.Channels.WhatsApp.BridgeToken = null;
        config.Channels.WhatsApp.BridgeTokenRef = "";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("BridgeTokenRef", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_DetectsRawSecretRefsBeyondTwilio()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Telegram.BotTokenRef = "raw:telegram-secret";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("raw: secret ref", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnforcePublicBindHardening_DynamicNativePluginsOnPublicBind_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Plugins.DynamicNative.Enabled = true;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("DynamicNative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_CanvasEnabledOnPublicBind_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Canvas.Enabled = true;
        config.Canvas.AllowOnPublicBind = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Canvas command forwarding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_CanvasPublicBindOptIn_IsAllowed()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Canvas.Enabled = true;
        config.Canvas.AllowOnPublicBind = true;

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_Loopback_DoesNotApplyPublicChecks()
    {
        var config = new GatewayConfig();
        config.Channels.Telegram.BotTokenRef = "raw:telegram-secret";

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: false);
    }

    [Fact]
    public void EnforcePublicBindHardening_RequireSandboxedShell_IsAllowed()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Tooling.AllowShell = true;
        config.Sandbox = new SandboxConfig
        {
            Provider = SandboxProviderNames.OpenSandbox,
            Endpoint = "http://localhost:5000",
            Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
            {
                ["shell"] = new()
                {
                    Mode = nameof(ToolSandboxMode.Require),
                    Template = "ghcr.io/example/shell:latest"
                },
                ["process"] = new()
                {
                    Mode = nameof(ToolSandboxMode.Require),
                    Template = "ghcr.io/example/process:latest"
                }
            }
        };

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_PreferSandboxedShell_StillThrows()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Tooling.AllowShell = true;
        config.Sandbox = new SandboxConfig
        {
            Provider = SandboxProviderNames.OpenSandbox,
            Endpoint = "http://localhost:5000",
            Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
            {
                ["shell"] = new()
                {
                    Mode = nameof(ToolSandboxMode.Prefer),
                    Template = "ghcr.io/example/shell:latest"
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("unsafe tooling settings", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnforcePublicBindHardening_ProcessFallsBackToUnsafeLocalExecution_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Tooling.AllowShell = true;
        config.Sandbox = new SandboxConfig
        {
            Provider = SandboxProviderNames.OpenSandbox,
            Endpoint = "http://localhost:5000",
            Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
            {
                ["shell"] = new()
                {
                    Mode = nameof(ToolSandboxMode.Require),
                    Template = "ghcr.io/example/shell:latest"
                }
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("unsafe tooling settings", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnforcePublicBindHardening_TwilioWithoutSignatureValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Sms.Twilio = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = false,
            AuthTokenRef = "env:TWILIO_AUTH_TOKEN",
            WebhookPublicBaseUrl = "https://example.com"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Twilio SMS webhooks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_TelegramWithoutSignatureValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Telegram.Enabled = true;
        config.Channels.Telegram.ValidateSignature = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Telegram webhooks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_TelegramLongPollingWithoutSignatureValidation_DoesNotThrow()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Telegram.Enabled = true;
        config.Channels.Telegram.UpdateMode = "long-polling";
        config.Channels.Telegram.ValidateSignature = false;

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }

    [Fact]
    public void EnforcePublicBindHardening_TeamsWithoutTokenValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Teams.Enabled = true;
        config.Channels.Teams.ValidateToken = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Teams inbound webhooks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_SlackWithoutSignatureValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Slack.Enabled = true;
        config.Channels.Slack.ValidateSignature = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Slack webhooks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcePublicBindHardening_DiscordWithoutSignatureValidation_Throws()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Channels.Discord.Enabled = true;
        config.Channels.Discord.ValidateSignature = false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true));

        Assert.Contains("Discord interactions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyStrictPublicBindProfile_EnablesSaferPublicDefaults()
    {
        var config = CreatePublicBindSafeBaseConfig();
        config.Security.StrictPublicBindProfile = true;
        config.Tooling.RequireToolApproval = false;
        config.Tooling.ApprovalRequiredTools = [];
        config.Security.RequireRequesterMatchForHttpToolApproval = false;

        GatewaySecurityExtensions.ApplyStrictPublicBindProfile(config, isNonLoopbackBind: true);

        Assert.True(config.Security.RequireRequesterMatchForHttpToolApproval);
        Assert.True(config.Tooling.RequireToolApproval);
        Assert.False(config.Canvas.Enabled);
        Assert.Contains("process", config.Tooling.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("shell", config.Tooling.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);
    }

    private static GatewayConfig CreatePublicBindSafeBaseConfig()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                AllowShell = false,
                AllowedReadRoots = ["/tmp/openclaw-read"],
                AllowedWriteRoots = ["/tmp/openclaw-write"]
            },
            Canvas = new CanvasConfig
            {
                Enabled = false
            },
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    Enabled = false,
                    BotTokenRef = "env:TELEGRAM_BOT_TOKEN"
                },
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = false
                }
            }
        };

        config.Plugins.Enabled = false;
        return config;
    }
}
