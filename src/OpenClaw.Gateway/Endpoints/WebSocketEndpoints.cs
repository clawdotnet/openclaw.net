using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Core.Models;

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
            if (!TryValidateWebSocketRequest(ctx, startup, runtime, bucket: "websocket"))
                return;

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var clientId = ctx.Connection.Id;
            await runtime.WebSocketChannel.HandleConnectionAsync(ws, clientId, ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
        });

        app.Map("/ws/live", async (HttpContext ctx) =>
        {
            if (!TryValidateWebSocketRequest(ctx, startup, runtime, bucket: "websocket_live"))
                return;

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                var openRequest = await ReceiveLiveOpenRequestAsync(ws, ctx.RequestAborted);
                await ctx.RequestServices.GetRequiredService<LiveSessionService>()
                    .BridgeAsync(ws, openRequest, ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GeminiLive");
                logger.LogWarning(ex, "Live websocket session failed for connection {ConnectionId}", ctx.Connection.Id);
                await TrySendLiveErrorAsync(ws, ex.Message, CancellationToken.None);
            }
            finally
            {
                try
                {
                    if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
                catch
                {
                }
            }
        });
    }

    private static bool TryValidateWebSocketRequest(
        HttpContext ctx,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        string bucket)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        if (!IsOriginAllowed(ctx, runtime))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        if (startup.IsNonLoopbackBind && !IsAuthorizedTokenRequest(ctx, startup))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return false;
        }

        if (!runtime.Operations.ActorRateLimits.TryConsume("ip", EndpointHelpers.GetRemoteIpKey(ctx), bucket, out _))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return false;
        }

        return true;
    }

    private static bool IsAuthorizedTokenRequest(HttpContext ctx, GatewayStartupContext startup)
    {
        if (!startup.IsNonLoopbackBind)
            return true;

        var token = GatewaySecurity.GetToken(ctx, startup.Config.Security.AllowQueryStringToken);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var policy = ctx.RequestServices.GetService<OrganizationPolicyService>()?.GetSnapshot() ?? new OrganizationPolicySnapshot();
        if (policy.BootstrapTokenEnabled &&
            policy.AllowedAuthModes.Any(mode => string.Equals(mode, OrganizationAuthModeNames.BootstrapToken, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(startup.Config.AuthToken) &&
            GatewaySecurity.IsTokenValid(token, startup.Config.AuthToken))
        {
            return true;
        }

        var operatorAccounts = ctx.RequestServices.GetService<OperatorAccountService>();
        return operatorAccounts is not null &&
               policy.AllowedAuthModes.Any(mode => string.Equals(mode, OrganizationAuthModeNames.AccountToken, StringComparison.OrdinalIgnoreCase)) &&
               operatorAccounts.TryAuthenticateToken(token, out _);
    }

    private static bool IsOriginAllowed(HttpContext ctx, GatewayAppRuntime runtime)
    {
        if (!ctx.Request.Headers.TryGetValue("Origin", out var origin))
            return true;

        var originStr = origin.ToString();
        if (string.IsNullOrWhiteSpace(originStr))
            return true;

        if (runtime.AllowedOriginsSet is not null)
            return runtime.AllowedOriginsSet.Contains(originStr);

        if (!Uri.TryCreate(originStr, UriKind.Absolute, out var originUri))
            return false;

        var host = ctx.Request.Host;
        if (!host.HasValue)
            return false;

        var expectedScheme = ctx.Request.Scheme;
        var expectedHost = host.Host;
        var expectedPort = host.Port ?? (string.Equals(expectedScheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var originPort = originUri.IsDefaultPort
            ? (string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : originUri.Port;

        return string.Equals(originUri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            && originPort == expectedPort;
    }

    private static async Task<LiveSessionOpenRequest> ReceiveLiveOpenRequestAsync(WebSocket socket, CancellationToken ct)
    {
        var payload = await ReceiveTextAsync(socket, ct);
        if (string.IsNullOrWhiteSpace(payload))
            return new LiveSessionOpenRequest();

        return JsonSerializer.Deserialize(payload, CoreJsonContext.Default.LiveSessionOpenRequest)
            ?? new LiveSessionOpenRequest();
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static async Task TrySendLiveErrorAsync(WebSocket socket, string error, CancellationToken ct)
    {
        if (socket.State is not WebSocketState.Open)
            return;

        var payload = JsonSerializer.Serialize(new LiveServerEnvelope
        {
            Type = "error",
            Error = error
        }, CoreJsonContext.Default.LiveServerEnvelope);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
