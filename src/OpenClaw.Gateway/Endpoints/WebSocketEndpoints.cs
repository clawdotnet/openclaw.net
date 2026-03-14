using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WebSocketEndpoints
{
    public static void MapOpenClawWebSocketEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
            {
                var originStr = origin.ToString();
                if (!string.IsNullOrWhiteSpace(originStr))
                {
                    if (runtime.AllowedOriginsSet is not null)
                    {
                        if (!runtime.AllowedOriginsSet.Contains(originStr))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                    else
                    {
                        if (!Uri.TryCreate(originStr, UriKind.Absolute, out var originUri))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        var host = ctx.Request.Host;
                        if (!host.HasValue)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        var expectedScheme = ctx.Request.Scheme;
                        var expectedHost = host.Host;
                        var expectedPort = host.Port ?? (string.Equals(expectedScheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
                        var originPort = originUri.IsDefaultPort
                            ? (string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                            : originUri.Port;

                        var sameOrigin =
                            string.Equals(originUri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(originUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
                            originPort == expectedPort;

                        if (!sameOrigin)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                }
            }

            if (startup.IsNonLoopbackBind && !EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!runtime.Operations.ActorRateLimits.TryConsume("ip", EndpointHelpers.GetRemoteIpKey(ctx), "websocket", out _))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var clientId = ctx.Connection.Id;
            await runtime.WebSocketChannel.HandleConnectionAsync(ws, clientId, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
        });
    }
}
