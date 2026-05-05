using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Payments.Core;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
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
            PaymentRuntime = app.Services.GetRequiredService<PaymentRuntimeService>(),
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
            CanvasBroker = app.Services.GetRequiredService<CanvasCommandBroker>(),
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
        ProviderSmokeRegistry providerSmokeRegistry,
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
            RegisterBridgeProviders(loggerFactory, services.ProviderRegistry, providerSmokeRegistry, pluginHost, runtimeDiagnostics, dynamicProviderOwners);
        }

        if (config.Plugins.DynamicNative.Enabled)
        {
            nativeDynamicPluginHost = startup.NativeDynamicPluginHost;
            if (nativeDynamicPluginHost is null)
            {
                nativeDynamicPluginHost = new NativeDynamicPluginHost(
                    config.Plugins.DynamicNative,
                    startup.RuntimeState,
                    loggerFactory.CreateLogger<NativeDynamicPluginHost>(),
                    blockedPluginIds);
                nativeDynamicTools = await nativeDynamicPluginHost.LoadAsync(startup.WorkspacePath, app.Lifetime.ApplicationStopping);
            }
            else
            {
                nativeDynamicTools = nativeDynamicPluginHost.Tools;
            }

            RegisterNativeDynamicChannels(channelAdapters, nativeDynamicPluginHost, runtimeDiagnostics);
            RegisterNativeDynamicCommands(services.CommandProcessor, nativeDynamicPluginHost, runtimeDiagnostics);
            RegisterNativeDynamicProviders(services.ProviderRegistry, providerSmokeRegistry, nativeDynamicPluginHost, runtimeDiagnostics, dynamicProviderOwners);
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
        ProviderSmokeRegistry providerSmokeRegistry,
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
                providerSmokeRegistry.RegisterMetadata(
                    providerId,
                    treatAsConfigured: true,
                    skipReason: $"Dynamic provider '{providerId}' is registered through a plugin bridge and does not expose a smoke probe.");
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
        ProviderSmokeRegistry providerSmokeRegistry,
        NativeDynamicPluginHost nativeDynamicPluginHost,
        IDictionary<string, List<PluginCompatibilityDiagnostic>> runtimeDiagnostics,
        ISet<string> dynamicProviderOwners)
    {
        foreach (var (pluginId, providerId, _, client) in nativeDynamicPluginHost.ProviderRegistrationsDetailed)
        {
            var ownerId = $"native_dynamic:{pluginId}";
            if (providerRegistry.TryRegisterDynamic(providerId, client, ownerId, []))
            {
                providerSmokeRegistry.RegisterMetadata(
                    providerId,
                    treatAsConfigured: true,
                    skipReason: $"Dynamic provider '{providerId}' is registered through a native plugin and does not expose a smoke probe.");
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
        public required PaymentRuntimeService PaymentRuntime { get; init; }
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
        public required CanvasCommandBroker CanvasBroker { get; init; }
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
