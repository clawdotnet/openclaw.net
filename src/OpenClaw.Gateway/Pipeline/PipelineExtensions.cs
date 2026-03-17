using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Pipeline;

internal static class PipelineExtensions
{
    public static void UseOpenClawPipeline(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        ConfigureForwardedHeaders(app, startup);
        ConfigureCors(app, runtime);

        app.UseStaticFiles();

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        StartWorkers(app, startup, runtime);
        StartChannels(app, runtime);
        RegisterShutdown(app, startup, runtime);
        LogStartupBanner(app, startup);
    }

    private static void ConfigureForwardedHeaders(WebApplication app, GatewayStartupContext startup)
    {
        if (!startup.Config.Security.TrustForwardedHeaders)
            return;

        var opts = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        foreach (var proxy in startup.Config.Security.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var ip))
                opts.KnownProxies.Add(ip);
        }

        app.UseForwardedHeaders(opts);
    }

    private static void ConfigureCors(WebApplication app, GatewayAppRuntime runtime)
    {
        if (runtime.AllowedOriginsSet is null)
            return;

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
            {
                var originStr = origin.ToString();
                if (runtime.AllowedOriginsSet.Contains(originStr))
                {
                    ctx.Response.Headers["Access-Control-Allow-Origin"] = originStr;
                    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                    ctx.Response.Headers["Access-Control-Max-Age"] = "3600";
                    ctx.Response.Headers.Vary = "Origin";
                }

                if (ctx.Request.Method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    return;
                }
            }

            await next();
        });
    }

    private static void StartWorkers(WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway");
        var workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

        GatewayWorkers.Start(
            app.Lifetime,
            logger,
            workerCount,
            startup.IsNonLoopbackBind,
            runtime.SessionManager,
            runtime.SessionLocks,
            runtime.LockLastUsed,
            runtime.Pipeline,
            runtime.MiddlewarePipeline,
            runtime.WebSocketChannel,
            runtime.AgentRuntime,
            runtime.ChannelAdapters,
            startup.Config,
            runtime.CronTask,
            runtime.ToolApprovalService,
            runtime.ApprovalAuditStore,
            runtime.PairingManager,
            runtime.CommandProcessor,
            runtime.Operations);
    }

    private static void StartChannels(WebApplication app, GatewayAppRuntime runtime)
    {
        async ValueTask HandleInboundMessageAsync(IChannelAdapter replyAdapter, OpenClaw.Core.Models.InboundMessage msg, CancellationToken ct)
        {
            await runtime.RecentSenders.RecordAsync(msg.ChannelId, msg.SenderId, msg.SenderName, ct);
            if (!runtime.Pipeline.InboundWriter.TryWrite(msg))
            {
                try
                {
                    await replyAdapter.SendAsync(new OpenClaw.Core.Models.OutboundMessage
                    {
                        ChannelId = msg.ChannelId,
                        RecipientId = msg.SenderId,
                        Text = "Server is busy. Please retry.",
                        ReplyToMessageId = msg.MessageId
                    }, ct);
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning(ex, "Failed to send busy response on channel {ChannelId}", replyAdapter.ChannelId);
                }
            }
        }

        foreach (var adapter in runtime.ChannelAdapters.Values)
        {
            adapter.OnMessageReceived += (msg, ct) => HandleInboundMessageAsync(adapter, msg, ct);

            _ = Task.Run(async () =>
            {
                try
                {
                    await adapter.StartAsync(app.Lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Channel adapter {ChannelId} stopped unexpectedly", adapter.ChannelId);
                }
            }, CancellationToken.None);
        }
    }

    private static void RegisterShutdown(WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        var drainCompleteEvent = new ManualResetEventSlim(false);
        var pluginDisposeTimeout = TimeSpan.FromSeconds(Math.Max(startup.Config.GracefulShutdownSeconds, 10));

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            app.Logger.LogInformation(
                "Shutdown signal received — draining in-flight requests ({Timeout}s timeout)…",
                startup.Config.GracefulShutdownSeconds);

            if (startup.Config.GracefulShutdownSeconds > 0)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(startup.Config.GracefulShutdownSeconds);
                var checkInterval = TimeSpan.FromMilliseconds(100);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    var allFree = true;
                    foreach (var kvp in runtime.SessionLocks)
                    {
                        if (kvp.Value.CurrentCount == 0)
                        {
                            allFree = false;
                            break;
                        }
                    }

                    if (allFree)
                    {
                        drainCompleteEvent.Set();
                        break;
                    }

                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        drainCompleteEvent.Wait(checkInterval < remaining ? checkInterval : remaining);
                }

                app.Logger.LogInformation("Drain complete — shutting down");
            }

            DisposePluginHostWithTimeout(runtime.PluginHost, pluginDisposeTimeout, app.Logger);
            DisposePluginHostWithTimeout(runtime.NativeDynamicPluginHost, pluginDisposeTimeout, app.Logger);
            foreach (var ownerId in runtime.DynamicProviderOwners)
            {
                runtime.Operations.ProviderRegistry.UnregisterOwnedBy(ownerId);
                LlmClientFactory.UnregisterProvidersOwnedBy(ownerId);
            }
            runtime.NativeRegistry.Dispose();
            runtime.SkillWatcher.Dispose();
            drainCompleteEvent.Dispose();
        });
    }

    private static void DisposePluginHostWithTimeout(IAsyncDisposable? host, TimeSpan timeout, ILogger logger)
    {
        if (host is null)
            return;

        try
        {
            if (!host.DisposeAsync().AsTask().Wait(timeout))
                logger.LogWarning("Plugin host disposal timed out after {Seconds}s", timeout.TotalSeconds);
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex.InnerException, "Plugin host disposal threw an exception");
        }
    }

    private static void LogStartupBanner(WebApplication app, GatewayStartupContext startup)
    {
        if (!app.Logger.IsEnabled(LogLevel.Information))
            return;

        var isAot = startup.RuntimeState.EffectiveMode == OpenClaw.Core.Models.GatewayRuntimeMode.Aot;
        app.Logger.LogInformation(
            """
            ╔══════════════════════════════════════════╗
            ║  OpenClaw.NET Gateway                    ║
            ║  Listening: ws://{BindAddress}:{Port}/ws  ║
            ║  Model: {Model}  ║
            ║  Runtime Mode: {RuntimeMode}  ║
            ║  NativeAOT: {NativeAOT}  ║
            ╚══════════════════════════════════════════╝
            """,
            startup.Config.BindAddress,
            startup.Config.Port,
            startup.Config.Llm.Model,
            startup.RuntimeState.EffectiveModeName,
            isAot ? "Yes" : "No");
    }
}
