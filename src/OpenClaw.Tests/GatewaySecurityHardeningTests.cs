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
