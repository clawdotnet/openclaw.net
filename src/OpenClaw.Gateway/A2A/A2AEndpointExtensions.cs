using System.Text.Json.Serialization.Metadata;
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

#pragma warning disable MEAI001
internal static class A2AEndpointExtensions
{
    internal const string StandardWellKnownAgentCardPath = "/.well-known/agent-card.json";
    private static readonly JsonTypeInfo<AgentCard> AgentCardJsonTypeInfo = MafJsonContext.Default.AgentCard;

    public static void MapOpenClawA2AEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = NormalizePathPrefix(options.A2APathPrefix);
        var cardFactory = app.Services.GetRequiredService<OpenClawAgentCardFactory>();
        var jsonRpcPath = GetJsonRpcPath(pathPrefix);
        var legacyAgentCardPath = GetLegacyWellKnownAgentCardPath(pathPrefix);

        app.MapA2AHttpJson(OpenClawA2ANames.AgentName, pathPrefix);
        app.MapA2AJsonRpc(OpenClawA2ANames.AgentName, jsonRpcPath);
        app.MapGet(StandardWellKnownAgentCardPath, (HttpContext context) =>
            CreateAgentCardResult(context, startup, options, cardFactory, pathPrefix, jsonRpcPath));
        app.MapGet(legacyAgentCardPath, (HttpContext context) =>
            CreateAgentCardResult(context, startup, options, cardFactory, pathPrefix, jsonRpcPath));
        var wellKnownLocation = string.IsNullOrWhiteSpace(options.A2APublicBaseUrl)
            ? StandardWellKnownAgentCardPath
            : BuildAgentUrl(ResolvePublicBaseUrl(null, startup, options), StandardWellKnownAgentCardPath);
        app.Logger.LogInformation(
            "A2A endpoints enabled at {HttpJsonPath} with JSON-RPC fallback at {JsonRpcPath}. Agent card discovery: {WellKnownLocation}",
            pathPrefix,
            jsonRpcPath,
            wellKnownLocation);
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
            if (IsA2ADiscoveryPath(ctx.Request.Path, pathPrefix))
            {
                await next(ctx);
                return;
            }

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

    internal static string GetWellKnownAgentCardPath()
        => StandardWellKnownAgentCardPath;

    internal static string GetLegacyWellKnownAgentCardPath(string pathPrefix)
        => pathPrefix + StandardWellKnownAgentCardPath;

    internal static string GetJsonRpcPath(string pathPrefix)
        => pathPrefix + "/rpc";

    internal static bool IsA2ADiscoveryPath(PathString requestPath, string pathPrefix)
    {
        var path = requestPath.Value;
        return string.Equals(path, StandardWellKnownAgentCardPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, GetLegacyWellKnownAgentCardPath(pathPrefix), StringComparison.OrdinalIgnoreCase);
    }

    internal static AgentCard BuildAgentCardForRequest(
        HttpContext context,
        GatewayStartupContext startup,
        MafOptions options,
        OpenClawAgentCardFactory cardFactory,
        string pathPrefix,
        string jsonRpcPath)
    {
        var publicBaseUrl = ResolvePublicBaseUrl(context, startup, options);
        return cardFactory.Create(
            BuildAgentUrl(publicBaseUrl, pathPrefix),
            BuildAgentUrl(publicBaseUrl, jsonRpcPath));
    }

    private static IResult CreateAgentCardResult(
        HttpContext context,
        GatewayStartupContext startup,
        MafOptions options,
        OpenClawAgentCardFactory cardFactory,
        string pathPrefix,
        string jsonRpcPath)
    {
        var card = BuildAgentCardForRequest(context, startup, options, cardFactory, pathPrefix, jsonRpcPath);
        return Results.Json(card, AgentCardJsonTypeInfo);
    }

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
#pragma warning restore MEAI001
