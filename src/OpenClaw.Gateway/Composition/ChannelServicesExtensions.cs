using OpenClaw.Channels;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Channels;

namespace OpenClaw.Gateway.Composition;

internal static class ChannelServicesExtensions
{
    public static IServiceCollection AddOpenClawChannelServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var config = startup.Config;

        // Feishu: always registered (supports runtime enable via admin API without restart).
        services.AddSingleton(config.Channels.Feishu);
        services.AddSingleton<FeishuChannel>();

        // DingTalk: always registered so Stream mode can be started and hot-reloaded via admin API.
        services.AddSingleton(config.Channels.DingTalk);
        services.AddSingleton<DingTalkChannel>();

        // WeCom: always registered so WebSocket long connection can be started and hot-reloaded via admin API.
        services.AddSingleton(config.Channels.WeCom);
        services.AddSingleton<WeComChannel>();

        // ChannelConfigStore: persists channel configs to {StoragePath}/channels/channel-{id}.json.
        services.AddSingleton(sp =>
            new ChannelConfigStore(
                config.Memory.StoragePath,
                sp.GetRequiredService<ILogger<ChannelConfigStore>>()));

        if (config.Channels.WhatsApp.Enabled)
        {
            services.AddSingleton(config.Channels.WhatsApp);
            if (!string.Equals(config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
                services.AddSingleton<WhatsAppWebhookHandler>();

            if (config.Channels.WhatsApp.Type == "bridge")
            {
                services.AddSingleton<WhatsAppBridgeChannel>(sp =>
                    new WhatsAppBridgeChannel(
                        config.Channels.WhatsApp,
                        OpenClaw.Core.Http.HttpClientFactory.Create(),
                        sp.GetRequiredService<ILogger<WhatsAppBridgeChannel>>()));
            }
            else
            {
                services.AddSingleton<WhatsAppChannel>(sp =>
                    new WhatsAppChannel(
                        config.Channels.WhatsApp,
                        OpenClaw.Core.Http.HttpClientFactory.Create(),
                        sp.GetRequiredService<ILogger<WhatsAppChannel>>()));
            }
        }

        if (config.Channels.Telegram.Enabled)
        {
            services.AddSingleton(config.Channels.Telegram);
            services.AddSingleton<TelegramChannel>();
            services.AddSingleton<TelegramWebhookHandler>(sp =>
                new TelegramWebhookHandler(
                    config.Channels.Telegram,
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistManager>(),
                    sp.GetRequiredService<OpenClaw.Core.Pipeline.RecentSendersStore>(),
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistSemantics>(),
                    sp.GetRequiredService<ILogger<TelegramWebhookHandler>>()));
        }

        if (config.Channels.Teams.Enabled)
        {
            services.AddSingleton(config.Channels.Teams);
            services.AddSingleton<ITeamsTokenValidator>(_ =>
                new BotFrameworkTokenValidator(
                    OpenClaw.Core.Security.SecretResolver.Resolve(config.Channels.Teams.AppIdRef) ?? config.Channels.Teams.AppId ?? ""));
            services.AddSingleton<TeamsWebhookHandler>();
            services.AddSingleton<TeamsChannel>(sp =>
                new TeamsChannel(
                    config.Channels.Teams,
                    OpenClaw.Core.Http.HttpClientFactory.Create(),
                    sp.GetRequiredService<ILogger<TeamsChannel>>()));
        }

        if (config.Channels.Slack.Enabled)
        {
            services.AddSingleton(config.Channels.Slack);
            services.AddSingleton<SlackWebhookHandler>(sp =>
                new SlackWebhookHandler(
                    config.Channels.Slack,
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistManager>(),
                    sp.GetRequiredService<OpenClaw.Core.Pipeline.RecentSendersStore>(),
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistSemantics>(),
                    sp.GetRequiredService<ILogger<SlackWebhookHandler>>()));
            services.AddSingleton<SlackChannel>();
        }

        if (config.Channels.Discord.Enabled)
        {
            services.AddSingleton(config.Channels.Discord);
            services.AddSingleton<DiscordWebhookHandler>(sp =>
                new DiscordWebhookHandler(
                    config.Channels.Discord,
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistManager>(),
                    sp.GetRequiredService<OpenClaw.Core.Pipeline.RecentSendersStore>(),
                    sp.GetRequiredService<OpenClaw.Core.Security.AllowlistSemantics>(),
                    sp.GetRequiredService<ILogger<DiscordWebhookHandler>>()));
            services.AddSingleton<DiscordChannel>();
        }

        if (config.Channels.Signal.Enabled)
        {
            services.AddSingleton(config.Channels.Signal);
            services.AddSingleton<SignalChannel>();
        }

        return services;
    }
}
