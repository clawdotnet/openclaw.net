using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
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
using OpenClaw.Gateway.Profiles;

namespace OpenClaw.Gateway.Composition;

internal static class RuntimeInitializationExtensions
{
    public static async Task<GatewayAppRuntime> InitializeOpenClawRuntimeAsync(
        this WebApplication app,
        GatewayStartupContext startup)
    {
        var config = startup.Config;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        var allowlistSemantics = app.Services.GetRequiredService<AllowlistSemantics>();
        var allowlists = app.Services.GetRequiredService<AllowlistManager>();
        var recentSenders = app.Services.GetRequiredService<RecentSendersStore>();
        var sessionManager = app.Services.GetRequiredService<SessionManager>();
        var retentionCoordinator = app.Services.GetRequiredService<IMemoryRetentionCoordinator>();
        var pairingManager = app.Services.GetRequiredService<PairingManager>();
        var commandProcessor = app.Services.GetRequiredService<ChatCommandProcessor>();
        var toolApprovalService = app.Services.GetRequiredService<ToolApprovalService>();
        var approvalAuditStore = app.Services.GetRequiredService<ApprovalAuditStore>();
        var runtimeMetrics = app.Services.GetRequiredService<RuntimeMetrics>();
        var providerUsage = app.Services.GetRequiredService<ProviderUsageTracker>();
        var providerRegistry = app.Services.GetRequiredService<LlmProviderRegistry>();
        var providerPolicies = app.Services.GetRequiredService<ProviderPolicyService>();
        var llmExecutionService = app.Services.GetRequiredService<GatewayLlmExecutionService>();
        var runtimeEventStore = app.Services.GetRequiredService<RuntimeEventStore>();
        var operatorAuditStore = app.Services.GetRequiredService<OperatorAuditStore>();
        var approvalGrantStore = app.Services.GetRequiredService<ToolApprovalGrantStore>();
        var webhookDeliveryStore = app.Services.GetRequiredService<WebhookDeliveryStore>();
        var actorRateLimits = app.Services.GetRequiredService<ActorRateLimitService>();
        var sessionMetadataStore = app.Services.GetRequiredService<SessionMetadataStore>();
        var pluginHealth = app.Services.GetRequiredService<PluginHealthService>();
        var memoryStore = app.Services.GetRequiredService<IMemoryStore>();
        var toolSandbox = app.Services.GetService<IToolSandbox>();
        var pipeline = app.Services.GetRequiredService<MessagePipeline>();
        var wsChannel = app.Services.GetRequiredService<WebSocketChannel>();
        var nativeRegistry = app.Services.GetRequiredService<NativePluginRegistry>();
        var runtimeDiagnostics = new Dictionary<string, List<PluginCompatibilityDiagnostic>>(StringComparer.Ordinal);
        var dynamicProviderOwners = new HashSet<string>(StringComparer.Ordinal);
        var blockedPluginIds = pluginHealth.GetBlockedPluginIds();

        var (smsChannel, smsWebhookHandler) = CreateTwilioResources(
            config,
            allowlists,
            recentSenders,
            allowlistSemantics);

        var channelAdapters = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
        {
            ["websocket"] = wsChannel
        };

        if (smsChannel is not null)
            channelAdapters["sms"] = smsChannel;

        if (config.Channels.Telegram.Enabled)
            channelAdapters["telegram"] = app.Services.GetRequiredService<TelegramChannel>();

        if (config.Channels.WhatsApp.Enabled)
        {
            if (config.Channels.WhatsApp.Type == "bridge")
                channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppBridgeChannel>();
            else
                channelAdapters["whatsapp"] = app.Services.GetRequiredService<WhatsAppChannel>();
        }

        if (config.Plugins.Native.Email.Enabled)
        {
            channelAdapters["email"] = new EmailChannel(
                config.Plugins.Native.Email,
                loggerFactory.CreateLogger<EmailChannel>());
        }

        channelAdapters["cron"] = new CronChannel(
            config.Memory.StoragePath,
            loggerFactory.CreateLogger<CronChannel>());

        var builtInTools = CreateBuiltInTools(config, memoryStore, sessionManager, pipeline, startup.WorkspacePath);
        LlmClientFactory.ResetDynamicProviders();
        try
        {
            providerRegistry.RegisterDefault(config.Llm, LlmClientFactory.CreateChatClient(config.Llm));
        }
        catch (InvalidOperationException)
        {
            // Dynamic/plugin-backed providers may become available after plugin loading.
        }

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
                blockedPluginIds);
            bridgeTools = await pluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);

            RegisterBridgeChannels(channelAdapters, pluginHost, runtimeDiagnostics);
            RegisterBridgeCommands(commandProcessor, pluginHost, runtimeDiagnostics);
            RegisterBridgeProviders(loggerFactory, providerRegistry, pluginHost, runtimeDiagnostics, dynamicProviderOwners);
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
            RegisterNativeDynamicCommands(commandProcessor, nativeDynamicPluginHost, runtimeDiagnostics);
            RegisterNativeDynamicProviders(providerRegistry, nativeDynamicPluginHost, runtimeDiagnostics, dynamicProviderOwners);
        }
        if (!providerRegistry.MarkDefault(config.Llm.Provider) && !providerRegistry.TryGet(config.Llm.Provider, out _))
        {
            throw new InvalidOperationException(
                $"Configured provider '{config.Llm.Provider}' is not available. " +
                "Register it as the built-in provider or via a compatible plugin.");
        }

        var chatClient = providerRegistry.TryGet("default", out var defaultRegistration) && defaultRegistration is not null
            ? defaultRegistration.Client
            : LlmClientFactory.CreateChatClient(config.Llm);

        var resolveLogger = loggerFactory.CreateLogger("PluginResolver");
        IReadOnlyList<ITool> tools = NativePluginRegistry.ResolvePreference(
            builtInTools,
            nativeRegistry.Tools,
            [.. bridgeTools, .. nativeDynamicTools],
            config.Plugins,
            resolveLogger);

        var combinedPluginSkillRoots = new List<string>();
        if (pluginHost is not null)
            combinedPluginSkillRoots.AddRange(pluginHost.SkillRoots);
        if (nativeDynamicPluginHost is not null)
            combinedPluginSkillRoots.AddRange(nativeDynamicPluginHost.SkillRoots);

        var skillLogger = loggerFactory.CreateLogger("SkillLoader");
        var skills = SkillLoader.LoadAll(config.Skills, startup.WorkspacePath, skillLogger, combinedPluginSkillRoots);
        if (skills.Count > 0)
            skillLogger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));

        var hooks = CreateHooks(config, loggerFactory, pluginHost, nativeDynamicPluginHost);
        var (effectiveRequireToolApproval, effectiveApprovalRequiredTools) = ResolveApprovalMode(config);

        var agentLogger = loggerFactory.CreateLogger("AgentRuntime");
        var orchestratorId = RuntimeOrchestrator.Normalize(config.Runtime.Orchestrator);
        var agentRuntime = CreateAgentRuntime(
            app.Services,
            config,
            startup.RuntimeState,
            chatClient,
            tools,
            memoryStore,
            runtimeMetrics,
            providerUsage,
            llmExecutionService,
            skills,
            config.Skills,
            agentLogger,
            hooks,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            effectiveRequireToolApproval,
            effectiveApprovalRequiredTools,
            toolSandbox);

        var middlewarePipeline = CreateMiddlewarePipeline(config, loggerFactory);
        var skillWatcher = new SkillWatcherService(
            config.Skills,
            startup.WorkspacePath,
            combinedPluginSkillRoots,
            agentRuntime,
            app.Services.GetRequiredService<ILogger<SkillWatcherService>>());
        skillWatcher.Start(app.Lifetime.ApplicationStopping);

        var cronTask = StartCronIfEnabled(config, loggerFactory, pipeline, app.Lifetime.ApplicationStopping);
        StartNativeEventBridges(config, loggerFactory, pipeline, app.Lifetime.ApplicationStopping);

        var profile = app.Services.GetRequiredService<IRuntimeProfile>();
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = llmExecutionService,
            PluginHealth = pluginHealth,
            ApprovalGrants = approvalGrantStore,
            RuntimeEvents = runtimeEventStore,
            OperatorAudit = operatorAuditStore,
            WebhookDeliveries = webhookDeliveryStore,
            ActorRateLimits = actorRateLimits,
            SessionMetadata = sessionMetadataStore
        };

        var runtime = new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = orchestratorId,
            Pipeline = pipeline,
            MiddlewarePipeline = middlewarePipeline,
            WebSocketChannel = wsChannel,
            ChannelAdapters = channelAdapters,
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = pairingManager,
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            ApprovalAuditStore = approvalAuditStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            SkillWatcher = skillWatcher,
            PluginReports = GetCombinedPluginReports(pluginHost, nativeDynamicPluginHost, runtimeDiagnostics),
            Operations = operations,
            EffectiveRequireToolApproval = effectiveRequireToolApproval,
            EffectiveApprovalRequiredTools = effectiveApprovalRequiredTools,
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = config.Security.AllowedOrigins.Length > 0
                ? config.Security.AllowedOrigins.ToFrozenSet(StringComparer.Ordinal)
                : null,
            DynamicProviderOwners = dynamicProviderOwners.ToArray(),
            CronTask = cronTask,
            TwilioSmsWebhookHandler = smsWebhookHandler,
            PluginHost = pluginHost,
            NativeDynamicPluginHost = nativeDynamicPluginHost
        };

        pluginHealth.SetRuntimeReports(runtime.PluginReports, pluginHost, nativeDynamicPluginHost);

        await profile.OnRuntimeInitializedAsync(app, startup, runtime);
        return runtime;
    }

    private static IReadOnlyList<ITool> CreateBuiltInTools(
        GatewayConfig config,
        IMemoryStore memoryStore,
        SessionManager sessionManager,
        MessagePipeline pipeline,
        string? workspacePath)
    {
        var projectId = config.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT")
            ?? "default";

        var tools = new List<ITool>
        {
            new ShellTool(config.Tooling),
            new FileReadTool(config.Tooling),
            new FileWriteTool(config.Tooling),
            new MemoryNoteTool(memoryStore),
            new MemorySearchTool((IMemoryNoteSearch)memoryStore),
            new ProjectMemoryTool(memoryStore, projectId),
            new SessionsTool(sessionManager, pipeline.InboundWriter)
        };

        if (config.Tooling.EnableBrowserTool)
            tools.Add(new BrowserTool(config.Tooling));

        if (string.Equals(Environment.GetEnvironmentVariable("OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL"), "1", StringComparison.Ordinal))
            tools.Add(new StreamingSmokeEchoTool());

        return tools;
    }

    private static IReadOnlyList<IToolHook> CreateHooks(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost)
    {
        var hooks = new List<IToolHook>
        {
            new AuditLogHook(loggerFactory.CreateLogger("AuditLog")),
            new AutonomyHook(config.Tooling, loggerFactory.CreateLogger("AutonomyHook"))
        };

        if (pluginHost is not null)
            hooks.AddRange(pluginHost.ToolHooks);
        if (nativeDynamicPluginHost is not null)
            hooks.AddRange(nativeDynamicPluginHost.ToolHooks);

        return hooks;
    }

    private static (bool RequireApproval, IReadOnlyList<string> RequiredTools) ResolveApprovalMode(GatewayConfig config)
    {
        var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
        var effectiveRequireToolApproval = config.Tooling.RequireToolApproval || autonomyMode == "supervised";
        var effectiveApprovalRequiredTools = config.Tooling.ApprovalRequiredTools;

        if (autonomyMode == "supervised")
        {
            var defaults = new[]
            {
                "shell", "write_file", "code_exec", "git", "home_assistant_write", "mqtt_publish",
                "database", "email", "inbox_zero", "calendar", "delegate_agent"
            };

            effectiveApprovalRequiredTools = effectiveApprovalRequiredTools
                .Concat(defaults)
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
            ToolSandbox = toolSandbox
        });
    }

    private static MiddlewarePipeline CreateMiddlewarePipeline(GatewayConfig config, ILoggerFactory loggerFactory)
    {
        var middlewareList = new List<IMessageMiddleware>();
        if (config.SessionRateLimitPerMinute > 0)
            middlewareList.Add(new RateLimitMiddleware(config.SessionRateLimitPerMinute, loggerFactory.CreateLogger("RateLimit")));
        if (config.SessionTokenBudget > 0)
            middlewareList.Add(new TokenBudgetMiddleware(config.SessionTokenBudget, loggerFactory.CreateLogger("TokenBudget")));

        return new MiddlewarePipeline(middlewareList);
    }

    private static CronScheduler? StartCronIfEnabled(
        GatewayConfig config,
        ILoggerFactory loggerFactory,
        MessagePipeline pipeline,
        CancellationToken stoppingToken)
    {
        if (!config.Cron.Enabled)
            return null;

        var logger = loggerFactory.CreateLogger<CronScheduler>();
        var cronTask = new CronScheduler(config, logger, pipeline.InboundWriter);
        _ = cronTask.StartAsync(stoppingToken).ContinueWith(
            t => logger.LogError(t.Exception!.InnerException, "CronScheduler failed to start"),
            TaskContinuationOptions.OnlyOnFaulted);
        return cronTask;
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
}
