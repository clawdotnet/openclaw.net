using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenClaw.Core.Security;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Composition;

public static class RuntimeEventWebhookExtensions
{
    public const string SectionName = "OpenClaw:RuntimeEvents:Webhook";

    /// <summary>
    /// Registers <see cref="RuntimeEventWebhook"/> when
    /// <c>OpenClaw:RuntimeEvents:Webhook:Url</c> is set. The registration is
    /// skipped entirely (no HttpClient allocated, no logger) when the URL is
    /// empty — same "off by default" rule as the sidecar side.
    /// </summary>
    public static IServiceCollection AddRuntimeEventWebhook(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var url = section.GetValue<string>("Url");

        if (string.IsNullOrWhiteSpace(url))
        {
            return services;
        }

        services.AddHttpClient("RuntimeEventWebhook", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var http = httpFactory.CreateClient("RuntimeEventWebhook");
            var options = section.Get<RuntimeEventWebhookOptions>() ?? new RuntimeEventWebhookOptions();
            options.Url = url;
            options.BearerToken = SecretResolver.Resolve(options.BearerToken ?? section.GetValue<string>("TokenSecret"));
            var logger = sp.GetService<ILogger<RuntimeEventWebhook>>()
                ?? NullLogger<RuntimeEventWebhook>.Instance;
            return new RuntimeEventWebhook(http, options, logger);
        });

        return services;
    }
}
