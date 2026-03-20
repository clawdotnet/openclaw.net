using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;

namespace OpenClaw.Gateway.Mcp;

internal static class McpSdkExtensions
{
    /// <summary>
    /// Registers the official ModelContextProtocol.AspNetCore MCP server plus the
    /// DI infrastructure needed to bridge <see cref="GatewayAppRuntime"/> into
    /// the tool / resource / prompt classes.
    /// Call this from the service registration phase (before <c>builder.Build()</c>).
    /// Then call <see cref="InitializeRuntime"/> after the runtime is created.
    /// </summary>
    public static IServiceCollection AddOpenClawMcpSdkServices(
        this IServiceCollection services,
        GatewayStartupContext startup)
    {
        // GatewayAppRuntime is built after the DI container; the holder bridges this gap.
        services.AddSingleton<GatewayRuntimeHolder>();

        // IntegrationApiFacade wraps all gate-level operations. Registered as singleton
        // because every dependency (GatewayRuntimeHolder, IMemoryStore) is also singleton.
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
            .WithHttpTransport()
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
    /// used by all other OpenClaw endpoints on requests to <c>/mcp</c>.
    /// </summary>
    public static void UseOpenClawMcpAuth(this WebApplication app, GatewayStartupContext startup)
    {
        var runtimeHolder = app.Services.GetRequiredService<GatewayRuntimeHolder>();

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
                    return;
                }

                if (!runtimeHolder.Runtime.Operations.ActorRateLimits.TryConsume(
                        "ip",
                        EndpointHelpers.GetRemoteIpKey(ctx),
                        "mcp_http",
                        out var blockedByPolicyId))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync($"{{\"error\":\"Rate limit exceeded by policy '{blockedByPolicyId}'.\"}}");
                    return;
                }
            }

            await next(ctx);
        });
    }
}
