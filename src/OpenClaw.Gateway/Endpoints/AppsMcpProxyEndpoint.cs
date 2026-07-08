using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.McpApp;
using System.Text.Json.Nodes;

namespace OpenClaw.Gateway.Endpoints;

internal static class AppsMcpProxyEndpoint
{
    public static async Task ConfigureSessionOptionsAsync(
        HttpContext httpContext,
        McpServerOptions sessionOptions,
        CancellationToken ct)
    {
        if (httpContext.Request.RouteValues["serverId"] is not string serverId || string.IsNullOrEmpty(serverId))
            return;

        var sessionId = httpContext.Request.Query["sessionId"].Count > 0
            ? httpContext.Request.Query["sessionId"].ToString()
            : null;

        var registry = httpContext.RequestServices.GetRequiredService<McpAppRegistry>();
        var upstream = registry.GetApp(serverId)?.Client;
        if (upstream is null)
            return;

        sessionOptions.Handlers.ListToolsHandler = async (ctx, ct2) =>
            await upstream.ListToolsAsync(ctx.Params ?? new ListToolsRequestParams(), ct2);

        sessionOptions.Handlers.ListResourcesHandler = async (ctx, ct2) =>
            await upstream.ListResourcesAsync(ctx.Params ?? new ListResourcesRequestParams(), ct2);

        sessionOptions.Handlers.ReadResourceHandler = async (ctx, ct2) =>
            await upstream.ReadResourceAsync(ctx.Params!, ct2);

        sessionOptions.Handlers.CallToolHandler = async (ctx, ct2) =>
        {
            var callParams = ctx.Params!;
            if (!string.IsNullOrEmpty(sessionId))
            {
                callParams.Meta ??= new JsonObject();
                callParams.Meta["sessionId"] = JsonValue.Create(sessionId);
            }

            return await upstream.CallToolAsync(callParams, ct2);
        };

        await Task.CompletedTask;
    }

    public static void MapOpenClawAppsMcpProxy(this WebApplication app, GatewayStartupContext startup)
    {
        app.MapMcp("/apps/mcp/{serverId}").AddEndpointFilter(async (ctx, next) =>
        {
            var httpContext = ctx.HttpContext;
            var ip = httpContext.Connection.RemoteIpAddress;
            var authorized = (ip is not null && System.Net.IPAddress.IsLoopback(ip))
                || EndpointHelpers.IsAuthorizedRequest(httpContext, startup.Config, startup.IsNonLoopbackBind);
            if (!authorized)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Empty;
            }

            return await next(ctx);
        });
    }
}