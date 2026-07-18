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
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Profiles;
using OpenClaw.Gateway.Tools;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.McpApp;
using OpenClaw.Plugins.TokenJuice;

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
        var approvalService = app.Services.GetRequiredService<ToolApprovalService>();
        Telemetry.RegisterApprovalQueueGauge(() => approvalService.PendingCount);
        var blockedPluginIds = services.PluginHealth.GetBlockedPluginIds();
        var channelComposition = await BuildChannelCompositionAsync(app, startup, services, loggerFactory);

        var artifactRuntime = new SkillArtifactRuntime();

        var builtInTools = CreateBuiltInTools(
            config,
            services,
            startup.WorkspacePath,
            startup.RuntimeState,
            artifactRuntime);
        if (config.Plugins.Mcp.Enabled)
            await services.McpRegistry.RegisterToolsAsync(services.NativeRegistry, app.Lifetime.ApplicationStopping);
        await using var mcpAppStartupCleanup = new AsyncStartupCleanupGuard();
        if (config.McpApps.Enabled)
        {
            await services.McpAppRegistry.RegisterMcpAppToolsAsync(services.NativeRegistry, config.McpApps, app.Lifetime.ApplicationStopping);
            mcpAppStartupCleanup.Register(() => services.McpAppRegistry.DisposeAsync());
        }
        LlmClientFactory.ResetDynamicProviders();
        var videoFrameExtraction = app.Services.GetRequiredService<IVideoFrameExtractionService>();
        string? builtInInitError = null;
        try
        {
            services.ProviderRegistry.RegisterDefault(config.Llm, LlmClientFactory.CreateChatClient(config.Llm, config.LocalInference, config.Multimodal, videoFrameExtraction));
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
            var suffix = builtInInitError is null ? string.Empty : $" Built-in provider initialization failed: {builtInInitError}.";
            throw new InvalidOperationException(
                $"Configured provider '{config.Llm.Provider}' is not available.{suffix} " +
                "Register it as the built-in provider or via a compatible plugin.");
        }
        services.ModelProfiles.SetDefaultProfileId();

        var chatClient = services.ProviderRegistry.TryGet("default", out var defaultRegistration) && defaultRegistration is not null
            ? defaultRegistration.Client
            : LlmClientFactory.CreateChatClient(config.Llm, config.LocalInference, config.Multimodal, videoFrameExtraction);

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
        artifactRuntime.ReplaceSkills(skills);
        IAgentRuntime? runtimeForLoadSkill = null;
        Func<IReadOnlyList<SkillDefinition>> skillsProvider = () => runtimeForLoadSkill?.LoadedSkills ?? skills;
        tools =
        [
            .. tools,
            new LoadSkillTool(skillsProvider),
            new ReadSkillResourceTool(skillsProvider, config.Skills.MaxResourceReadBytes),
            new MetaInvokeTool(skillsProvider)
        ];

        var hooks = CreateHooks(
            config,
            loggerFactory,
            pluginComposition.PluginHost,
            pluginComposition.NativeDynamicPluginHost,
            services.SessionManager,
            services.ContractGovernance);

        var interceptors = new List<IToolResultInterceptor>
        {
            TokenJuicePluginRegistration.CreateInterceptor()
        };

        var (effectiveRequireToolApproval, effectiveApprovalRequiredTools) = ResolveApprovalMode(config);

        var agentLogger = loggerFactory.CreateLogger("AgentRuntime");
        var orchestratorId = RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator);
        // Resolve workspace path: prefer the explicit env-var shortcut, fall back to the
        // config-based WorkspaceRoot reference (e.g. "raw:/path" or a different env var).
        // This ensures ReloadSkillsAsync finds workspace skills even when OPENCLAW_WORKSPACE
        // is not set directly but the workspace is configured via Tooling.WorkspaceRoot.
        var resolvedRuntimeWorkspacePath = startup.WorkspacePath
            ?? SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);

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
            resolvedRuntimeWorkspacePath,
            combinedPluginSkillRoots,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools,
            services.ToolSandbox,
            interceptors);
        runtimeForLoadSkill = agentRuntime;
        if (agentRuntime is AgentRuntime concreteRuntime)
        {
            services.CommandProcessor.SetCompactCallback(async (session, ct) =>
            {
                await concreteRuntime.CompactHistoryAsync(session, ct);
                return session.History.Count;
            });
        }

        WireLoopCommandCallback(app, services);

        var middlewarePipeline = CreateMiddlewarePipeline(config, loggerFactory, services.ContractGovernance, services.SessionManager);
        var skillWatcher = new SkillWatcherService(
            config.Skills,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            agentRuntime,
            app.Services.GetRequiredService<ILogger<SkillWatcherService>>(),
            skills => artifactRuntime.ReplaceSkills(skills));
        skillWatcher.Start(app.Lifetime.ApplicationStopping);
        var mcpWatcher = StartMcpWorkspaceWatcher(app, services, startup, agentRuntime);

        await services.AutomationService.RefreshCacheAsync(app.Lifetime.ApplicationStopping);
        var cronScheduler = app.Services.GetRequiredService<CronScheduler>();
        StartNativeEventBridges(config, loggerFactory, services.Pipeline, app.Lifetime.ApplicationStopping);

        var profile = app.Services.GetRequiredService<IRuntimeProfile>();
        var shutdownCoordinator = app.Services.GetRequiredService<GatewayRuntimeShutdownCoordinator>();
        shutdownCoordinator.RegisterAsyncCleanup("mcp registry", _ => services.McpRegistry.DisposeAsync());
        shutdownCoordinator.RegisterAsyncCleanup("mcp workspace watcher", _ => mcpWatcher.DisposeAsync());
        shutdownCoordinator.RegisterAsyncCleanup("mcpapp registry", _ => services.McpAppRegistry.DisposeAsync());
        mcpAppStartupCleanup.Cancel();
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
            cronScheduler,
            artifactRuntime);
        shutdownCoordinator.AttachRuntime(startup, runtime);

        services.PluginHealth.SetRuntimeReports(
            runtime.PluginReports,
            pluginComposition.PluginHost,
            pluginComposition.NativeDynamicPluginHost);

        await profile.OnRuntimeInitializedAsync(app, startup, runtime);
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
