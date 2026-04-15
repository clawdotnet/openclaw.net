using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal static class ChannelReadinessEvaluator
{
    public static IReadOnlyList<ChannelReadinessState> Evaluate(GatewayConfig config, bool isNonLoopbackBind)
    {
        return
        [
            EvaluateSms(config, isNonLoopbackBind),
            EvaluateTelegram(config, isNonLoopbackBind),
            EvaluateWhatsApp(config, isNonLoopbackBind),
            EvaluateTeams(config, isNonLoopbackBind),
            EvaluateSlack(config, isNonLoopbackBind),
            EvaluateDiscord(config, isNonLoopbackBind),
            EvaluateSignal(config)
        ];
    }

    private static ChannelReadinessState EvaluateSms(GatewayConfig config, bool isNonLoopbackBind)
    {
        var sms = config.Channels.Sms.Twilio;
        if (!sms.Enabled)
            return ChannelReadinessState.Disabled("sms", "Twilio SMS", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable SMS channel",
                    Href = "#sms-enabled-input",
                    Reference = "OpenClaw:Channels:Sms:Twilio:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(sms.AuthTokenRef)))
        {
            missing.Add("Twilio AuthTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio auth token ref",
                Href = "#setup-ref-sms-auth-token",
                Reference = "OpenClaw:Channels:Sms:Twilio:AuthTokenRef = env:TWILIO_AUTH_TOKEN"
            });
        }
        if (string.IsNullOrWhiteSpace(sms.AccountSid))
        {
            missing.Add("Twilio AccountSid");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio account SID",
                Href = "#setup-ref-sms-account-sid",
                Reference = "OpenClaw:Channels:Sms:Twilio:AccountSid"
            });
        }
        if (string.IsNullOrWhiteSpace(sms.MessagingServiceSid) && string.IsNullOrWhiteSpace(sms.FromNumber))
        {
            missing.Add("Twilio MessagingServiceSid or FromNumber");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio sender",
                Href = "#setup-ref-sms-sender",
                Reference = "OpenClaw:Channels:Sms:Twilio:MessagingServiceSid or OpenClaw:Channels:Sms:Twilio:FromNumber"
            });
        }
        if (sms.ValidateSignature && string.IsNullOrWhiteSpace(sms.WebhookPublicBaseUrl))
        {
            missing.Add("Twilio WebhookPublicBaseUrl (required when signature validation is enabled)");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set SMS webhook public base URL",
                Href = "#setup-ref-sms-webhook-public-base-url",
                Reference = "OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl"
            });
        }
        if (!sms.ValidateSignature)
        {
            warnings.Add(isNonLoopbackBind
                ? "SMS webhook signature validation is disabled on a public bind."
                : "SMS webhook signature validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable SMS signature validation",
                Href = "#sms-validate-signature-input",
                Reference = "OpenClaw:Channels:Sms:Twilio:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("sms", "Twilio SMS", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateTelegram(GatewayConfig config, bool isNonLoopbackBind)
    {
        var telegram = config.Channels.Telegram;
        if (!telegram.Enabled)
            return ChannelReadinessState.Disabled("telegram", "Telegram", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable Telegram channel",
                    Href = "#telegram-enabled-input",
                    Reference = "OpenClaw:Channels:Telegram:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveTelegramToken(telegram)))
        {
            missing.Add("Telegram BotToken or BotTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Telegram bot token",
                Href = "#setup-ref-telegram-bot-token",
                Reference = "OpenClaw:Channels:Telegram:BotTokenRef = env:TELEGRAM_BOT_TOKEN"
            });
        }
        if (telegram.ValidateSignature && string.IsNullOrWhiteSpace(ResolveTelegramSecret(telegram)))
        {
            missing.Add("Telegram WebhookSecretToken or WebhookSecretTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Telegram webhook secret",
                Href = "#setup-ref-telegram-webhook-secret",
                Reference = "OpenClaw:Channels:Telegram:WebhookSecretTokenRef = env:TELEGRAM_WEBHOOK_SECRET"
            });
        }
        if (!telegram.ValidateSignature)
        {
            warnings.Add(isNonLoopbackBind
                ? "Telegram webhook secret validation is disabled on a public bind."
                : "Telegram webhook secret validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable Telegram webhook secret validation",
                Href = "#telegram-validate-signature-input",
                Reference = "OpenClaw:Channels:Telegram:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("telegram", "Telegram", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateWhatsApp(GatewayConfig config, bool isNonLoopbackBind)
    {
        var whatsapp = config.Channels.WhatsApp;
        if (!whatsapp.Enabled)
            return ChannelReadinessState.Disabled("whatsapp", "WhatsApp", whatsapp.Type, [
                new ChannelFixGuidance
                {
                    Label = "Enable WhatsApp channel",
                    Href = "#whatsapp-enabled-input",
                    Reference = "OpenClaw:Channels:WhatsApp:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.Equals(whatsapp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(whatsapp.BridgeUrl))
            {
                missing.Add("WhatsApp BridgeUrl");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge URL",
                    Href = "#setup-ref-whatsapp-bridge-url",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeUrl"
                });
            }

            var bridgeToken = ResolveSecretRefOrNull(whatsapp.BridgeTokenRef) ?? whatsapp.BridgeToken;
            if (isNonLoopbackBind && string.IsNullOrWhiteSpace(bridgeToken))
            {
                missing.Add("WhatsApp BridgeToken or BridgeTokenRef for public bind");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge token",
                    Href = "#setup-ref-whatsapp-bridge-token",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeTokenRef = env:WHATSAPP_BRIDGE_TOKEN"
                });
            }
            else if (string.IsNullOrWhiteSpace(bridgeToken))
            {
                warnings.Add("WhatsApp bridge inbound authentication token is not configured.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge token",
                    Href = "#setup-ref-whatsapp-bridge-token",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeTokenRef = env:WHATSAPP_BRIDGE_TOKEN"
                });
            }
        }
        else if (string.Equals(whatsapp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            if (whatsapp.FirstPartyWorker.Accounts.Count == 0)
            {
                missing.Add("WhatsApp first-party worker account configuration");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Add a first-party worker account",
                    Href = "#wa-first-party-worker-config-json-input",
                    Reference = "OpenClaw:Channels:WhatsApp:FirstPartyWorker:Accounts"
                });
            }

            if (string.IsNullOrWhiteSpace(whatsapp.FirstPartyWorker.ExecutablePath))
            {
                warnings.Add("WhatsApp first-party worker executable path is not configured; runtime will rely on colocated auto-discovery.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set first-party worker executable path",
                    Href = "#wa-first-party-worker-config-json-input",
                    Reference = "OpenClaw:Channels:WhatsApp:FirstPartyWorker:ExecutablePath"
                });
            }
        }
        else
        {
            var cloudToken = ResolveSecretRefOrNull(whatsapp.CloudApiTokenRef) ?? whatsapp.CloudApiToken;
            if (string.IsNullOrWhiteSpace(cloudToken))
            {
                missing.Add("WhatsApp CloudApiToken or CloudApiTokenRef");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp Cloud API token",
                    Href = "#setup-ref-whatsapp-cloud-token",
                    Reference = "OpenClaw:Channels:WhatsApp:CloudApiTokenRef = env:WHATSAPP_CLOUD_API_TOKEN"
                });
            }
            if (string.IsNullOrWhiteSpace(whatsapp.PhoneNumberId))
            {
                missing.Add("WhatsApp PhoneNumberId");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp phone number ID",
                    Href = "#setup-ref-whatsapp-phone-number-id",
                    Reference = "OpenClaw:Channels:WhatsApp:PhoneNumberId"
                });
            }
            if (whatsapp.ValidateSignature)
            {
                var appSecret = ResolveSecretRefOrNull(whatsapp.WebhookAppSecretRef) ?? whatsapp.WebhookAppSecret;
                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    missing.Add("WhatsApp WebhookAppSecret or WebhookAppSecretRef");
                    guidance.Add(new ChannelFixGuidance
                    {
                        Label = "Set WhatsApp webhook app secret",
                        Href = "#setup-ref-whatsapp-app-secret",
                        Reference = "OpenClaw:Channels:WhatsApp:WebhookAppSecretRef = env:WHATSAPP_APP_SECRET"
                    });
                }
            }
            else
            {
                warnings.Add(isNonLoopbackBind
                    ? "WhatsApp official signature validation is disabled on a public bind."
                    : "WhatsApp official signature validation is disabled.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Enable WhatsApp signature validation",
                    Href = "#whatsapp-validate-signature-input",
                    Reference = "OpenClaw:Channels:WhatsApp:ValidateSignature"
                });
            }
        }

        return ChannelReadinessState.From("whatsapp", "WhatsApp", whatsapp.Type, missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateTeams(GatewayConfig config, bool isNonLoopbackBind)
    {
        var teams = config.Channels.Teams;
        if (!teams.Enabled)
            return ChannelReadinessState.Disabled("teams", "Teams", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable Teams channel",
                    Href = "#teams-enabled-input",
                    Reference = "OpenClaw:Channels:Teams:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(teams.AppIdRef) ?? teams.AppId))
        {
            missing.Add("Teams AppId or AppIdRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Teams app ID",
                Href = "#setup-ref-teams-app-id",
                Reference = "OpenClaw:Channels:Teams:AppIdRef = env:TEAMS_APP_ID"
            });
        }

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(teams.AppPasswordRef) ?? teams.AppPassword))
        {
            missing.Add("Teams AppPassword or AppPasswordRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Teams app password",
                Href = "#setup-ref-teams-app-password",
                Reference = "OpenClaw:Channels:Teams:AppPasswordRef = env:TEAMS_APP_PASSWORD"
            });
        }

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(teams.TenantIdRef) ?? teams.TenantId))
        {
            missing.Add("Teams TenantId or TenantIdRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Teams tenant ID",
                Href = "#setup-ref-teams-tenant-id",
                Reference = "OpenClaw:Channels:Teams:TenantIdRef = env:TEAMS_TENANT_ID"
            });
        }

        if (!teams.ValidateToken)
        {
            warnings.Add(isNonLoopbackBind
                ? "Teams JWT validation is disabled on a public bind."
                : "Teams JWT validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable Teams JWT validation",
                Href = "#teams-validate-token-input",
                Reference = "OpenClaw:Channels:Teams:ValidateToken"
            });
        }

        return ChannelReadinessState.From("teams", "Teams", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateSlack(GatewayConfig config, bool isNonLoopbackBind)
    {
        var slack = config.Channels.Slack;
        if (!slack.Enabled)
            return ChannelReadinessState.Disabled("slack", "Slack", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable Slack channel",
                    Href = "#slack-enabled-input",
                    Reference = "OpenClaw:Channels:Slack:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(slack.BotTokenRef) ?? slack.BotToken))
        {
            missing.Add("Slack BotToken or BotTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Slack bot token",
                Href = "#setup-ref-slack-bot-token",
                Reference = "OpenClaw:Channels:Slack:BotTokenRef = env:SLACK_BOT_TOKEN"
            });
        }

        if (slack.ValidateSignature)
        {
            if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(slack.SigningSecretRef) ?? slack.SigningSecret))
            {
                missing.Add("Slack SigningSecret or SigningSecretRef");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set Slack signing secret",
                    Href = "#setup-ref-slack-signing-secret",
                    Reference = "OpenClaw:Channels:Slack:SigningSecretRef = env:SLACK_SIGNING_SECRET"
                });
            }
        }
        else
        {
            warnings.Add(isNonLoopbackBind
                ? "Slack signature validation is disabled on a public bind."
                : "Slack signature validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable Slack signature validation",
                Href = "#slack-validate-signature-input",
                Reference = "OpenClaw:Channels:Slack:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("slack", "Slack", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateDiscord(GatewayConfig config, bool isNonLoopbackBind)
    {
        var discord = config.Channels.Discord;
        if (!discord.Enabled)
            return ChannelReadinessState.Disabled("discord", "Discord", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable Discord channel",
                    Href = "#discord-enabled-input",
                    Reference = "OpenClaw:Channels:Discord:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(discord.BotTokenRef) ?? discord.BotToken))
        {
            missing.Add("Discord BotToken or BotTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Discord bot token",
                Href = "#setup-ref-discord-bot-token",
                Reference = "OpenClaw:Channels:Discord:BotTokenRef = env:DISCORD_BOT_TOKEN"
            });
        }

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(discord.ApplicationIdRef) ?? discord.ApplicationId))
        {
            missing.Add("Discord ApplicationId or ApplicationIdRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Discord application ID",
                Href = "#setup-ref-discord-application-id",
                Reference = "OpenClaw:Channels:Discord:ApplicationIdRef = env:DISCORD_APPLICATION_ID"
            });
        }

        if (discord.ValidateSignature)
        {
            if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(discord.PublicKeyRef) ?? discord.PublicKey))
            {
                missing.Add("Discord PublicKey or PublicKeyRef");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set Discord public key",
                    Href = "#setup-ref-discord-public-key",
                    Reference = "OpenClaw:Channels:Discord:PublicKeyRef = env:DISCORD_PUBLIC_KEY"
                });
            }
        }
        else
        {
            warnings.Add(isNonLoopbackBind
                ? "Discord interaction signature validation is disabled on a public bind."
                : "Discord interaction signature validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable Discord signature validation",
                Href = "#discord-validate-signature-input",
                Reference = "OpenClaw:Channels:Discord:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("discord", "Discord", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateSignal(GatewayConfig config)
    {
        var signal = config.Channels.Signal;
        if (!signal.Enabled)
            return ChannelReadinessState.Disabled("signal", "Signal", signal.Driver, [
                new ChannelFixGuidance
                {
                    Label = "Enable Signal channel",
                    Href = "#signal-enabled-input",
                    Reference = "OpenClaw:Channels:Signal:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(signal.AccountPhoneNumberRef) ?? signal.AccountPhoneNumber))
        {
            missing.Add("Signal AccountPhoneNumber or AccountPhoneNumberRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Signal account phone number",
                Href = "#setup-ref-signal-account-phone",
                Reference = "OpenClaw:Channels:Signal:AccountPhoneNumberRef = env:SIGNAL_PHONE_NUMBER"
            });
        }

        if (string.Equals(signal.Driver, "signal_cli", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(signal.SignalCliPath))
        {
            missing.Add("Signal SignalCliPath");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set signal-cli executable path",
                Href = "#setup-ref-signal-cli-path",
                Reference = "OpenClaw:Channels:Signal:SignalCliPath"
            });
        }

        if (signal.TrustAllKeys)
        {
            warnings.Add("Signal TrustAllKeys is enabled. Consider pinning keys for stricter transport trust.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Review Signal trust policy",
                Href = "#signal-trust-all-keys-input",
                Reference = "OpenClaw:Channels:Signal:TrustAllKeys"
            });
        }

        return ChannelReadinessState.From("signal", "Signal", signal.Driver, missing, warnings, guidance);
    }

    private static string? ResolveSecretRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return SecretResolver.Resolve(value);
        }

        return value;
    }

    private static string? ResolveTelegramToken(TelegramChannelConfig telegram)
    {
        if (!string.IsNullOrWhiteSpace(telegram.BotToken))
            return telegram.BotToken;

        if (telegram.BotTokenRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(telegram.BotTokenRef[4..]);

        return null;
    }

    private static string? ResolveTelegramSecret(TelegramChannelConfig telegram)
    {
        if (!string.IsNullOrWhiteSpace(telegram.WebhookSecretToken))
            return telegram.WebhookSecretToken;

        return ResolveSecretRefOrNull(telegram.WebhookSecretTokenRef);
    }
}

