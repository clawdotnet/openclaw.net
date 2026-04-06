using Microsoft.Extensions.DependencyInjection;
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
    private readonly IToolPresetResolver? _toolPresetResolver;
    private readonly TextToSpeechService? _textToSpeechService;

    public static IntegrationApiFacade Create(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        IServiceProvider services)
    {
        var memoryStore = services.GetRequiredService<IMemoryStore>();
        var sessionAdminStore = (ISessionAdminStore)memoryStore;
        var sessionSearchStore = FeatureFallbackServices.ResolveSessionSearchStore(services);
        var fallbackFeatureStore = FeatureFallbackServices.CreateFallbackFeatureStore(startup);
        var profileStore = services.GetService<IUserProfileStore>() ?? fallbackFeatureStore;
        var heartbeat = services.GetService<HeartbeatService>()
            ?? throw new InvalidOperationException("HeartbeatService must be registered before mapping integration endpoints.");
        var automationService = FeatureFallbackServices.ResolveAutomationService(startup, services, heartbeat, fallbackFeatureStore);
        var learningService = FeatureFallbackServices.ResolveLearningService(startup, services, fallbackFeatureStore);
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
        var persisted = await _sessionAdminStore.ListSessionsAsync(page, pageSize, query, cancellationToken);
        var active = (await _runtime.SessionManager.ListActiveAsync(cancellationToken))
            .Where(session => MatchesSessionQuery(session, query, metadataById))
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

        var filteredPersisted = new PagedSessionList
        {
            Page = persisted.Page,
            PageSize = persisted.PageSize,
            HasMore = persisted.HasMore,
            Items = persisted.Items
                .Where(item => MatchesSummaryQuery(item, query, metadataById))
                .ToArray()
        };

        return new IntegrationSessionsResponse
        {
            Filters = query,
            Active = active,
            Persisted = filteredPersisted
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
            Events = QueryRuntimeEvents(new RuntimeEventQuery { Limit = 20 })
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
        if (result == RunNowResult.Queued)
        {
            await _automationService.SaveRunStateAsync(new AutomationRunState
            {
                AutomationId = automationId,
                Outcome = "queued",
                LastRunAtUtc = DateTimeOffset.UtcNow,
                SessionId = string.IsNullOrWhiteSpace(automation.SessionId) ? $"automation:{automation.Id}" : automation.SessionId,
                MessagePreview = automation.Prompt.Length > 180 ? automation.Prompt[..180] : automation.Prompt
            }, cancellationToken);

            AppendRuntimeEvent(
                component: "automations",
                action: "queued",
                summary: $"Automation '{automationId}' queued for execution.",
                sessionId: automation.SessionId,
                channelId: automation.DeliveryChannelId,
                senderId: automation.DeliveryRecipientId);
        }

        return result switch
        {
            RunNowResult.Queued => new MutationResponse { Success = true, Message = "Automation queued." },
            RunNowResult.AlreadyRunning => new MutationResponse { Success = false, Error = "Automation is already running." },
            _ => new MutationResponse { Success = false, Error = "Automation could not be queued." }
        };
    }

    public async Task<LearningProposalListResponse> ListLearningProposalsAsync(string? status, string? kind, CancellationToken cancellationToken)
        => new()
        {
            Items = await _learningService.ListAsync(status, kind, cancellationToken)
        };

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

    private static bool MatchesSessionQuery(
        Session session,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(session.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(session.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && session.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && session.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && session.State != state)
            return false;

        var metadata = metadataById.TryGetValue(session.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = session.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return session.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSummaryQuery(
        SessionSummary summary,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(summary.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(summary.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && summary.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && summary.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && summary.State != state)
            return false;

        var metadata = metadataById.TryGetValue(summary.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = summary.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return summary.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }
}
