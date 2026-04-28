using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Agent.Integrations;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.Tools;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
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
        GatewayRuntimeState runtimeState)
    {
        var projectId = config.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT")
            ?? "default";
        var browserAvailability = BrowserToolSupport.Evaluate(config, runtimeState);

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
            new CanvasPresentTool(services.CanvasBroker, config),
            new CanvasHideTool(services.CanvasBroker, config),
            new CanvasNavigateTool(services.CanvasBroker, config),
            new CanvasSnapshotTool(services.CanvasBroker, config),
            new A2UiPushTool(services.CanvasBroker, config),
            new A2UiResetTool(services.CanvasBroker, config),
            new A2UiEvalTool(services.CanvasBroker, config),

            new EditFileTool(config.Tooling),
            new ApplyPatchTool(config.Tooling),

            new SessionsHistoryTool(services.SessionManager, services.MemoryStore),
            new SessionsSendTool(services.SessionManager, services.Pipeline),
            new SessionsSpawnTool(services.SessionManager, services.Pipeline),
            new SessionStatusTool(services.SessionManager),
            new AgentsListTool(config.Delegation),

            new CronTool(services.CronJobSource, services.Pipeline),
            new GatewayTool(services.RuntimeMetrics, services.SessionManager, config, runtimeState),

            new MessageTool(services.Pipeline),
            new XSearchTool(),
            new MemoryGetTool(services.MemoryStore),
            new ProfileWriteTool(services.UserProfileStore),
            new SessionsYieldTool(services.SessionManager, services.Pipeline, services.MemoryStore),
        };

        if (browserAvailability.Registered)
            tools.Add(new BrowserTool(config.Tooling, services.RuntimeMetrics, browserAvailability.LocalExecutionSupported));

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
                "shell", "process", "write_file", "code_exec", "git", "home_assistant_write", "mqtt_publish", "notion_write",
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
            AppendContractSnapshot = (session, status) => contractGovernance.AppendSnapshot(session, status)
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
}
