using OpenClaw.Channels;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal static class ChannelServicesExtensions
{
    public static IServiceCollection AddOpenClawChannelServices(this IServiceCollection services, GatewayStartupContext startup)
    {
        var config = startup.Config;

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

        return services;
    }
}
