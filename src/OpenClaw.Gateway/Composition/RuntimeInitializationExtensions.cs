using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Integrations;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Profiles;
using OpenClaw.Gateway.Tools;
using OpenClaw.Gateway.Pipeline;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
    public static async Task<GatewayAppRuntime> InitializeOpenClawRuntimeAsync(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        var config = startup.Config;
        GatewaySecurityExtensions.ApplyStrictPublicBindProfile(config, startup.IsNonLoopbackBind);
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var startupLogger = loggerFactory.CreateLogger("Startup");
        var startupNoticeSink = app.Services.GetRequiredService<IStartupNoticeSink>();
        var browserAvailability = BrowserToolSupport.Evaluate(config, startup.RuntimeState);
        startupLogger.LogInformation(
            "Runtime mode resolved: requested={RequestedMode}, effective={EffectiveMode}, dynamicCodeSupported={DynamicCodeSupported}, orchestrator={Orchestrator}.",
            startup.RuntimeState.RequestedMode,
            startup.RuntimeState.EffectiveModeName,
            startup.RuntimeState.DynamicCodeSupported,
            RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator));
        if (browserAvailability.ConfiguredEnabled && !browserAvailability.Registered)
        {
            const string browserNotice = "Disabled: browser tool is unavailable because no execution backend or sandbox route is configured.";
            startupLogger.LogInformation(browserNotice);
            startupNoticeSink.Record(browserNotice);
        }
        if (startup.IsNonLoopbackBind && !config.Security.RequireRequesterMatchForHttpToolApproval)
        {
            startupLogger.LogWarning(
                "Requester-matched HTTP tool approvals are disabled on a non-loopback bind. Enable OpenClaw:Security:RequireRequesterMatchForHttpToolApproval for safer public deployments.");
        }
        var services = ResolveRuntimeServices(app);
        RecordLegacyMafConfigNotice(app, services, startupLogger, startupNoticeSink);
        var providerSmokeRegistry = app.Services.GetRequiredService<ProviderSmokeRegistry>();

        // Register observability gauge for pending tool approvals
        var approvalService = app.Services.GetRequiredService<ToolApprovalService>();
        Telemetry.RegisterApprovalQueueGauge(() => approvalService.PendingCount);

        var blockedPluginIds = services.PluginHealth.GetBlockedPluginIds();
        var channelComposition = await BuildChannelCompositionAsync(app, startup, services, loggerFactory);

        var builtInTools = CreateBuiltInTools(
            config,
            services,
            startup.WorkspacePath,
            startup.RuntimeState);
        if (config.Plugins.Mcp.Enabled)
            await services.McpRegistry.RegisterToolsAsync(services.NativeRegistry, app.Lifetime.ApplicationStopping);

        LlmClientFactory.ResetDynamicProviders();
        string? builtInInitError = null;
        try
        {
            services.ProviderRegistry.RegisterDefault(config.Llm, LlmClientFactory.CreateChatClient(config.Llm));
        }
        catch (InvalidOperationException ex)
        {
            builtInInitError = ex.Message;
            startupLogger.LogInformation(
                "Configured provider '{Provider}' was not available via built-in initialization at startup: {Reason}. Waiting for plugin-backed providers.",
                config.Llm.Provider,
                ex.Message);
        }

        var pluginComposition = await LoadPluginCompositionAsync(
            app,
            startup,
            services,
            loggerFactory,
            providerSmokeRegistry,
            channelComposition.ChannelAdapters,
            blockedPluginIds);

        if (!services.ProviderRegistry.MarkDefault(config.Llm.Provider) && !services.ProviderRegistry.TryGet(config.Llm.Provider, out _))
        {
            var suffix = builtInInitError is null
                ? string.Empty
                : $" Built-in provider initialization failed: {builtInInitError}.";
            throw new InvalidOperationException(
                $"Configured provider '{config.Llm.Provider}' is not available.{suffix} " +
                "Register it as the built-in provider or via a compatible plugin.");
        }

        var chatClient = services.ProviderRegistry.TryGet("default", out var defaultRegistration) && defaultRegistration is not null
            ? defaultRegistration.Client
            : LlmClientFactory.CreateChatClient(config.Llm);

        var resolveLogger = loggerFactory.CreateLogger("PluginResolver");
        IReadOnlyList<ITool> tools = NativePluginRegistry.ResolvePreference(
            builtInTools,
            services.NativeRegistry.Tools,
            [.. pluginComposition.BridgeTools, .. pluginComposition.NativeDynamicTools],
            config.Plugins,
            resolveLogger);

        var combinedPluginSkillRoots = CollectPluginSkillRoots(pluginComposition);

        var skillLogger = loggerFactory.CreateLogger("SkillLoader");
        var skills = SkillLoader.LoadAll(config.Skills, startup.WorkspacePath, skillLogger, combinedPluginSkillRoots);
        if (skills.Count > 0)
            skillLogger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));

        var hooks = CreateHooks(
            config,
            loggerFactory,
            pluginComposition.PluginHost,
            pluginComposition.NativeDynamicPluginHost,
            services.SessionManager,
            services.ContractGovernance);
        var (effectiveRequireToolApproval, effectiveApprovalRequiredTools) = ResolveApprovalMode(config);

        var agentLogger = loggerFactory.CreateLogger("AgentRuntime");
        var orchestratorId = RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator);
        var agentRuntime = CreateAgentRuntime(
            app.Services,
            config,
            startup.RuntimeState,
            chatClient,
            tools,
            services.MemoryStore,
            services.RuntimeMetrics,
            services.ProviderUsage,
            services.LlmExecutionService,
            skills,
            config.Skills,
            agentLogger,
            hooks,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools,
            services.ToolSandbox);

        // Wire compact callback so /compact command can trigger LLM-powered compaction
        if (agentRuntime is AgentRuntime concreteRuntime)
        {
            services.CommandProcessor.SetCompactCallback(async (session, ct) =>
            {
                var countBefore = session.History.Count;
                await concreteRuntime.CompactHistoryAsync(session, ct);
                var countAfter = session.History.Count;
                return countAfter; // Return actual remaining turn count
            });
        }

        var middlewarePipeline = CreateMiddlewarePipeline(config, loggerFactory, services.ContractGovernance, services.SessionManager);
        var skillWatcher = new SkillWatcherService(
            config.Skills,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            agentRuntime,
            app.Services.GetRequiredService<ILogger<SkillWatcherService>>());
        skillWatcher.Start(app.Lifetime.ApplicationStopping);

        await services.AutomationService.RefreshCacheAsync(app.Lifetime.ApplicationStopping);
        var cronScheduler = app.Services.GetRequiredService<CronScheduler>();
        StartNativeEventBridges(config, loggerFactory, services.Pipeline, app.Lifetime.ApplicationStopping);

        var profile = app.Services.GetRequiredService<IRuntimeProfile>();
        var shutdownCoordinator = app.Services.GetRequiredService<GatewayRuntimeShutdownCoordinator>();
        shutdownCoordinator.RegisterAsyncCleanup("mcp registry", _ => services.McpRegistry.DisposeAsync());
        var runtime = CreateGatewayRuntime(
            config,
            services,
            channelComposition,
            pluginComposition,
            agentRuntime,
            middlewarePipeline,
            skillWatcher,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools,
            orchestratorId,
            tools,
            skills,
            cronScheduler);
        shutdownCoordinator.AttachRuntime(startup, runtime);

        services.PluginHealth.SetRuntimeReports(
            runtime.PluginReports,
            pluginComposition.PluginHost,
            pluginComposition.NativeDynamicPluginHost);

        await profile.OnRuntimeInitializedAsync(app, startup, runtime);

        // Start integration services
        if (config.Tailscale.Enabled)
        {
            var tailscale = new Integrations.TailscaleService(
                config.Tailscale,
                config.Port,
                loggerFactory.CreateLogger<Integrations.TailscaleService>());
            shutdownCoordinator.RegisterAsyncCleanup("tailscale serve/funnel", _ => tailscale.DisposeAsync());
            await tailscale.StartAsync(app.Lifetime.ApplicationStopping);
        }

        if (config.Mdns.Enabled)
        {
            var mdns = new Integrations.MdnsDiscoveryService(
                config.Mdns,
                config.Port,
                authRequired: !string.IsNullOrWhiteSpace(config.AuthToken),
                loggerFactory.CreateLogger<Integrations.MdnsDiscoveryService>());
            shutdownCoordinator.RegisterAsyncCleanup("mDNS discovery", _ => mdns.DisposeAsync());
            mdns.Start(app.Lifetime.ApplicationStopping);
        }

        return runtime;
    }

}
