using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewaySecurityHardeningTests
{
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
