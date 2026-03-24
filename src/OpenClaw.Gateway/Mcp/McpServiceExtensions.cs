using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;

namespace OpenClaw.Gateway.Mcp;

internal static class McpServiceExtensions
{
    /// <summary>
    /// Registers the official ModelContextProtocol.AspNetCore MCP server plus the
    /// DI infrastructure needed to bridge <see cref="GatewayAppRuntime"/> into
    /// the tool / resource / prompt classes.
    /// Call this from the service registration phase (before <c>builder.Build()</c>).
    /// Then call <see cref="InitializeMcpRuntime"/> after the runtime is created.
    /// </summary>
    public static IServiceCollection AddOpenClawMcpServices(
        this IServiceCollection services,
        GatewayStartupContext startup)
    {
        services.AddSingleton<GatewayRuntimeHolder>();

        services.AddSingleton<IntegrationApiFacade>(sp =>
        {
            var holder = sp.GetRequiredService<GatewayRuntimeHolder>();
            var sessionAdminStore = (ISessionAdminStore)sp.GetRequiredService<IMemoryStore>();
            return new IntegrationApiFacade(startup, holder.Runtime, sessionAdminStore);
        });

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "OpenClaw Gateway MCP",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithTools<OpenClawMcpTools>()
            .WithResources<OpenClawMcpResources>()
            .WithPrompts<OpenClawMcpPrompts>();

        return services;
    }

    /// <summary>
    /// Populates <see cref="GatewayRuntimeHolder.Runtime"/> after the runtime is created.
    /// Must be called before any MCP requests are served.
    /// </summary>
    public static void InitializeMcpRuntime(this WebApplication app, GatewayAppRuntime runtime)
    {
        app.Services.GetRequiredService<GatewayRuntimeHolder>().Runtime = runtime;
    }

    /// <summary>
    /// Adds a lightweight middleware that enforces the same token-based authorization
    /// and rate limiting used by all other OpenClaw endpoints on requests to <c>/mcp</c>.
    /// </summary>
    public static void UseOpenClawMcpAuth(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!runtime.Operations.ActorRateLimits.TryConsume(
                        "ip",
                        EndpointHelpers.GetRemoteIpKey(ctx),
                        "mcp_http",
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
