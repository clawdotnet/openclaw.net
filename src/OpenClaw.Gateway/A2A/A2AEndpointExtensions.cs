#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using A2A.AspNetCore;
using Microsoft.Extensions.Options;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

/// <summary>
/// Maps the A2A protocol endpoints (<c>message/send</c>, <c>message/stream</c>,
/// <c>.well-known/agent-card.json</c>) into the Gateway's HTTP pipeline,
/// protected by the same auth and rate limiting as all other OpenClaw endpoints.
/// </summary>
internal static class A2AEndpointExtensions
{
    /// <summary>
    /// Adds an authorization + rate-limiting middleware for requests targeting
    /// the A2A path prefix, then maps the A2A JSON-RPC and agent-card endpoints.
    /// </summary>
    public static void MapOpenClawA2AEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = options.A2APathPrefix.TrimEnd('/');

        // Resolve the A2A request handler (A2AServer) from DI
        var requestHandler = app.Services.GetRequiredService<IA2ARequestHandler>();

        // Build and register the agent card for service discovery
        var cardFactory = app.Services.GetRequiredService<OpenClawAgentCardFactory>();
        var agentUrl = $"http://{startup.Config.BindAddress}:{startup.Config.Port}{pathPrefix}";
        var agentCard = cardFactory.Create(agentUrl);

        // Map the A2A protocol endpoint (handles JSON-RPC POST for message/send, message/stream, etc.)
        // and the .well-known/agent-card.json discovery endpoint using the official SDK extension
        app.MapHttpA2A(requestHandler, agentCard, pathPrefix);
        app.MapWellKnownAgentCard(agentCard, pathPrefix);
        app.Logger.LogInformation(
            "A2A protocol endpoints mapped at {PathPrefix} with agent '{AgentName}'.",
            pathPrefix,
            options.AgentName);
    }

    /// <summary>
    /// Adds a middleware that enforces token-based authorization and rate limiting
    /// on requests targeting the A2A path prefix.
    /// </summary>
    public static void UseOpenClawA2AAuth(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = options.A2APathPrefix.TrimEnd('/');

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase))
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
}
#endif
