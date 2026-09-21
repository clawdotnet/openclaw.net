using OpenClaw.Core.Validation;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Routing.Decisions;
using OpenClaw.Routing.Onnx;

namespace OpenClaw.Gateway.Routing;

internal static class TurnRoutingServices
{
    public static IServiceCollection AddDynamicTurnRouting(this IServiceCollection services,
        DynamicTurnRoutingConfig config, string storagePath)
    {
        var jevEnabled = DecisionRoutingConfiguration.NormalizeMode(config.Jev) != "disabled";
        var layaEnabled = DecisionRoutingConfiguration.NormalizeMode(config.Laya) != "disabled";
        if (jevEnabled && layaEnabled)
            throw new ArgumentException("Enable only one decision provider: Jev or Laya.");
        DecisionRoutingConfig? decision = layaEnabled ? config.Laya : jevEnabled ? config.Jev : null;
        if (decision is not null)
        {
            var endpoint = DecisionRoutingConfiguration.Validate(decision);
            services.AddHttpClient("routing-decisions", client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = !layaEnabled,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });
            services.AddSingleton<IDecisionClient>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("routing-decisions");
                return layaEnabled ? new LayaDecisionClient(http, config.Laya) : new TypeSafeDecisionClient(http, endpoint,
                    ct => SecretResolver.ResolveAsync(config.Jev.ApiKeyRef, ct));
            });
            services.AddSingleton<IDecisionRoutingObserver>(sp => new JsonlDecisionRoutingObserver(
                string.IsNullOrWhiteSpace(decision.DiagnosticsPath) ? null :
                    Path.GetFullPath(decision.DiagnosticsPath, Path.GetFullPath(storagePath)),
                sp.GetRequiredService<ILogger<JsonlDecisionRoutingObserver>>()));
        }

        services.AddSingleton(_ => DynamicTurnRoutingConfigNormalizer.Normalize(config, new OpenSquillaBundleLoader()));
        services.AddSingleton<ITurnRoutingPolicy>(sp =>
        {
            var resolved = sp.GetRequiredService<ResolvedDynamicTurnRoutingConfig>();
            ITurnRoutingPolicy baseline = resolved.Enabled
                ? new OnnxTurnRoutingPolicy(resolved, sp.GetRequiredService<ILogger<OnnxTurnRoutingPolicy>>())
                : NoopTurnRoutingPolicy.Instance;
            return decision is null ? baseline : new DecisionTurnRoutingPolicy(decision, resolved.Policy,
                baseline, sp.GetRequiredService<IDecisionClient>(),
                sp.GetRequiredService<IRedactionPipeline>(), sp.GetRequiredService<IDecisionRoutingObserver>());
        });
        return services;
    }
}
