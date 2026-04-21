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
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Profiles;
using OpenClaw.Gateway.Tools;

namespace OpenClaw.Gateway.Composition;

internal static class RuntimeInitializationExtensions
{
    public static async Task<GatewayAppRuntime> InitializeOpenClawRuntimeAsync(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        var config = startup.Config;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var startupLogger = loggerFactory.CreateLogger("Startup");
        startupLogger.LogInformation(
            "Runtime mode resolved: requested={RequestedMode}, effective={EffectiveMode}, dynamicCodeSupported={DynamicCodeSupported}, orchestrator={Orchestrator}.",
            startup.RuntimeState.RequestedMode,
            startup.RuntimeState.EffectiveModeName,
            startup.RuntimeState.DynamicCodeSupported,
            RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator));
        if (startup.IsNonLoopbackBind && !config.Security.RequireRequesterMatchForHttpToolApproval)
        {
            startupLogger.LogWarning(
                "Requester-matched HTTP tool approvals are disabled on a non-loopback bind. Enable OpenClaw:Security:RequireRequesterMatchForHttpToolApproval for safer public deployments.");
        }
        var services = ResolveRuntimeServices(app);

        // Register observability gauge for pending tool approvals
        var approvalService = app.Services.GetRequiredService<ToolApprovalService>();
        Telemetry.RegisterApprovalQueueGauge(() => approvalService.PendingCount);

        var blockedPluginIds = services.PluginHealth.GetBlockedPluginIds();
        var channelComposition = await BuildChannelCompositionAsync(app, startup, services, loggerFactory);

        var effectiveBrowserEnabled = config.Tooling.EnableBrowserTool;
        if (effectiveBrowserEnabled && !RuntimeFeature.IsDynamicCodeSupported)
        {
            startupLogger.LogWarning(
                "Browser tool is enabled (OpenClaw:Tooling:EnableBrowserTool=true) but this gateway is running without dynamic-code support (NativeAOT/trimmed). " +
                "Microsoft.Playwright uses reflection-based JSON serialization and will crash on first use. " +
                "Skipping browser tool registration. To use the browser tool, run a JIT build (OpenClaw:Runtime:Mode=jit) or publish without PublishAot; " +
                "to silence this warning, set OpenClaw:Tooling:EnableBrowserTool=false.");
            effectiveBrowserEnabled = false;
        }

