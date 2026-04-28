using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Models;
using QRCoder;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    public static void MapOpenClawAdminEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operatorAccounts = app.Services.GetRequiredService<OperatorAccountService>();
        var organizationPolicy = app.Services.GetRequiredService<OrganizationPolicyService>();
        var adminSettings = app.Services.GetRequiredService<AdminSettingsService>();
        var pluginAdminSettings = app.Services.GetRequiredService<PluginAdminSettingsService>();
        var heartbeat = app.Services.GetRequiredService<HeartbeatService>();
        var memoryStore = app.Services.GetRequiredService<IMemoryStore>();
        var memorySearch = memoryStore as IMemoryNoteSearch;
        var memoryCatalog = memoryStore as IMemoryNoteCatalog;
        var fallbackFeatureStore = FeatureFallbackServices.CreateFallbackFeatureStore(startup);
        var profileStore = app.Services.GetService<IUserProfileStore>() ?? fallbackFeatureStore;
        var proposalStore = app.Services.GetService<ILearningProposalStore>() ?? fallbackFeatureStore;
        var automationService = FeatureFallbackServices.ResolveAutomationService(startup, app.Services, heartbeat, fallbackFeatureStore);
        var learningService = FeatureFallbackServices.ResolveLearningService(startup, app.Services, fallbackFeatureStore);
        var facade = IntegrationApiFacade.Create(startup, runtime, app.Services);
        var sessionMetadataStore = app.Services.GetService<SessionMetadataStore>()
            ?? new SessionMetadataStore(startup.Config.Memory.StoragePath, NullLogger<SessionMetadataStore>.Instance);
        var toolPresetResolver = new ToolPresetResolver(startup.Config, sessionMetadataStore);
        var sessionAdminStore = app.Services.GetRequiredService<ISessionAdminStore>();
        var observability = new AdminObservabilityService(
            startup,
            runtime,
            automationService,
            organizationPolicy,
            app.Services.GetRequiredService<ToolUsageTracker>(),
            sessionAdminStore);
        var maintenance = app.Services.GetService<GatewayMaintenanceRuntimeService>()
            ?? new GatewayMaintenanceRuntimeService(startup, runtime, automationService);
        var providerSmokeRegistry = app.Services.GetService<ProviderSmokeRegistry>()
            ?? new ProviderSmokeRegistry();
        var setupVerificationSnapshots = app.Services.GetService<SetupVerificationSnapshotStore>()
            ?? new SetupVerificationSnapshotStore(startup.Config.Memory.StoragePath);
        var modelProfiles = app.Services.GetService<IModelProfileRegistry>()
            ?? runtime.Operations.ModelProfiles as IModelProfileRegistry
            ?? new ConfiguredModelProfileRegistry(startup.Config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelEvaluationRunner = app.Services.GetService<ModelEvaluationRunner>()
            ?? new ModelEvaluationRunner(
                runtime.Operations.ModelProfiles as ConfiguredModelProfileRegistry
                    ?? new ConfiguredModelProfileRegistry(startup.Config, NullLogger<ConfiguredModelProfileRegistry>.Instance),
                startup.Config,
                NullLogger<ModelEvaluationRunner>.Instance);

        var services = new AdminEndpointServices
        {
            Startup = startup,
            Runtime = runtime,
            BrowserSessions = browserSessions,
            OperatorAccounts = operatorAccounts,
            OrganizationPolicy = organizationPolicy,
            AdminSettings = adminSettings,
            PluginAdminSettings = pluginAdminSettings,
            Heartbeat = heartbeat,
            MemoryStore = memoryStore,
            MemorySearch = memorySearch,
            MemoryCatalog = memoryCatalog,
            ProfileStore = profileStore,
            ProposalStore = proposalStore,
            AutomationService = automationService,
            LearningService = learningService,
            Facade = facade,
            ToolPresetResolver = toolPresetResolver,
            Observability = observability,
            Maintenance = maintenance,
            SessionAdminStore = sessionAdminStore,
            Operations = runtime.Operations,
            ProviderSmokeRegistry = providerSmokeRegistry,
            SetupVerificationSnapshots = setupVerificationSnapshots,
            ModelProfiles = modelProfiles,
            ModelEvaluationRunner = modelEvaluationRunner
        };

        MapAuthEndpoints(app, services);
        MapSetupEndpoints(app, services);
        MapSessionEndpoints(app, services);
        MapAutomationEndpoints(app, services);
        MapMemoryEndpoints(app, services);
        MapProfilesAndLearningEndpoints(app, services);
        MapRuntimeEndpoints(app, services);
        MapPluginAndChannelEndpoints(app, services);
    }
}