internal sealed class ChannelReadinessState
{
    public required string ChannelId { get; init; }
    public required string DisplayName { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public bool Enabled { get; init; }
    public bool Ready => string.Equals(Status, "ready", StringComparison.Ordinal);
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ChannelFixGuidance> FixGuidance { get; init; } = [];

    public static ChannelReadinessState Disabled(
        string channelId,
        string displayName,
        string mode = "official",
        IReadOnlyList<ChannelFixGuidance>? fixGuidance = null)
        => new()
        {
            ChannelId = channelId,
            DisplayName = displayName,
            Mode = mode,
            Status = "disabled",
            Enabled = false,
            MissingRequirements = [],
            Warnings = [],
            FixGuidance = fixGuidance ?? []
        };

    public static ChannelReadinessState From(
        string channelId,
        string displayName,
        string mode,
        IReadOnlyList<string> missingRequirements,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ChannelFixGuidance> fixGuidance)
        => new()
        {
            ChannelId = channelId,
            DisplayName = displayName,
            Mode = mode,
            Status = missingRequirements.Count > 0 ? "misconfigured" : warnings.Count > 0 ? "degraded" : "ready",
            Enabled = true,
            MissingRequirements = missingRequirements,
            Warnings = warnings,
            FixGuidance = fixGuidance
        };
}

internal sealed class ChannelFixGuidance
{
    public required string Label { get; init; }
    public required string Href { get; init; }
    public required string Reference { get; init; }
}
