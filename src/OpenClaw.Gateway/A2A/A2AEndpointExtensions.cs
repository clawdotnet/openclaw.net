#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

internal static class A2AEndpointExtensions
{
    public static void MapOpenClawA2AEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = NormalizePathPrefix(options.A2APathPrefix);
        var requestHandler = app.Services.GetRequiredService<IA2ARequestHandler>();
        var cardFactory = app.Services.GetRequiredService<OpenClawAgentCardFactory>();
        var fallbackBaseUrl = ResolvePublicBaseUrl(null, startup, options);
        var agentCard = cardFactory.Create(BuildAgentUrl(fallbackBaseUrl, pathPrefix));

        app.MapHttpA2A(requestHandler, agentCard, pathPrefix);
        app.MapGet(GetWellKnownAgentCardPath(pathPrefix), (HttpContext ctx) =>
        {
            var publicBaseUrl = ResolvePublicBaseUrl(ctx, startup, options);
            return Results.Json(
                cardFactory.Create(BuildAgentUrl(publicBaseUrl, pathPrefix)),
                MafJsonContext.Default.AgentCard);
        });
        app.Logger.LogInformation("A2A endpoints enabled at {PathPrefix}.", pathPrefix);
    }

    public static void UseOpenClawA2AAuth(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = NormalizePathPrefix(options.A2APathPrefix);

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!runtime.Operations.ActorRateLimits.TryConsume(
                        "ip",
                        EndpointHelpers.GetRemoteIpKey(ctx),
                        "a2a_http",
                        out _))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }

            await next(ctx);
        });
    }

    internal static string NormalizePathPrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/a2a";

        var trimmed = value.Trim();
        var normalized = trimmed.TrimEnd('/');

        if (string.IsNullOrEmpty(normalized) || normalized == "/")
            return "/a2a";

        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    internal static string ResolvePublicBaseUrl(
        HttpContext? context,
        GatewayStartupContext startup,
        MafOptions options)
    {
        var configuredBaseUrl = NormalizePublicBaseUrl(options.A2APublicBaseUrl);
        if (!string.IsNullOrEmpty(configuredBaseUrl))
            return configuredBaseUrl;

        var requestBaseUrl = context is null ? null : BuildRequestBaseUrl(context);
        if (!string.IsNullOrEmpty(requestBaseUrl))
            return requestBaseUrl;

        return new UriBuilder(Uri.UriSchemeHttp, startup.Config.BindAddress, startup.Config.Port)
            .Uri
            .GetLeftPart(UriPartial.Authority);
    }

    internal static string BuildAgentUrl(string publicBaseUrl, string pathPrefix)
        => NormalizePublicBaseUrl(publicBaseUrl)! + pathPrefix;

    internal static string GetWellKnownAgentCardPath(string pathPrefix)
        => pathPrefix + "/.well-known/agent-card.json";

    private static string? BuildRequestBaseUrl(HttpContext context)
    {
        if (!context.Request.Host.HasValue)
            return null;

        return UriHelper.BuildAbsolute(
                context.Request.Scheme,
                context.Request.Host,
                context.Request.PathBase)
            .TrimEnd('/');
    }

    private static string? NormalizePublicBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimEnd('/');
    }
}
#endif
