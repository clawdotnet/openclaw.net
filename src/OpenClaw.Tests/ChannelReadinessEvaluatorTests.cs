using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class ChannelReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_SmsEnabledWithoutSecrets_ReturnsMisconfigured()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Sms = new SmsChannelConfig
                {
                    Twilio = new TwilioSmsConfig
                    {
                        Enabled = true,
                        ValidateSignature = true
                    }
                }
            }
        };

        var sms = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
            .Single(item => item.ChannelId == "sms");

        Assert.Equal("misconfigured", sms.Status);
        Assert.Contains(sms.MissingRequirements, value => value.Contains("AuthTokenRef", StringComparison.Ordinal));
        Assert.Contains(sms.MissingRequirements, value => value.Contains("WebhookPublicBaseUrl", StringComparison.Ordinal));
        Assert.Contains(sms.FixGuidance, value => value.Href == "#setup-ref-sms-auth-token");
    }

    [Fact]
    public void Evaluate_TelegramEnabledWithoutSignatureValidation_ReturnsDegraded()
    {
        var previous = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "test-token");
            var config = new GatewayConfig
            {
                Channels = new ChannelsConfig
                {
                    Telegram = new TelegramChannelConfig
                    {
                        Enabled = true,
                        ValidateSignature = false
                    }
                }
            };

            var telegram = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
                .Single(item => item.ChannelId == "telegram");

            Assert.Equal("degraded", telegram.Status);
            Assert.Contains(telegram.Warnings, value => value.Contains("public bind", StringComparison.Ordinal));
            Assert.Contains(telegram.FixGuidance, value => value.Href == "#telegram-validate-signature-input");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", previous);
        }
    }

    [Fact]
    public void Evaluate_WhatsAppBridgePublicWithoutToken_ReturnsMisconfigured()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = true,
                    Type = "bridge",
                    BridgeUrl = "https://bridge.example.com/inbound",
                    BridgeTokenRef = ""
                }
            }
        };

        var whatsapp = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
            .Single(item => item.ChannelId == "whatsapp");

        Assert.Equal("misconfigured", whatsapp.Status);
        Assert.Contains(whatsapp.MissingRequirements, value => value.Contains("BridgeToken", StringComparison.Ordinal));
        Assert.Contains(whatsapp.FixGuidance, value => value.Href == "#setup-ref-whatsapp-bridge-token");
    }

    [Fact]
    public void Evaluate_SlackEnabledWithoutSigningSecret_ReturnsMisconfigured()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Slack = new SlackChannelConfig
                {
                    Enabled = true,
                    BotTokenRef = "env:SLACK_BOT_TOKEN",
                    SigningSecretRef = ""
                }
            }
        };

        var previous = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-test");
            var slack = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
                .Single(item => item.ChannelId == "slack");

            Assert.Equal("misconfigured", slack.Status);
            Assert.Contains(slack.MissingRequirements, value => value.Contains("SigningSecret", StringComparison.Ordinal));
            Assert.Contains(slack.FixGuidance, value => value.Href == "#setup-ref-slack-signing-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", previous);
        }
    }

    [Fact]
    public void Evaluate_TeamsWithoutTokenValidation_ReturnsDegraded()
    {
        var previousAppId = Environment.GetEnvironmentVariable("TEAMS_APP_ID");
        var previousPassword = Environment.GetEnvironmentVariable("TEAMS_APP_PASSWORD");
        var previousTenant = Environment.GetEnvironmentVariable("TEAMS_TENANT_ID");
        try
        {
            Environment.SetEnvironmentVariable("TEAMS_APP_ID", "app-id");
            Environment.SetEnvironmentVariable("TEAMS_APP_PASSWORD", "password");
            Environment.SetEnvironmentVariable("TEAMS_TENANT_ID", "tenant");

            var config = new GatewayConfig
            {
                Channels = new ChannelsConfig
                {
                    Teams = new TeamsChannelConfig
                    {
                        Enabled = true,
                        ValidateToken = false
                    }
                }
            };

            var teams = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
                .Single(item => item.ChannelId == "teams");

            Assert.Equal("degraded", teams.Status);
            Assert.Contains(teams.Warnings, value => value.Contains("public bind", StringComparison.Ordinal));
            Assert.Contains(teams.FixGuidance, value => value.Href == "#teams-validate-token-input");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEAMS_APP_ID", previousAppId);
            Environment.SetEnvironmentVariable("TEAMS_APP_PASSWORD", previousPassword);
            Environment.SetEnvironmentVariable("TEAMS_TENANT_ID", previousTenant);
        }
    }

    [Fact]
    public void Evaluate_DiscordMissingApplicationId_ReturnsMisconfigured()
    {
        var previousBot = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        var previousKey = Environment.GetEnvironmentVariable("DISCORD_PUBLIC_KEY");
        try
        {
            Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", "bot-token");
            Environment.SetEnvironmentVariable("DISCORD_PUBLIC_KEY", "public-key");

            var config = new GatewayConfig
            {
                Channels = new ChannelsConfig
                {
                    Discord = new DiscordChannelConfig
                    {
                        Enabled = true,
                        ApplicationIdRef = ""
                    }
                }
            };

            var discord = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: true)
                .Single(item => item.ChannelId == "discord");

            Assert.Equal("misconfigured", discord.Status);
            Assert.Contains(discord.MissingRequirements, value => value.Contains("ApplicationId", StringComparison.Ordinal));
            Assert.Contains(discord.FixGuidance, value => value.Href == "#setup-ref-discord-application-id");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_BOT_TOKEN", previousBot);
            Environment.SetEnvironmentVariable("DISCORD_PUBLIC_KEY", previousKey);
        }
    }

    [Fact]
    public void Evaluate_SignalCliWithoutPath_ReturnsMisconfigured()
    {
        var previous = Environment.GetEnvironmentVariable("SIGNAL_PHONE_NUMBER");
        try
        {
            Environment.SetEnvironmentVariable("SIGNAL_PHONE_NUMBER", "+15551230000");

            var config = new GatewayConfig
            {
                Channels = new ChannelsConfig
                {
                    Signal = new SignalChannelConfig
                    {
                        Enabled = true,
                        Driver = "signal_cli",
                        SignalCliPath = null
                    }
                }
            };

            var signal = ChannelReadinessEvaluator.Evaluate(config, isNonLoopbackBind: false)
                .Single(item => item.ChannelId == "signal");

            Assert.Equal("misconfigured", signal.Status);
            Assert.Contains(signal.MissingRequirements, value => value.Contains("SignalCliPath", StringComparison.Ordinal));
            Assert.Contains(signal.FixGuidance, value => value.Href == "#setup-ref-signal-cli-path");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIGNAL_PHONE_NUMBER", previous);
        }
    }
}
