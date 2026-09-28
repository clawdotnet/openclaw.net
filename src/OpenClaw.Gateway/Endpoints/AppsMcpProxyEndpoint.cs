using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
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
        {
            sessionOptions.Handlers.ListToolsHandler = (_, _) =>
                ValueTask.FromResult(new ListToolsResult
                {
                    Tools = []
                });

            sessionOptions.Handlers.ListResourcesHandler = (_, _) =>
                ValueTask.FromResult(new ListResourcesResult
                {
                    Resources = []
                });

            sessionOptions.Handlers.ReadResourceHandler = (_, _) =>
                ValueTask.FromResult(new ReadResourceResult
                {
                    Contents = []
                });

            sessionOptions.Handlers.CallToolHandler = (_, _) =>
                ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"MCP app '{serverId}' is not loaded." }]
                });

            return;
        }

        var startup = httpContext.RequestServices.GetRequiredService<GatewayStartupContext>();
        var httpContextAccessor = httpContext.RequestServices.GetService<IHttpContextAccessor>();

        sessionOptions.Handlers.ListToolsHandler = async (ctx, ct2) =>
            await upstream.ListToolsAsync(ctx.Params ?? new ListToolsRequestParams(), ct2);

        sessionOptions.Handlers.ListResourcesHandler = async (ctx, ct2) =>
            await upstream.ListResourcesAsync(ctx.Params ?? new ListResourcesRequestParams(), ct2);

        sessionOptions.Handlers.ReadResourceHandler = async (ctx, ct2) =>
            await upstream.ReadResourceAsync(ctx.Params!, ct2);

        sessionOptions.Handlers.CallToolHandler = async (ctx, ct2) =>
        {
            var callParams = ctx.Params!;

            // App tools can change state and share the agent's upstream session, so calling them needs the same
            // operator role as /apps/chat, the host these UIs run in. Listing and reading stay open to viewers.
            // Check the current request rather than the one that opened a stateful session. Dynamic App tools
            // run synchronously so the request context remains available; if it is ever absent, fail closed.
            var caller = httpContextAccessor?.HttpContext;
            if (caller is null || !EndpointHelpers.CanExecuteAgent(
                    caller,
                    startup,
                    $"MCP App tool {serverId}/{callParams.Name}",
                    requireCsrf: true))
                throw new McpException(EndpointHelpers.OperatorRoleRequiredMessage);

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
            // Same bind-based rule as AppsEndpoints: a loopback client IP alone is not trusted.
            var httpContext = ctx.HttpContext;
            if (!EndpointHelpers.IsAuthorizedRequest(httpContext, startup.Config, startup.IsNonLoopbackBind))
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Empty;
            }

            return await next(ctx);
        });
    }
}
