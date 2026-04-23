using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Compatibility;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal sealed class IntegrationApiFacade
{
    private readonly GatewayStartupContext _startup;
    private readonly GatewayAppRuntime _runtime;
    private readonly ISessionAdminStore _sessionAdminStore;
    private readonly ISessionSearchStore _sessionSearchStore;
    private readonly IUserProfileStore _profileStore;
    private readonly GatewayAutomationService _automationService;
    private readonly LearningService _learningService;
    private readonly IMemoryNoteCatalog? _memoryCatalog;
    private readonly IToolPresetResolver? _toolPresetResolver;
    private readonly TextToSpeechService? _textToSpeechService;

    public static IntegrationApiFacade Create(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        IServiceProvider services)
    {
        var sessionAdminStore = services.GetRequiredService<ISessionAdminStore>();
        var sessionSearchStore = FeatureFallbackServices.ResolveSessionSearchStore(services);
        var fallbackFeatureStore = FeatureFallbackServices.CreateFallbackFeatureStore(startup);
        var profileStore = services.GetService<IUserProfileStore>() ?? fallbackFeatureStore;
        var heartbeat = services.GetService<HeartbeatService>()
            ?? throw new InvalidOperationException("HeartbeatService must be registered before mapping integration endpoints.");
        var automationService = FeatureFallbackServices.ResolveAutomationService(startup, services, heartbeat, fallbackFeatureStore);
        var learningService = FeatureFallbackServices.ResolveLearningService(startup, services, fallbackFeatureStore);
        var memoryCatalog = services.GetService<IMemoryStore>() as IMemoryNoteCatalog;
        var toolPresetResolver = services.GetService<IToolPresetResolver>();
        var textToSpeechService = services.GetService<TextToSpeechService>();

        return new IntegrationApiFacade(
            startup,
            runtime,
            sessionAdminStore,
            sessionSearchStore,
            profileStore,
            automationService,
            learningService,
            memoryCatalog,
            toolPresetResolver,
            textToSpeechService);
    }

    public IntegrationApiFacade(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        ISessionAdminStore sessionAdminStore,
        ISessionSearchStore sessionSearchStore,
        IUserProfileStore profileStore,
        GatewayAutomationService automationService,
        LearningService learningService,
        IMemoryNoteCatalog? memoryCatalog,
        IToolPresetResolver? toolPresetResolver,
        TextToSpeechService? textToSpeechService)
    {
        _startup = startup;
        _runtime = runtime;
        _sessionAdminStore = sessionAdminStore;
        _sessionSearchStore = sessionSearchStore;
        _profileStore = profileStore;
        _automationService = automationService;
        _learningService = learningService;
        _memoryCatalog = memoryCatalog;
        _toolPresetResolver = toolPresetResolver;
        _textToSpeechService = textToSpeechService;
    }

    public IntegrationStatusResponse BuildStatusResponse()
    {
        _runtime.RuntimeMetrics.SetActiveSessions(_runtime.SessionManager.ActiveCount);
        _runtime.RuntimeMetrics.SetCircuitBreakerState((int)_runtime.AgentRuntime.CircuitBreakerState);

        return new IntegrationStatusResponse
        {
            Health = new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 },
            Runtime = _startup.RuntimeState,
            Metrics = _runtime.RuntimeMetrics.Snapshot(),
            ActiveSessions = _runtime.SessionManager.ActiveCount,
            PendingApprovals = _runtime.ToolApprovalService.ListPending().Count,
            ActiveApprovalGrants = _runtime.Operations.ApprovalGrants.List().Count
        };
    }

    public async Task<IntegrationSessionsResponse> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken cancellationToken)
    {
        var metadataById = _runtime.Operations.SessionMetadata.GetAll();
        var persisted = await SessionAdminPersistedListing.ListPersistedAsync(
            _sessionAdminStore,
            page,
            pageSize,
            query,
            metadataById,
            cancellationToken);
        var active = (await _runtime.SessionManager.ListActiveAsync(cancellationToken))
            .Where(session => SessionAdminQuery.MatchesSessionQuery(session, query, metadataById))
            .OrderByDescending(static session => session.LastActiveAt)
            .Select(static session => new SessionSummary
            {
                Id = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                CreatedAt = session.CreatedAt,
                LastActiveAt = session.LastActiveAt,
                State = session.State,
                HistoryTurns = session.History.Count,
                TotalInputTokens = session.TotalInputTokens,
                TotalOutputTokens = session.TotalOutputTokens,
                IsActive = true
            })
            .ToArray();

        return new IntegrationSessionsResponse
        {
            Filters = query,
            Active = active,
            Persisted = persisted
        };
    }

    public async Task<IntegrationSessionDetailResponse?> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        var session = await _runtime.SessionManager.LoadAsync(id, cancellationToken);
        if (session is null)
            return null;

        var branches = await _runtime.SessionManager.ListBranchesAsync(id, cancellationToken);

        return new IntegrationSessionDetailResponse
        {
            Session = session,
            IsActive = _runtime.SessionManager.IsActive(id),
            BranchCount = branches.Count,
            Metadata = _runtime.Operations.SessionMetadata.Get(id)
        };
    }

    public async Task<IntegrationSessionTimelineResponse?> GetSessionTimelineAsync(string id, int limit, CancellationToken cancellationToken)
    {
        var session = await _runtime.SessionManager.LoadAsync(id, cancellationToken);
        if (session is null)
            return null;

        return new IntegrationSessionTimelineResponse
        {
            SessionId = id,
            Events = _runtime.Operations.RuntimeEvents.Query(new RuntimeEventQuery { SessionId = id, Limit = limit }),
            ProviderTurns = _runtime.ProviderUsage.RecentTurns(id, limit)
        };
    }

    public IntegrationRuntimeEventsResponse QueryRuntimeEvents(RuntimeEventQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.Operations.RuntimeEvents.Query(query)
        };

    public IntegrationApprovalsResponse GetApprovals(string? channelId, string? senderId)
        => new()
        {
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId,
            SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId,
            Items = _runtime.ToolApprovalService.ListPending(channelId, senderId)
        };

    public IntegrationApprovalHistoryResponse GetApprovalHistory(ApprovalHistoryQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.ApprovalAuditStore.Query(query)
        };

    public IntegrationProvidersResponse GetProviders(int recentTurnsLimit)
        => new()
        {
            ModelProfiles = new ModelProfilesStatusResponse
            {
                DefaultProfileId = _runtime.Operations.ModelProfiles.DefaultProfileId,
                Profiles = _runtime.Operations.ModelProfiles.ListStatuses()
            },
            Routes = _runtime.Operations.LlmExecution.SnapshotRoutes(),
            Usage = _runtime.ProviderUsage.Snapshot(),
            Policies = _runtime.Operations.ProviderPolicies.List(),
            RecentTurns = _runtime.ProviderUsage.RecentTurns(limit: recentTurnsLimit)
        };

    public IntegrationPluginsResponse GetPlugins()
        => new()
        {
            Items = _runtime.Operations.PluginHealth.ListSnapshots()
        };

    public IntegrationCompatibilityCatalogResponse GetCompatibilityCatalog(
        string? compatibilityStatus,
        string? kind,
        string? category)
        => new()
        {
            Catalog = PublicCompatibilityCatalog.GetCatalog(compatibilityStatus, kind, category)
        };

    public IntegrationOperatorAuditResponse GetOperatorAudit(OperatorAuditQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.Operations.OperatorAudit.Query(query)
        };

    public async Task<IntegrationDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        return new IntegrationDashboardResponse
        {
            Status = BuildStatusResponse(),
            Approvals = GetApprovals(channelId: null, senderId: null),
            ApprovalHistory = GetApprovalHistory(new ApprovalHistoryQuery { Limit = 12 }),
            Providers = GetProviders(recentTurnsLimit: 20),
            Plugins = GetPlugins(),
            Events = QueryRuntimeEvents(new RuntimeEventQuery { Limit = 20 }),
            Operator = await GetOperatorDashboardAsync(cancellationToken)
        };
    }

    public async Task<OperatorDashboardSnapshot> GetOperatorDashboardAsync(CancellationToken cancellationToken)
    {
        var metadataById = _runtime.Operations.SessionMetadata.GetAll();
        var persistedSessions = await SessionAdminPersistedListing.ListAllMatchingSummariesAsync(
            _sessionAdminStore,
            new SessionListQuery(),
            metadataById,
            cancellationToken);
        var activeSessions = (await _runtime.SessionManager.ListActiveAsync(cancellationToken))
            .Select(static session => new SessionSummary
            {
                Id = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                CreatedAt = session.CreatedAt,
                LastActiveAt = session.LastActiveAt,
                State = session.State,
                HistoryTurns = session.History.Count,
                TotalInputTokens = session.TotalInputTokens,
                TotalOutputTokens = session.TotalOutputTokens,
                IsActive = true
            })
            .ToArray();

        var sessionMap = new Dictionary<string, SessionSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in persistedSessions)
            sessionMap[item.Id] = item;
        foreach (var item in activeSessions)
            sessionMap[item.Id] = item;

        var allSessions = sessionMap.Values.ToArray();
        var now = DateTimeOffset.UtcNow;
        var approvalHistory = _runtime.ApprovalAuditStore.Query(new ApprovalHistoryQuery { Limit = 200 });
        var pendingApprovals = _runtime.ToolApprovalService.ListPending();
        var learningProposals = await _learningService.ListAsync(status: null, kind: null, cancellationToken);
        var automations = await _automationService.ListAsync(cancellationToken);
        var runningAutomationIds = new HashSet<string>(_automationService.ListRunningIds(), StringComparer.OrdinalIgnoreCase);
        var automationItems = new List<DashboardAutomationItem>(automations.Count);
        var enabledAutomations = 0;
        var draftAutomations = 0;
        var neverRunAutomations = 0;
        var queuedOrRunningAutomations = 0;
        var failingAutomations = 0;

        foreach (var automation in automations.OrderByDescending(static item => item.UpdatedAtUtc).Take(10))
        {
            var runState = await _automationService.GetRunStateAsync(automation.Id, cancellationToken);
            var outcome = runState?.Outcome ?? "never";
            var lifecycle = runState?.LifecycleState ?? AutomationLifecycleStates.Never;
            var isQueuedOrRunning = runningAutomationIds.Contains(automation.Id) ||
                string.Equals(lifecycle, AutomationLifecycleStates.Queued, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lifecycle, AutomationLifecycleStates.Running, StringComparison.OrdinalIgnoreCase);
            var isFailing = string.Equals(runState?.HealthState, AutomationHealthStates.Degraded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runState?.HealthState, AutomationHealthStates.Quarantined, StringComparison.OrdinalIgnoreCase);

            if (automation.Enabled)
                enabledAutomations++;
            if (automation.IsDraft)
                draftAutomations++;
            if (string.Equals(outcome, "never", StringComparison.OrdinalIgnoreCase))
                neverRunAutomations++;
            if (isQueuedOrRunning)
                queuedOrRunningAutomations++;
            if (isFailing)
                failingAutomations++;

            automationItems.Add(new DashboardAutomationItem
            {
                Id = automation.Id,
                Name = automation.Name,
                Enabled = automation.Enabled,
                IsDraft = automation.IsDraft,
                DeliveryChannelId = automation.DeliveryChannelId,
                TemplateKey = automation.TemplateKey,
                Outcome = outcome,
                LastRunAtUtc = runState?.LastRunAtUtc
            });
        }

        foreach (var automation in automations.Skip(automationItems.Count))
        {
            var runState = await _automationService.GetRunStateAsync(automation.Id, cancellationToken);
            var outcome = runState?.Outcome ?? "never";
            var lifecycle = runState?.LifecycleState ?? AutomationLifecycleStates.Never;
            if (automation.Enabled)
                enabledAutomations++;
            if (automation.IsDraft)
                draftAutomations++;
            if (string.Equals(outcome, "never", StringComparison.OrdinalIgnoreCase))
                neverRunAutomations++;
            if (runningAutomationIds.Contains(automation.Id) ||
                string.Equals(lifecycle, AutomationLifecycleStates.Queued, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lifecycle, AutomationLifecycleStates.Running, StringComparison.OrdinalIgnoreCase))
            {
                queuedOrRunningAutomations++;
            }
            if (string.Equals(runState?.HealthState, AutomationHealthStates.Degraded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runState?.HealthState, AutomationHealthStates.Quarantined, StringComparison.OrdinalIgnoreCase))
            {
                failingAutomations++;
            }
        }

        var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(_startup.Config, _startup.IsNonLoopbackBind));
        var pluginHealth = _runtime.Operations.PluginHealth.ListSnapshots();
        var memoryEntries = _memoryCatalog is null
            ? []
            : await _memoryCatalog.ListNotesAsync(prefix: "", limit: 500, cancellationToken);
        var memoryNotes = memoryEntries
            .Select(static entry => MapMemoryNoteItem(entry.Key, entry.PreviewContent, entry.UpdatedAt))
            .ToArray();
        var memoryEvents = _runtime.Operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = 20, Component = "memory" });

        return new OperatorDashboardSnapshot
        {
            Sessions = new DashboardSessionSummary
            {
                Active = activeSessions.Length,
                Persisted = persistedSessions.Count,
                UniqueTotal = allSessions.Length,
                Last24Hours = allSessions.Count(item => item.LastActiveAt >= now.AddHours(-24)),
                Last7Days = allSessions.Count(item => item.LastActiveAt >= now.AddDays(-7)),
                Starred = metadataById.Values.Count(item => item.Starred && sessionMap.ContainsKey(item.SessionId)),
                Channels = BuildMetrics(
                    allSessions.GroupBy(static item => item.ChannelId, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count()))),
                States = BuildMetrics(
                    allSessions.GroupBy(static item => item.State.ToString(), StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count())))
            },
            Approvals = new DashboardApprovalSummary
            {
                Pending = pendingApprovals.Count,
                DecisionsLast24Hours = approvalHistory.Count(static item =>
                    string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase) &&
                    item.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-24)),
                ApprovedLast24Hours = approvalHistory.Count(static item =>
                    string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase) &&
                    item.Approved == true &&
                    item.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-24)),
                RejectedLast24Hours = approvalHistory.Count(static item =>
                    string.Equals(item.EventType, "decision", StringComparison.OrdinalIgnoreCase) &&
                    item.Approved == false &&
                    item.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-24)),
                PendingByTool = BuildMetrics(
                    pendingApprovals.GroupBy(static item => item.ToolName, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count()))),
                PendingByChannel = BuildMetrics(
                    pendingApprovals.GroupBy(static item => item.ChannelId, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count())))
            },
            Memory = new DashboardMemorySummary
            {
                ListedNotes = memoryNotes.Length,
                CatalogTruncated = memoryEntries.Count >= 500,
                ByClass = BuildMetrics(
                    memoryNotes.GroupBy(static item => item.MemoryClass, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count()))),
                RecentNotes = memoryNotes.Take(8).ToArray(),
                RecentActivity = memoryEvents
            },
            Automations = new DashboardAutomationSummary
            {
                Total = automations.Count,
                Enabled = enabledAutomations,
                Drafts = draftAutomations,
                NeverRun = neverRunAutomations,
                QueuedOrRunning = queuedOrRunningAutomations,
                Failing = failingAutomations,
                Items = automationItems,
                Templates = _automationService.GetTemplates()
            },
            Learning = new DashboardLearningSummary
            {
                Pending = learningProposals.Count(item => string.Equals(item.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase)),
                Approved = learningProposals.Count(item => string.Equals(item.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase)),
                Rejected = learningProposals.Count(item => string.Equals(item.Status, LearningProposalStatus.Rejected, StringComparison.OrdinalIgnoreCase)),
                RolledBack = learningProposals.Count(item => string.Equals(item.Status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase)),
                PendingByKind = BuildMetrics(
                    learningProposals
                        .Where(static item => string.Equals(item.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(static item => item.Kind, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count()))),
                Recent = learningProposals
                    .OrderByDescending(static item => item.UpdatedAtUtc)
                    .Take(8)
                    .ToArray()
            },
            Delegation = new DashboardDelegationSummary
            {
                Enabled = _startup.Config.Delegation.Enabled,
                MaxDepth = _startup.Config.Delegation.MaxDepth,
                Last24Hours = allSessions.Count(static item => string.Equals(item.ChannelId, "delegation", StringComparison.OrdinalIgnoreCase) &&
                    item.LastActiveAt >= DateTimeOffset.UtcNow.AddHours(-24)),
                Profiles = _startup.Config.Delegation.Profiles.Keys
                    .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            Channels = new DashboardChannelSummary
            {
                Ready = readiness.Count(static item => string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase)),
                Degraded = readiness.Count(static item => string.Equals(item.Status, "degraded", StringComparison.OrdinalIgnoreCase)),
                Misconfigured = readiness.Count(static item => string.Equals(item.Status, "misconfigured", StringComparison.OrdinalIgnoreCase)),
                Items = readiness
            },
            Plugins = new DashboardPluginSummary
            {
                Total = pluginHealth.Count,
                Loaded = pluginHealth.Count(static item => item.Loaded),
                Disabled = pluginHealth.Count(static item => item.Disabled),
                Quarantined = pluginHealth.Count(static item => item.Quarantined),
                NeedsReview = pluginHealth.Count(static item => !item.Reviewed),
                WarningCount = pluginHealth.Sum(static item => item.WarningCount),
                ErrorCount = pluginHealth.Sum(static item => item.ErrorCount),
                TrustLevels = BuildMetrics(
                    pluginHealth.GroupBy(static item => item.TrustLevel, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count()))),
                CompatibilityStatuses = BuildMetrics(
                    pluginHealth.GroupBy(static item => item.CompatibilityStatus, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => (Key: group.Key, Label: group.Key, Count: group.Count())))
            }
        };
    }

    public async Task<IntegrationSessionSearchResponse> SearchSessionsAsync(SessionSearchQuery query, CancellationToken cancellationToken)
        => new()
        {
            Result = await _sessionSearchStore.SearchSessionsAsync(query, cancellationToken)
        };

    public async Task<IntegrationProfilesResponse> ListProfilesAsync(CancellationToken cancellationToken)
        => new()
        {
            Items = await _profileStore.ListProfilesAsync(cancellationToken)
        };

    public async Task<IntegrationTextToSpeechResponse> SynthesizeSpeechAsync(IntegrationTextToSpeechRequest request, CancellationToken cancellationToken)
    {
        if (_textToSpeechService is null)
            throw new InvalidOperationException("Text-to-speech is not available in this runtime.");

        var result = await _textToSpeechService.SynthesizeSpeechAsync(
            new TextToSpeechRequest
            {
                Text = request.Text,
                Provider = request.Provider,
                VoiceId = request.VoiceId,
                VoiceName = request.VoiceName,
                Model = request.Model
            },
            cancellationToken);

        return new IntegrationTextToSpeechResponse
        {
            Provider = result.Provider,
            AssetId = result.Asset.Id,
            MediaType = result.Asset.MediaType,
            DataUrl = result.DataUrl,
            Marker = result.Marker
        };
    }

    public async Task<IntegrationProfileResponse> GetProfileAsync(string actorId, CancellationToken cancellationToken)
        => new()
        {
            Profile = await _profileStore.GetProfileAsync(actorId, cancellationToken)
        };

    public async Task<IntegrationProfileResponse> SaveProfileAsync(string actorId, UserProfile profile, CancellationToken cancellationToken)
    {
        var normalized = NormalizeProfile(actorId, profile);
        await _profileStore.SaveProfileAsync(normalized, cancellationToken);
        AppendRuntimeEvent(
            component: "profiles",
            action: "updated",
            summary: $"Profile '{normalized.ActorId}' updated.",
            channelId: normalized.ChannelId,
            senderId: normalized.SenderId);

        return new IntegrationProfileResponse { Profile = normalized };
    }

    public async Task<IntegrationAutomationsResponse> ListAutomationsAsync(CancellationToken cancellationToken)
        => new()
        {
            Items = await _automationService.ListAsync(cancellationToken)
        };

    public IntegrationToolPresetsResponse ListToolPresets()
        => new()
        {
            Items = _toolPresetResolver?.ListPresets(_runtime.RegisteredToolNames) ?? []
        };

    public async Task<IntegrationAutomationDetailResponse> GetAutomationAsync(string automationId, CancellationToken cancellationToken)
        => new()
        {
            Automation = await _automationService.GetAsync(automationId, cancellationToken),
            RunState = await _automationService.GetRunStateAsync(automationId, cancellationToken)
        };

    public async Task<IntegrationAutomationRunsResponse> GetAutomationRunsAsync(string automationId, CancellationToken cancellationToken)
        => new()
        {
            AutomationId = automationId,
            RunState = await _automationService.GetRunStateAsync(automationId, cancellationToken),
            Items = await _automationService.ListRunRecordsAsync(automationId, limit: 50, cancellationToken)
        };

    public async Task<IntegrationAutomationRunDetailResponse> GetAutomationRunAsync(string automationId, string runId, CancellationToken cancellationToken)
        => new()
        {
            AutomationId = automationId,
            Automation = await _automationService.GetAsync(automationId, cancellationToken),
            RunState = await _automationService.GetRunStateAsync(automationId, cancellationToken),
            Run = await _automationService.GetRunRecordAsync(automationId, runId, cancellationToken)
        };

    public AutomationTemplateListResponse ListAutomationTemplates()
        => new()
        {
            Items = _automationService.GetTemplates()
        };

    public async Task<MutationResponse> RunAutomationAsync(string automationId, bool dryRun, CancellationToken cancellationToken)
    {
        var automation = await _automationService.GetAsync(automationId, cancellationToken);
        if (automation is null)
        {
            return new MutationResponse
            {
                Success = false,
                Error = "Automation not found."
            };
        }

        if (dryRun)
        {
            return new MutationResponse
            {
                Success = true,
                Message = "Dry run validated."
            };
        }

        var result = await _automationService.RunNowAsync(automationId, _runtime.Pipeline, cancellationToken);
        if (string.Equals(result.Status, "queued", StringComparison.Ordinal))
        {
            AppendRuntimeEvent(
                component: "automations",
                action: "queued",
                summary: string.IsNullOrWhiteSpace(result.RunId)
                    ? $"Automation '{automationId}' queued for execution."
                    : $"Automation '{automationId}' queued for execution as run '{result.RunId}'.",
                sessionId: automation.SessionId,
                channelId: automation.DeliveryChannelId,
                senderId: automation.DeliveryRecipientId);
        }

        return result.Status switch
        {
            "queued" => new MutationResponse { Success = true, Message = "Automation queued." },
            "already_running" => new MutationResponse { Success = false, Error = "Automation is already running." },
            "quarantined" => new MutationResponse { Success = false, Error = "Automation is quarantined and cannot be scheduled automatically." },
            _ => new MutationResponse { Success = false, Error = "Automation could not be queued." }
        };
    }

    public async Task<MutationResponse> ReplayAutomationRunAsync(string automationId, string runId, CancellationToken cancellationToken)
    {
        var automation = await _automationService.GetAsync(automationId, cancellationToken);
        if (automation is null)
            return new MutationResponse { Success = false, Error = "Automation not found." };

        var result = await _automationService.ReplayAsync(automationId, runId, _runtime.Pipeline, cancellationToken);
        return result.Status switch
        {
            "queued" => new MutationResponse { Success = true, Message = "Automation replay queued." },
            "already_running" => new MutationResponse { Success = false, Error = "Automation is already running." },
            _ => new MutationResponse { Success = false, Error = "Automation replay could not be queued." }
        };
    }

    public async Task<MutationResponse> ClearAutomationQuarantineAsync(string automationId, CancellationToken cancellationToken)
    {
        var automation = await _automationService.GetAsync(automationId, cancellationToken);
        if (automation is null)
            return new MutationResponse { Success = false, Error = "Automation not found." };

        await _automationService.ClearQuarantineAsync(automationId, cancellationToken);
        return new MutationResponse
        {
            Success = true,
            Message = "Automation quarantine cleared."
        };
    }

    public async Task<MutationResponse> DeleteAutomationAsync(string automationId, CancellationToken cancellationToken)
    {
        var existing = await _automationService.GetAsync(automationId, cancellationToken);
        if (existing is null)
        {
            return new MutationResponse
            {
                Success = false,
                Error = "Automation not found."
            };
        }

        await _automationService.DeleteAsync(automationId, cancellationToken);
        AppendRuntimeEvent(
            component: "automations",
            action: "deleted",
            summary: $"Automation '{automationId}' deleted.",
            sessionId: existing.SessionId,
            channelId: existing.DeliveryChannelId,
            senderId: existing.DeliveryRecipientId);

        return new MutationResponse
        {
            Success = true,
            Message = "Automation deleted."
        };
    }

    public async Task<LearningProposalListResponse> ListLearningProposalsAsync(string? status, string? kind, CancellationToken cancellationToken)
        => new()
        {
            Items = await _learningService.ListAsync(status, kind, cancellationToken)
        };

    public async Task<LearningProposalDetailResponse?> GetLearningProposalDetailAsync(string proposalId, CancellationToken cancellationToken)
        => await _learningService.GetDetailAsync(proposalId, cancellationToken);

    public async Task<LearningProposal?> ApproveLearningProposalAsync(string proposalId, CancellationToken cancellationToken)
    {
        var approved = await _learningService.ApproveAsync(proposalId, _runtime.AgentRuntime, cancellationToken);
        if (approved is not null)
        {
            AppendRuntimeEvent(
                component: "learning",
                action: "approved",
                summary: $"Learning proposal '{proposalId}' approved.",
                channelId: approved.ProfileUpdate?.ChannelId ?? approved.AutomationDraft?.DeliveryChannelId,
                senderId: approved.ProfileUpdate?.SenderId ?? approved.AutomationDraft?.DeliveryRecipientId);
        }

        return approved;
    }

    public async Task<LearningProposal?> RejectLearningProposalAsync(string proposalId, string? reason, CancellationToken cancellationToken)
    {
        var rejected = await _learningService.RejectAsync(proposalId, reason, cancellationToken);
        if (rejected is not null)
        {
            AppendRuntimeEvent(
                component: "learning",
                action: "rejected",
                summary: $"Learning proposal '{proposalId}' rejected.",
                channelId: rejected.ProfileUpdate?.ChannelId ?? rejected.AutomationDraft?.DeliveryChannelId,
                senderId: rejected.ProfileUpdate?.SenderId ?? rejected.AutomationDraft?.DeliveryRecipientId);
        }

        return rejected;
    }

    public async Task<IntegrationMessageResponse> QueueMessageAsync(IntegrationMessageRequest request, CancellationToken cancellationToken)
    {
        var effectiveChannelId = string.IsNullOrWhiteSpace(request.ChannelId) ? "integration-api" : request.ChannelId.Trim();
        var effectiveSenderId = string.IsNullOrWhiteSpace(request.SenderId) ? "http-client" : request.SenderId.Trim();
        var effectiveSessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"{effectiveChannelId}:{effectiveSenderId}"
            : request.SessionId.Trim();

        await _runtime.RecentSenders.RecordAsync(effectiveChannelId, effectiveSenderId, senderName: null, cancellationToken);

        var message = new InboundMessage
        {
            ChannelId = effectiveChannelId,
            SenderId = effectiveSenderId,
            SessionId = effectiveSessionId,
            Type = "user_message",
            Text = request.Text,
            MessageId = request.MessageId,
            ReplyToMessageId = request.ReplyToMessageId
        };

        if (!_runtime.Pipeline.InboundWriter.TryWrite(message))
            await _runtime.Pipeline.InboundWriter.WriteAsync(message, cancellationToken);

        return new IntegrationMessageResponse
        {
            Accepted = true,
            ChannelId = effectiveChannelId,
            SenderId = effectiveSenderId,
            SessionId = effectiveSessionId,
            MessageId = request.MessageId
        };
    }

    private static IReadOnlyList<DashboardNamedMetric> BuildMetrics(IEnumerable<(string Key, string Label, int Count)> source, int limit = 6)
        => source
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(static item => new DashboardNamedMetric
            {
                Key = item.Key,
                Label = item.Label,
                Count = item.Count
            })
            .ToArray();

    private static IReadOnlyList<ChannelReadinessDto> MapChannelReadiness(IReadOnlyList<ChannelReadinessState> states)
        => states.Select(static state => new ChannelReadinessDto
        {
            ChannelId = state.ChannelId,
            DisplayName = state.DisplayName,
            Mode = state.Mode,
            Status = state.Status,
            Enabled = state.Enabled,
            Ready = state.Ready,
            MissingRequirements = state.MissingRequirements,
            Warnings = state.Warnings,
            FixGuidance = state.FixGuidance.Select(static guidance => new ChannelFixGuidanceDto
            {
                Label = guidance.Label,
                Href = guidance.Href,
                Reference = guidance.Reference
            }).ToArray()
        }).ToArray();

    private static MemoryNoteItem MapMemoryNoteItem(string key, string content, DateTimeOffset updatedAt)
    {
        var classification = ClassifyMemoryNoteKey(key);
        var preview = content.Length <= 512 ? content : content[..512] + "…";
        return new MemoryNoteItem
        {
            Key = key,
            DisplayKey = classification.DisplayKey,
            MemoryClass = classification.MemoryClass,
            ProjectId = classification.ProjectId,
            Preview = preview,
            UpdatedAtUtc = updatedAt
        };
    }

    private static (string MemoryClass, string? ProjectId, string DisplayKey) ClassifyMemoryNoteKey(string key)
    {
        if (key.StartsWith("project:", StringComparison.Ordinal))
        {
            var segments = key.Split(':', 3, StringSplitOptions.None);
            if (segments.Length == 3)
                return (MemoryNoteClass.ProjectFact, segments[1], segments[2]);
        }

        if (key.StartsWith("runbook:", StringComparison.Ordinal))
            return (MemoryNoteClass.OperationalRunbook, null, key["runbook:".Length..]);

        if (key.StartsWith("skill:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedSkill, null, key["skill:".Length..]);

        if (key.StartsWith("automation:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedAutomation, null, key["automation:".Length..]);

        return (MemoryNoteClass.General, null, key);
    }

    private void AppendRuntimeEvent(
        string component,
        string action,
        string summary,
        string? sessionId = null,
        string? channelId = null,
        string? senderId = null)
    {
        _runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = sessionId,
            ChannelId = channelId,
            SenderId = senderId,
            Component = component,
            Action = action,
            Severity = "info",
            Summary = summary
        });
    }

    private static UserProfile NormalizeProfile(string actorId, UserProfile profile)
    {
        var normalizedActorId = string.IsNullOrWhiteSpace(actorId) ? profile.ActorId : actorId.Trim();
        var parts = normalizedActorId.Split(':', 2, StringSplitOptions.TrimEntries);
        var channelId = !string.IsNullOrWhiteSpace(profile.ChannelId)
            ? profile.ChannelId.Trim()
            : (parts.Length > 0 ? parts[0] : "unknown");
        var senderId = !string.IsNullOrWhiteSpace(profile.SenderId)
            ? profile.SenderId.Trim()
            : (parts.Length > 1 ? parts[1] : normalizedActorId);

        return new UserProfile
        {
            ActorId = normalizedActorId,
            ChannelId = channelId,
            SenderId = senderId,
            Summary = profile.Summary,
            Tone = profile.Tone,
            Facts = profile.Facts,
            Preferences = profile.Preferences,
            ActiveProjects = profile.ActiveProjects,
            RecentIntents = profile.RecentIntents,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static SessionListQuery BuildSessionQuery(
        string? search,
        string? channelId,
        string? senderId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? state,
        bool? starred,
        string? tag)
    {
        return new SessionListQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search,
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId,
            SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            State = ParseSessionState(state),
            Starred = starred,
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim()
        };
    }

    public static SessionState? ParseSessionState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<SessionState>(value, ignoreCase: true, out var state)
            ? state
            : null;
    }

}
