using OpenClaw.Core.Validation;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Routing.Jev;
using OpenClaw.Routing.Onnx;

namespace OpenClaw.Gateway.Routing;

internal static class TurnRoutingServices
{
    public static IServiceCollection AddDynamicTurnRouting(this IServiceCollection services,
        DynamicTurnRoutingConfig config, string storagePath)
    {
        var mode = JevRoutingConfiguration.NormalizeMode(config.Jev);
        if (mode != "disabled")
        {
            var endpoint = JevRoutingConfiguration.Validate(config.Jev);
            services.AddHttpClient("jev-decisions", client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });
            services.AddSingleton<ITypeSafeDecisionClient>(sp => new TypeSafeDecisionClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("jev-decisions"), endpoint,
                ct => SecretResolver.ResolveAsync(config.Jev.ApiKeyRef, ct)));
            services.AddSingleton<IJevRoutingObserver>(sp => new JsonlJevRoutingObserver(
                string.IsNullOrWhiteSpace(config.Jev.DiagnosticsPath) ? null :
                    Path.GetFullPath(config.Jev.DiagnosticsPath, Path.GetFullPath(storagePath)),
                sp.GetRequiredService<ILogger<JsonlJevRoutingObserver>>()));
        }

        services.AddSingleton(_ => DynamicTurnRoutingConfigNormalizer.Normalize(config, new OpenSquillaBundleLoader()));
        services.AddSingleton<ITurnRoutingPolicy>(sp =>
        {
            var resolved = sp.GetRequiredService<ResolvedDynamicTurnRoutingConfig>();
            ITurnRoutingPolicy baseline = resolved.Enabled
                ? new OnnxTurnRoutingPolicy(resolved, sp.GetRequiredService<ILogger<OnnxTurnRoutingPolicy>>())
                : NoopTurnRoutingPolicy.Instance;
            return mode == "disabled" ? baseline : new JevTurnRoutingPolicy(config.Jev, resolved.Policy,
                baseline, sp.GetRequiredService<ITypeSafeDecisionClient>(),
                sp.GetRequiredService<IRedactionPipeline>(), sp.GetRequiredService<IJevRoutingObserver>());
        });
        return services;
    }
}