        var builtInTools = CreateBuiltInTools(
            config,
            services,
            startup.WorkspacePath,
            effectiveBrowserEnabled);
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
            _ = tailscale.StartAsync(app.Lifetime.ApplicationStopping);
            app.Lifetime.ApplicationStopping.Register(() => tailscale.DisposeAsync().AsTask().GetAwaiter().GetResult());
        }

        if (config.Mdns.Enabled)
        {
            var mdns = new Integrations.MdnsDiscoveryService(
                config.Mdns,
                config.Port,
                authRequired: !string.IsNullOrWhiteSpace(config.AuthToken),
                loggerFactory.CreateLogger<Integrations.MdnsDiscoveryService>());
            mdns.Start(app.Lifetime.ApplicationStopping);
            app.Lifetime.ApplicationStopping.Register(() => mdns.DisposeAsync().AsTask().GetAwaiter().GetResult());
        }

        return runtime;
    }

    private static RuntimeServices ResolveRuntimeServices(WebApplication app)
        => new()
        {
            Allowlists = app.Services.GetRequiredService<AllowlistManager>(),
            AllowlistSemantics = app.Services.GetRequiredService<AllowlistSemantics>(),
            RecentSenders = app.Services.GetRequiredService<RecentSendersStore>(),
            SessionManager = app.Services.GetRequiredService<SessionManager>(),
            RetentionCoordinator = app.Services.GetRequiredService<IMemoryRetentionCoordinator>(),
            PairingManager = app.Services.GetRequiredService<PairingManager>(),
            CommandProcessor = app.Services.GetRequiredService<ChatCommandProcessor>(),
            ToolApprovalService = app.Services.GetRequiredService<ToolApprovalService>(),
            ApprovalAuditStore = app.Services.GetRequiredService<ApprovalAuditStore>(),
            RuntimeMetrics = app.Services.GetRequiredService<RuntimeMetrics>(),
            ProviderUsage = app.Services.GetRequiredService<ProviderUsageTracker>(),
            ModelProfiles = app.Services.GetRequiredService<ConfiguredModelProfileRegistry>(),
            ProviderRegistry = app.Services.GetRequiredService<LlmProviderRegistry>(),
            ProviderPolicies = app.Services.GetRequiredService<ProviderPolicyService>(),
            LlmExecutionService = app.Services.GetRequiredService<GatewayLlmExecutionService>(),
            RuntimeEventStore = app.Services.GetRequiredService<RuntimeEventStore>(),
            OperatorAuditStore = app.Services.GetRequiredService<OperatorAuditStore>(),
            ApprovalGrantStore = app.Services.GetRequiredService<ToolApprovalGrantStore>(),
            WebhookDeliveryStore = app.Services.GetRequiredService<WebhookDeliveryStore>(),
            ActorRateLimits = app.Services.GetRequiredService<ActorRateLimitService>(),
            SessionMetadataStore = app.Services.GetRequiredService<SessionMetadataStore>(),
            HeartbeatService = app.Services.GetRequiredService<HeartbeatService>(),
            AutomationService = app.Services.GetRequiredService<GatewayAutomationService>(),
            PluginHealth = app.Services.GetRequiredService<PluginHealthService>(),
            MemoryStore = app.Services.GetRequiredService<IMemoryStore>(),
            SessionSearchStore = app.Services.GetRequiredService<ISessionSearchStore>(),
            UserProfileStore = app.Services.GetRequiredService<IUserProfileStore>(),
            ProcessService = app.Services.GetRequiredService<ExecutionProcessService>(),
            GeminiMultimodalService = app.Services.GetRequiredService<GeminiMultimodalService>(),
            TextToSpeechService = app.Services.GetRequiredService<TextToSpeechService>(),
            LiveSessionService = app.Services.GetRequiredService<LiveSessionService>(),
            CronJobSource = app.Services.GetRequiredService<ICronJobSource>(),
            ContractGovernance = app.Services.GetRequiredService<ContractGovernanceService>(),
            ToolSandbox = app.Services.GetService<IToolSandbox>(),
            Pipeline = app.Services.GetRequiredService<MessagePipeline>(),
            WebSocketChannel = app.Services.GetRequiredService<WebSocketChannel>(),
            NativeRegistry = app.Services.GetRequiredService<NativePluginRegistry>(),
            McpRegistry = app.Services.GetRequiredService<McpServerToolRegistry>()
        };

    private static async Task<ChannelComposition> BuildChannelCompositionAsync(
        WebApplication app,
        GatewayStartupContext startup,
        RuntimeServices services,
        ILoggerFactory loggerFactory)
    {
        var config = startup.Config;
        var (smsChannel, smsWebhookHandler) = CreateTwilioResources(
            config,
            services.Allowlists,
            services.RecentSenders,
            services.AllowlistSemantics);

        var channelAdapters = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
        {
            ["websocket"] = services.WebSocketChannel
        };

        if (smsChannel is not null)
            channelAdapters["sms"] = smsChannel;

        if (config.Channels.Telegram.Enabled)
            channelAdapters["telegram"] = app.Services.GetRequiredService<TelegramChannel>();

        if (config.Channels.Teams.Enabled)
            channelAdapters["teams"] = app.Services.GetRequiredService<TeamsChannel>();

        if (config.Channels.Slack.Enabled)
            channelAdapters["slack"] = app.Services.GetRequiredService<SlackChannel>();

        if (config.Channels.Discord.Enabled)
            channelAdapters["discord"] = app.Services.GetRequiredService<DiscordChannel>();

        if (config.Channels.Signal.Enabled)
            channelAdapters["signal"] = app.Services.GetRequiredService<SignalChannel>();

        var whatsAppWorkerHost = await CreateWhatsAppChannelAsync(app, startup, services, loggerFactory, channelAdapters);

        if (config.Plugins.Native.Email.Enabled)
        {
            channelAdapters["email"] = new EmailChannel(
                config.Plugins.Native.Email,
                loggerFactory.CreateLogger<EmailChannel>());
        }

        channelAdapters["cron"] = new CronChannel(
            config.Memory.StoragePath,
            loggerFactory.CreateLogger<CronChannel>());

        return new ChannelComposition
        {
            ChannelAdapters = channelAdapters,
            TwilioSmsWebhookHandler = smsWebhookHandler,
            WhatsAppWorkerHost = whatsAppWorkerHost
        };
    }

    private static async Task<FirstPartyWhatsAppWorkerHost?> CreateWhatsAppChannelAsync(
        WebApplication app,
        GatewayStartupContext startup,
        RuntimeServices services,
        ILoggerFactory loggerFactory,
        IDictionary<string, IChannelAdapter> channelAdapters)
    {
        var config = startup.Config;
        if (!config.Channels.WhatsApp.Enabled)
            return null;

        if (string.Equals(config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            var launchSpec = FirstPartyWhatsAppWorkerHost.ResolveLaunchSpec(config.Channels.WhatsApp.FirstPartyWorker);
            var whatsAppWorkerHost = new FirstPartyWhatsAppWorkerHost(
                Path.Combine(AppContext.BaseDirectory, "Plugins", "plugin-bridge.mjs"),
                launchSpec,
                loggerFactory.CreateLogger<FirstPartyWhatsAppWorkerHost>(),
                config.Plugins.Transport,
                Path.Combine(config.Memory.StoragePath, "runtime"),
                services.RuntimeMetrics);
            var workerChannels = await whatsAppWorkerHost.LoadAsync(
                config.Channels.WhatsApp.FirstPartyWorker,
                app.Lifetime.ApplicationStopping);
            foreach (var workerChannel in workerChannels)
                channelAdapters[workerChannel.ChannelId] = workerChannel;

            return whatsAppWorkerHost;
        }

        if (string.Equals(config.Channels.WhatsApp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
            channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppBridgeChannel>();
        else
            channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppChannel>();

        return null;
    }

    private static async Task<PluginComposition> LoadPluginCompositionAsync(
        WebApplication app,
        GatewayStartupContext startup,
        RuntimeServices services,
        ILoggerFactory loggerFactory,
        IDictionary<string, IChannelAdapter> channelAdapters,
        IReadOnlyCollection<string> blockedPluginIds)
    {
        var config = startup.Config;
        var runtimeDiagnostics = new Dictionary<string, List<PluginCompatibilityDiagnostic>>(StringComparer.Ordinal);
        var dynamicProviderOwners = new HashSet<string>(StringComparer.Ordinal);
        PluginHost? pluginHost = null;
        NativeDynamicPluginHost? nativeDynamicPluginHost = null;
        IReadOnlyList<ITool> bridgeTools = [];
        IReadOnlyList<ITool> nativeDynamicTools = [];

        if (config.Plugins.Enabled)
        {
            var bridgeScript = Path.Combine(AppContext.BaseDirectory, "Plugins", "plugin-bridge.mjs");
            pluginHost = new PluginHost(
                config.Plugins,
                bridgeScript,
                loggerFactory.CreateLogger<PluginHost>(),
                startup.RuntimeState,
                blockedPluginIds,
                Path.Combine(config.Memory.StoragePath, "runtime"),
                services.RuntimeMetrics);
            bridgeTools = await pluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);

            RegisterBridgeChannels(channelAdapters, pluginHost, runtimeDiagnostics);
            RegisterBridgeCommands(services.CommandProcessor, pluginHost, runtimeDiagnostics);
            RegisterBridgeProviders(loggerFactory, services.ProviderRegistry, pluginHost, runtimeDiagnostics, dynamicProviderOwners);
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            nativeDynamicPluginHost = new NativeDynamicPluginHost(
                config.Plugins.DynamicNative,
                startup.RuntimeState,
                loggerFactory.CreateLogger<NativeDynamicPluginHost>(),
                blockedPluginIds);
            nativeDynamicTools = await nativeDynamicPluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);

            RegisterNativeDynamicChannels(channelAdapters, nativeDynamicPluginHost, runtimeDiagnostics);
            RegisterNativeDynamicCommands(services.CommandProcessor, nativeDynamicPluginHost, runtimeDiagnostics);
            RegisterNativeDynamicProviders(services.ProviderRegistry, nativeDynamicPluginHost, runtimeDiagnostics, dynamicProviderOwners);
        }

        return new PluginComposition
        {
            PluginHost = pluginHost,
            NativeDynamicPluginHost = nativeDynamicPluginHost,
            BridgeTools = bridgeTools,
            NativeDynamicTools = nativeDynamicTools,
            RuntimeDiagnostics = runtimeDiagnostics,
            DynamicProviderOwners = [.. dynamicProviderOwners]
        };
    }

    private static List<string> CollectPluginSkillRoots(PluginComposition pluginComposition)
    {
        var combinedPluginSkillRoots = new List<string>();
        if (pluginComposition.PluginHost is not null)
            combinedPluginSkillRoots.AddRange(pluginComposition.PluginHost.SkillRoots);
        if (pluginComposition.NativeDynamicPluginHost is not null)
            combinedPluginSkillRoots.AddRange(pluginComposition.NativeDynamicPluginHost.SkillRoots);
        return combinedPluginSkillRoots;
    }

    private static GatewayAppRuntime CreateGatewayRuntime(
        GatewayConfig config,
        RuntimeServices services,
        ChannelComposition channelComposition,
        PluginComposition pluginComposition,
        IAgentRuntime agentRuntime,
        MiddlewarePipeline middlewarePipeline,
        SkillWatcherService skillWatcher,
        bool effectiveRequireToolApproval,
        IReadOnlyList<string> effectiveApprovalRequiredTools,
        string orchestratorId,
        IReadOnlyList<ITool> tools,
        IReadOnlyList<SkillDefinition> skills,
        CronScheduler? cronScheduler)
    {
        var operations = new RuntimeOperationsState
        {
            ModelProfiles = services.ModelProfiles,
            ProviderPolicies = services.ProviderPolicies,
            ProviderRegistry = services.ProviderRegistry,
            LlmExecution = services.LlmExecutionService,
            PluginHealth = services.PluginHealth,
            ApprovalGrants = services.ApprovalGrantStore,
            RuntimeEvents = services.RuntimeEventStore,
            OperatorAudit = services.OperatorAuditStore,
            WebhookDeliveries = services.WebhookDeliveryStore,
            ActorRateLimits = services.ActorRateLimits,
            SessionMetadata = services.SessionMetadataStore
        };

        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = orchestratorId,
            Pipeline = services.Pipeline,
            MiddlewarePipeline = middlewarePipeline,
            WebSocketChannel = services.WebSocketChannel,
            ChannelAdapters = channelComposition.ChannelAdapters,
            SessionManager = services.SessionManager,
            RetentionCoordinator = services.RetentionCoordinator,
            PairingManager = services.PairingManager,
            Allowlists = services.Allowlists,
            AllowlistSemantics = services.AllowlistSemantics,
            RecentSenders = services.RecentSenders,
            CommandProcessor = services.CommandProcessor,
            ToolApprovalService = services.ToolApprovalService,
            ApprovalAuditStore = services.ApprovalAuditStore,
            RuntimeMetrics = services.RuntimeMetrics,
            ProviderUsage = services.ProviderUsage,
            Heartbeat = services.HeartbeatService,
            LoadedSkills = skills,
            SkillWatcher = skillWatcher,
            PluginReports = GetCombinedPluginReports(
                pluginComposition.PluginHost,
                pluginComposition.NativeDynamicPluginHost,
                pluginComposition.RuntimeDiagnostics),
            Operations = operations,
            EffectiveRequireToolApproval = effectiveRequireToolApproval,
            EffectiveApprovalRequiredTools = effectiveApprovalRequiredTools,
            NativeRegistry = services.NativeRegistry,
            SessionLocks = services.SessionManager.SessionLocks,
            LockLastUsed = services.SessionManager.LockLastUsed,
            AllowedOriginsSet = config.Security.AllowedOrigins.Length > 0
                ? config.Security.AllowedOrigins.ToFrozenSet(StringComparer.Ordinal)
                : null,
            DynamicProviderOwners = pluginComposition.DynamicProviderOwners,
            EstimatedSkillPromptChars = SkillPromptBuilder.EstimateCharacterCost(skills),
            CronTask = cronScheduler,
            TwilioSmsWebhookHandler = channelComposition.TwilioSmsWebhookHandler,
            PluginHost = pluginComposition.PluginHost,
            NativeDynamicPluginHost = pluginComposition.NativeDynamicPluginHost,
            WhatsAppWorkerHost = channelComposition.WhatsAppWorkerHost,
            RegisteredToolNames = tools.Select(t => t.Name).ToFrozenSet(StringComparer.Ordinal),
            ChannelAuthEvents = WireChannelAuthEvents(channelComposition.ChannelAdapters)
        };
    }

    private static IReadOnlyList<ITool> CreateBuiltInTools(
        GatewayConfig config,
        RuntimeServices services,
        string? workspacePath,
        bool browserToolEnabled)
    {
        var projectId = config.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT")
            ?? "default";

        var tools = new List<ITool>
        {
            new ShellTool(config.Tooling),
            new FileReadTool(config.Tooling),
            new FileWriteTool(config.Tooling),
            new ProcessTool(services.ProcessService, config.Tooling),
            new MemoryNoteTool(services.MemoryStore),
            new MemorySearchTool((IMemoryNoteSearch)services.MemoryStore),
            new ProjectMemoryTool(services.MemoryStore, projectId),
            new SessionsTool(services.SessionManager, services.Pipeline.InboundWriter),
            new SessionSearchTool(services.SessionSearchStore),
            new ProfileReadTool(services.UserProfileStore),
            new TodoTool(services.SessionMetadataStore),
            new AutomationTool(services.AutomationService, services.Pipeline),
            new VisionAnalyzeTool(services.GeminiMultimodalService),
            new TextToSpeechTool(services.TextToSpeechService),

            // Core dev tools
            new EditFileTool(config.Tooling),
            new ApplyPatchTool(config.Tooling),

            // Session management tools
            new SessionsHistoryTool(services.SessionManager, services.MemoryStore),
            new SessionsSendTool(services.SessionManager, services.Pipeline),
            new SessionsSpawnTool(services.SessionManager, services.Pipeline),
            new SessionStatusTool(services.SessionManager),
            new AgentsListTool(config.Delegation),

            // System management tools
            new CronTool(services.CronJobSource, services.Pipeline),
            new GatewayTool(services.RuntimeMetrics, services.SessionManager, config),

            // Communication & data tools
            new MessageTool(services.Pipeline),
            new XSearchTool(),
            new MemoryGetTool(services.MemoryStore),
            new ProfileWriteTool(services.UserProfileStore),
            new SessionsYieldTool(services.SessionManager, services.Pipeline, services.MemoryStore),
        };

        if (browserToolEnabled)
            tools.Add(new BrowserTool(config.Tooling, services.RuntimeMetrics));

        if (string.Equals(Environment.GetEnvironmentVariable("OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL"), "1", StringComparison.Ordinal))
            tools.Add(new StreamingSmokeEchoTool());

        return tools;
    }

    private static IReadOnlyList<IToolHook> CreateHooks(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost,
        SessionManager sessionManager,
        ContractGovernanceService contractGovernance)
    {
        var hooks = new List<IToolHook>
        {
            new AuditLogHook(loggerFactory.CreateLogger("AuditLog")),
            new AutonomyHook(config.Tooling, loggerFactory.CreateLogger("AutonomyHook")),
            new ContractScopeHook(
                sessionId =>
                {
                    var session = sessionManager.TryGetActiveById(sessionId);
                    return session?.ContractPolicy;
                },
                sessionId =>
                {
                    // Approximate tool call count from provider usage turns as a proxy
                    // The actual count is tracked on the session's TurnContext at runtime
                    var session = sessionManager.TryGetActiveById(sessionId);
                    if (session is null) return 0;
                    return session.History
                        .Where(t => t.ToolCalls is { Count: > 0 })
                        .Sum(t => t.ToolCalls!.Count);
                },
                loggerFactory.CreateLogger("ContractScopeHook"))
        };

        if (pluginHost is not null)
            hooks.AddRange(pluginHost.ToolHooks);
        if (nativeDynamicPluginHost is not null)
            hooks.AddRange(nativeDynamicPluginHost.ToolHooks);

        return hooks;
    }

    internal static (bool RequireApproval, IReadOnlyList<string> RequiredTools) ResolveApprovalMode(GatewayConfig config)
    {
        var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
        var requireNotionWriteApproval = config.Plugins.Native.Notion.Enabled &&
            !config.Plugins.Native.Notion.ReadOnly &&
            config.Plugins.Native.Notion.RequireApprovalForWrites;

        var effectiveRequireToolApproval = config.Tooling.RequireToolApproval || autonomyMode == "supervised" || requireNotionWriteApproval;
        var effectiveApprovalRequiredTools = config.Tooling.ApprovalRequiredTools;

        if (autonomyMode == "supervised")
        {
            var defaults = new[]
            {
                "shell", "write_file", "code_exec", "git", "home_assistant_write", "mqtt_publish", "notion_write",
                "database", "email", "inbox_zero", "calendar", "delegate_agent"
            };

            effectiveApprovalRequiredTools = effectiveApprovalRequiredTools
                .Concat(defaults)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (requireNotionWriteApproval)
        {
            effectiveApprovalRequiredTools = effectiveApprovalRequiredTools
                .Concat(["notion_write"])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return (effectiveRequireToolApproval, effectiveApprovalRequiredTools);
    }

    private static IAgentRuntime CreateAgentRuntime(
        IServiceProvider services,
        GatewayConfig config,
        GatewayRuntimeState runtimeState,
        IChatClient chatClient,
        IReadOnlyList<ITool> tools,
        IMemoryStore memoryStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILlmExecutionService llmExecutionService,
        IReadOnlyList<SkillDefinition> skills,
        SkillsConfig skillsConfig,
        ILogger logger,
        IReadOnlyList<IToolHook> hooks,
        string? workspacePath,
        IReadOnlyList<string> pluginSkillDirs,
        bool requireToolApproval,
        IReadOnlyList<string> approvalRequiredTools,
        IToolSandbox? toolSandbox)
    {
        var factory = AgentRuntimeFactorySelector.Select(
            services.GetServices<IAgentRuntimeFactory>(),
            config.Runtime.Orchestrator);
        var contractGovernance = services.GetRequiredService<ContractGovernanceService>();

        return factory.Create(new AgentRuntimeFactoryContext
        {
            Services = services,
            Config = config,
            RuntimeState = runtimeState,
            ChatClient = chatClient,
            Tools = tools,
            MemoryStore = memoryStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            LlmExecutionService = llmExecutionService,
            Skills = skills,
            SkillsConfig = skillsConfig,
            WorkspacePath = workspacePath,
            PluginSkillDirs = pluginSkillDirs,
            Logger = logger,
            Hooks = hooks,
            RequireToolApproval = requireToolApproval,
            ApprovalRequiredTools = approvalRequiredTools,
            ToolSandbox = toolSandbox,
            ToolUsageTracker = services.GetRequiredService<ToolUsageTracker>(),
            ToolAuditLog = services.GetRequiredService<ToolAuditLog>(),
            IsContractTokenBudgetExceeded = contractGovernance.IsTokenBudgetExceeded,
            IsContractRuntimeBudgetExceeded = contractGovernance.IsRuntimeBudgetExceeded,
            RecordContractTurnUsage = contractGovernance.RecordTurnUsage,
            AppendContractSnapshot = contractGovernance.AppendSnapshot
        });
    }

    private static MiddlewarePipeline CreateMiddlewarePipeline(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        ContractGovernanceService contractGovernance,
        SessionManager sessionManager)
    {
        var middlewareList = new List<IMessageMiddleware>();
        if (config.SessionRateLimitPerMinute > 0)
            middlewareList.Add(new RateLimitMiddleware(config.SessionRateLimitPerMinute, loggerFactory.CreateLogger("RateLimit")));

        Func<string?, string, string, (decimal, decimal, bool)> costChecker =
            (sessionId, channelId, senderId) => contractGovernance.CheckCostBudget(sessionId, channelId, senderId, sessionManager);

        middlewareList.Add(new TokenBudgetMiddleware(
            config.SessionTokenBudget,
            loggerFactory.CreateLogger("TokenBudget"),
            costChecker: costChecker));

        return new MiddlewarePipeline(middlewareList);
    }

    private static void StartNativeEventBridges(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        MessagePipeline pipeline,
        CancellationToken stoppingToken)
    {
        if (config.Plugins.Native.HomeAssistant.Enabled && config.Plugins.Native.HomeAssistant.Events.Enabled)
        {
            var haLogger = loggerFactory.CreateLogger<HomeAssistantEventBridge>();
            var haBridge = new HomeAssistantEventBridge(
                config.Plugins.Native.HomeAssistant,
                haLogger,
                pipeline.InboundWriter);
            _ = haBridge.StartAsync(stoppingToken).ContinueWith(
                t => haLogger.LogError(t.Exception!.InnerException, "HomeAssistant event bridge failed to start"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        if (config.Plugins.Native.Mqtt.Enabled && config.Plugins.Native.Mqtt.Events.Enabled)
        {
            var mqttLogger = loggerFactory.CreateLogger<MqttEventBridge>();
            var mqttBridge = new MqttEventBridge(
                config.Plugins.Native.Mqtt,
                mqttLogger,
                pipeline.InboundWriter);
            _ = mqttBridge.StartAsync(stoppingToken).ContinueWith(
                t => mqttLogger.LogError(t.Exception!.InnerException, "MQTT event bridge failed to start"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static (TwilioSmsChannel? Channel, TwilioSmsWebhookHandler? Handler) CreateTwilioResources(
        GatewayConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics)
    {
        if (!config.Channels.Sms.Twilio.Enabled)
            return (null, null);

        if (config.Channels.Sms.Twilio.ValidateSignature &&
            string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.WebhookPublicBaseUrl))
        {
            throw new InvalidOperationException("OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl must be set when ValidateSignature is true.");
        }

        var twilioAuthToken = OpenClaw.Core.Security.SecretResolver.Resolve(config.Channels.Sms.Twilio.AuthTokenRef)
            ?? throw new InvalidOperationException("Twilio AuthTokenRef is not configured or could not be resolved.");

        var smsContacts = new FileContactStore(config.Memory.StoragePath);
        var httpClient = OpenClaw.Core.Http.HttpClientFactory.Create();
        var smsChannel = new TwilioSmsChannel(config.Channels.Sms.Twilio, twilioAuthToken, smsContacts, httpClient);
        var handler = new TwilioSmsWebhookHandler(
            config.Channels.Sms.Twilio,
            twilioAuthToken,
            smsContacts,
            allowlists,
            recentSenders,
            allowlistSemantics);

        return (smsChannel, handler);
    }

    private static void RegisterBridgeChannels(
        IDictionary<string, IChannelAdapter> channelAdapters,
        PluginHost pluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics)
    {
        foreach (var (pluginId, channelId, adapter) in pluginHost.ChannelRegistrations)
        {
            if (channelAdapters.TryAdd(channelId, adapter))
                continue;

            AddDiagnostic(
                runtimeDiagnostics,
                pluginId,
                "duplicate_channel_id",
                $"Channel '{channelId}' from plugin '{pluginId}' was skipped because that channel id is already registered.",
                "registerChannel",
                channelId);
        }
    }

    private static void RegisterNativeDynamicChannels(
        IDictionary<string, IChannelAdapter> channelAdapters,
        NativeDynamicPluginHost nativeDynamicPluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics)
    {
        foreach (var (pluginId, channelId, adapter) in nativeDynamicPluginHost.ChannelRegistrations)
        {
            if (channelAdapters.TryAdd(channelId, adapter))
                continue;

            AddDiagnostic(
                runtimeDiagnostics,
                pluginId,
                "duplicate_channel_id",
                $"Channel '{channelId}' from dynamic native plugin '{pluginId}' was skipped because that channel id is already registered.",
                "registerChannel",
                channelId);
        }
    }

    private static void RegisterBridgeCommands(
        ChatCommandProcessor commandProcessor,
        PluginHost pluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics)
    {
        foreach (var (pluginId, name, _, bridge) in pluginHost.CommandRegistrations)
        {
            var result = commandProcessor.RegisterDynamic(name, async (args, ct) =>
            {
                var response = await bridge.SendAndWaitAsync(
                    "command_execute",
                    new BridgeCommandExecuteRequest
                    {
                        Name = name,
                        Args = args,
                    },
                    CoreJsonContext.Default.BridgeCommandExecuteRequest,
                    ct);

                if (response.Error is not null)
                    return $"Command error: {response.Error.Message}";

                if (response.Result is { } value && value.TryGetProperty("result", out var resultValue))
                    return resultValue.GetString() ?? "";

                return response.Result?.GetRawText() ?? "";
            });

            AddCommandRegistrationDiagnostic(runtimeDiagnostics, pluginId, name, result);
        }
    }

    private static void RegisterNativeDynamicCommands(
        ChatCommandProcessor commandProcessor,
        NativeDynamicPluginHost nativeDynamicPluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics)
    {
        foreach (var (pluginId, name, _, handler) in nativeDynamicPluginHost.CommandRegistrations)
        {
            var result = commandProcessor.RegisterDynamic(name, handler);
            AddCommandRegistrationDiagnostic(runtimeDiagnostics, pluginId, name, result);
        }
    }

    private static void AddCommandRegistrationDiagnostic(
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics,
        string pluginId,
        string name,
        DynamicCommandRegistrationResult result)
    {
        switch (result)
        {
            case DynamicCommandRegistrationResult.Registered:
                return;
            case DynamicCommandRegistrationResult.ReservedBuiltIn:
                AddDiagnostic(
                    runtimeDiagnostics,
                    pluginId,
                    "reserved_command_name",
                    $"Command '/{name.TrimStart('/')}' from plugin '{pluginId}' was skipped because built-in commands are reserved.",
                    "registerCommand",
                    name);
                return;
            default:
                AddDiagnostic(
                    runtimeDiagnostics,
                    pluginId,
                    "duplicate_command_name",
                    $"Command '/{name.TrimStart('/')}' from plugin '{pluginId}' was skipped because that command name is already registered.",
                    "registerCommand",
                    name);
                return;
        }
    }

    private static void RegisterBridgeProviders(
        ILoggerFactory loggerFactory,
        LlmProviderRegistry providerRegistry,
        PluginHost pluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics,
        ISet<string> dynamicProviderOwners)
    {
        foreach (var (pluginId, providerId, _, bridge) in pluginHost.ProviderRegistrationsDetailed)
        {
            var ownerId = $"bridge:{pluginId}";
            var bridgedProvider = new BridgedLlmProvider(
                bridge,
                providerId,
                loggerFactory.CreateLogger<BridgedLlmProvider>());

            if (providerRegistry.TryRegisterDynamic(providerId, bridgedProvider, ownerId, []))
            {
                dynamicProviderOwners.Add(ownerId);
                continue;
            }

            AddDiagnostic(
                runtimeDiagnostics,
                pluginId,
                "duplicate_provider_id",
                $"Provider '{providerId}' from plugin '{pluginId}' was skipped because that provider id is already registered.",
                "registerProvider",
                providerId);
        }
    }

    private static void RegisterNativeDynamicProviders(
        LlmProviderRegistry providerRegistry,
        NativeDynamicPluginHost nativeDynamicPluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics,
        ISet<string> dynamicProviderOwners)
    {
        foreach (var (pluginId, providerId, _, client) in nativeDynamicPluginHost.ProviderRegistrationsDetailed)
        {
            var ownerId = $"native_dynamic:{pluginId}";
            if (providerRegistry.TryRegisterDynamic(providerId, client, ownerId, []))
            {
                dynamicProviderOwners.Add(ownerId);
                continue;
            }

            AddDiagnostic(
                runtimeDiagnostics,
                pluginId,
                "duplicate_provider_id",
                $"Provider '{providerId}' from dynamic native plugin '{pluginId}' was skipped because that provider id is already registered.",
                "registerProvider",
                providerId);
        }
    }

    private static void AddDiagnostic(
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics,
        string pluginId,
        string code,
        string message,
        string surface,
        string? path)
    {
        if (!runtimeDiagnostics.TryGetValue(pluginId, out var list))
        {
            list = [];
            runtimeDiagnostics[pluginId] = list;
        }

        list.Add(new PluginCompatibilityDiagnostic
        {
            Severity = "warning",
            Code = code,
            Message = message,
            Surface = surface,
            Path = path
        });
    }

    private static IReadOnlyList<PluginLoadReport> GetCombinedPluginReports(
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost,
        IReadOnlyDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics)
    {
        var reports = new List<PluginLoadReport>();
        if (pluginHost is not null)
            reports.AddRange(pluginHost.Reports);
        if (nativeDynamicPluginHost is not null)
            reports.AddRange(nativeDynamicPluginHost.Reports);

        if (runtimeDiagnostics.Count == 0)
            return reports;

        return reports
            .Select(report =>
            {
                if (!runtimeDiagnostics.TryGetValue(report.PluginId, out var diagnostics) || diagnostics.Count == 0)
                    return report;

                return new PluginLoadReport
                {
                    PluginId = report.PluginId,
                    SourcePath = report.SourcePath,
                    EntryPath = report.EntryPath,
                    Origin = report.Origin,
                    Loaded = report.Loaded,
                    EffectiveRuntimeMode = report.EffectiveRuntimeMode,
                    RequestedCapabilities = report.RequestedCapabilities,
                    BlockedByRuntimeMode = report.BlockedByRuntimeMode,
                    BlockedReason = report.BlockedReason,
                    ToolCount = report.ToolCount,
                    ChannelCount = report.ChannelCount,
                    CommandCount = report.CommandCount,
                    EventSubscriptionCount = report.EventSubscriptionCount,
                    ProviderCount = report.ProviderCount,
                    SkillDirectories = report.SkillDirectories,
                    Diagnostics = [.. report.Diagnostics, .. diagnostics],
                    Error = report.Error
                };
            })
            .ToArray();
    }

    private static ChannelAuthEventStore WireChannelAuthEvents(
        IReadOnlyDictionary<string, IChannelAdapter> channelAdapters)
    {
        var store = new ChannelAuthEventStore();
        foreach (var adapter in channelAdapters.Values)
        {
            if (adapter is Agent.Plugins.BridgedChannelAdapter bridged)
            {
                bridged.OnAuthEvent += store.Record;
            }
        }
        return store;
    }

    private sealed class RuntimeServices
    {
        public required AllowlistManager Allowlists { get; init; }
        public required AllowlistSemantics AllowlistSemantics { get; init; }
        public required RecentSendersStore RecentSenders { get; init; }
        public required SessionManager SessionManager { get; init; }
        public required IMemoryRetentionCoordinator RetentionCoordinator { get; init; }
        public required PairingManager PairingManager { get; init; }
        public required ChatCommandProcessor CommandProcessor { get; init; }
        public required ToolApprovalService ToolApprovalService { get; init; }
        public required ApprovalAuditStore ApprovalAuditStore { get; init; }
        public required RuntimeMetrics RuntimeMetrics { get; init; }
        public required ProviderUsageTracker ProviderUsage { get; init; }
        public required ConfiguredModelProfileRegistry ModelProfiles { get; init; }
        public required LlmProviderRegistry ProviderRegistry { get; init; }
        public required ProviderPolicyService ProviderPolicies { get; init; }
        public required GatewayLlmExecutionService LlmExecutionService { get; init; }
        public required RuntimeEventStore RuntimeEventStore { get; init; }
        public required OperatorAuditStore OperatorAuditStore { get; init; }
        public required ToolApprovalGrantStore ApprovalGrantStore { get; init; }
        public required WebhookDeliveryStore WebhookDeliveryStore { get; init; }
        public required ActorRateLimitService ActorRateLimits { get; init; }
        public required SessionMetadataStore SessionMetadataStore { get; init; }
        public required HeartbeatService HeartbeatService { get; init; }
        public required GatewayAutomationService AutomationService { get; init; }
        public required PluginHealthService PluginHealth { get; init; }
        public required IMemoryStore MemoryStore { get; init; }
        public required ISessionSearchStore SessionSearchStore { get; init; }
        public required IUserProfileStore UserProfileStore { get; init; }
        public required ExecutionProcessService ProcessService { get; init; }
        public required GeminiMultimodalService GeminiMultimodalService { get; init; }
        public required TextToSpeechService TextToSpeechService { get; init; }
        public required LiveSessionService LiveSessionService { get; init; }
        public required ICronJobSource CronJobSource { get; init; }
        public required ContractGovernanceService ContractGovernance { get; init; }
        public IToolSandbox? ToolSandbox { get; init; }
        public required MessagePipeline Pipeline { get; init; }
        public required WebSocketChannel WebSocketChannel { get; init; }
        public required NativePluginRegistry NativeRegistry { get; init; }
        public required McpServerToolRegistry McpRegistry { get; init; }
    }

    private sealed class ChannelComposition
    {
        public required Dictionary<string, IChannelAdapter> ChannelAdapters { get; init; }
        public TwilioSmsWebhookHandler? TwilioSmsWebhookHandler { get; init; }
        public FirstPartyWhatsAppWorkerHost? WhatsAppWorkerHost { get; init; }
    }

    private sealed class PluginComposition
    {
        public PluginHost? PluginHost { get; init; }
        public NativeDynamicPluginHost? NativeDynamicPluginHost { get; init; }
        public required IReadOnlyList<ITool> BridgeTools { get; init; }
        public required IReadOnlyList<ITool> NativeDynamicTools { get; init; }
        public required IReadOnlyDictionary<string, List<PluginCompatibilityDiagnostic>> RuntimeDiagnostics { get; init; }
        public required IReadOnlyList<string> DynamicProviderOwners { get; init; }
    }
}
