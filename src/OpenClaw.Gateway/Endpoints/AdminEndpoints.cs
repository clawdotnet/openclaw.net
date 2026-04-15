using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Models;
using QRCoder;

namespace OpenClaw.Gateway.Endpoints;

internal static class AdminEndpoints
{
    private const int MaxAdminJsonBodyBytes = 256 * 1024;

    public static void MapOpenClawAdminEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
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
        var sessionAdminStore = app.Services.GetRequiredService<ISessionAdminStore>();
        var operations = runtime.Operations;
        var modelEvaluationRunner = app.Services.GetService<ModelEvaluationRunner>()
            ?? new ModelEvaluationRunner(
                operations.ModelProfiles as ConfiguredModelProfileRegistry
                    ?? new ConfiguredModelProfileRegistry(startup.Config, NullLogger<ConfiguredModelProfileRegistry>.Instance),
                startup.Config,
                NullLogger<ModelEvaluationRunner>.Instance);

        app.MapGet("/auth/session", (HttpContext ctx) =>
        {
            if (!startup.IsNonLoopbackBind)
            {
                return Results.Json(new AuthSessionResponse
                {
                    AuthMode = "loopback-open",
                    Persistent = false
                }, CoreJsonContext.Default.AuthSessionResponse);
            }

            if (browserSessions.TryAuthorize(ctx, requireCsrf: false, out var browserTicket))
            {
                return Results.Json(new AuthSessionResponse
                {
                    AuthMode = "browser-session",
                    CsrfToken = browserTicket!.CsrfToken,
                    ExpiresAtUtc = browserTicket.ExpiresAtUtc,
                    Persistent = browserTicket.Persistent
                }, CoreJsonContext.Default.AuthSessionResponse);
            }

            var token = GatewaySecurity.GetToken(ctx, startup.Config.Security.AllowQueryStringToken);
            if (!GatewaySecurity.IsTokenValid(token, startup.Config.AuthToken!))
                return Results.Unauthorized();

            return Results.Json(new AuthSessionResponse
            {
                AuthMode = "bearer",
                Persistent = false
            }, CoreJsonContext.Default.AuthSessionResponse);
        });

        app.MapPost("/auth/session", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized || (startup.IsNonLoopbackBind && !string.Equals(auth.AuthMode, "bearer", StringComparison.Ordinal)))
                return Results.Unauthorized();

            AuthSessionRequest? request = null;
            if (ctx.Request.ContentLength is > 0)
            {
                var authRequest = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AuthSessionRequest);
                if (authRequest.Failure is not null)
                    return authRequest.Failure;

                request = authRequest.Value;
            }

            var ticket = browserSessions.Create(request?.Remember ?? false);
            browserSessions.WriteCookie(ctx, ticket);

            return Results.Json(new AuthSessionResponse
            {
                AuthMode = "browser-session",
                CsrfToken = ticket.CsrfToken,
                ExpiresAtUtc = ticket.ExpiresAtUtc,
                Persistent = ticket.Persistent
            }, CoreJsonContext.Default.AuthSessionResponse);
        });

        app.MapDelete("/auth/session", (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            browserSessions.Revoke(ctx);
            browserSessions.ClearCookie(ctx);
            return Results.Ok(new OperationStatusResponse
            {
                Success = true,
                Message = "Browser session cleared."
            });
        });

        app.MapGet("/admin/summary", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.summary");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var persistence = adminSettings.GetPersistence();
            var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind));
            var settingsWarnings = GetChannelWarnings(readiness);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);
            var pluginHealth = operations.PluginHealth.ListSnapshots();

            var response = new AdminSummaryResponse
            {
                Auth = new AdminSummaryAuth
                {
                    Mode = auth.AuthMode,
                    BrowserSessionActive = auth.UsedBrowserSession
                },
                Runtime = new AdminSummaryRuntime
                {
                    RequestedMode = startup.RuntimeState.RequestedMode,
                    EffectiveMode = startup.RuntimeState.EffectiveModeName,
                    Orchestrator = runtime.OrchestratorId,
                    DynamicCodeSupported = startup.RuntimeState.DynamicCodeSupported,
                    ActiveSessions = runtime.SessionManager.ActiveCount,
                    PendingApprovals = runtime.ToolApprovalService.ListPending().Count,
                    ActiveApprovalGrants = operations.ApprovalGrants.List().Count,
                    LiveSkillCount = runtime.AgentRuntime.LoadedSkillNames.Count,
                    LiveSkillNames = runtime.AgentRuntime.LoadedSkillNames
                },
                Settings = new AdminSummarySettings
                {
                    Persistence = persistence,
                    OverridesActive = persistence.Exists,
                    Warnings = settingsWarnings
                },
                Channels = new AdminSummaryChannels
                {
                    AllowlistSemantics = startup.Config.Channels.AllowlistSemantics,
                    Readiness = readiness
                },
                Retention = new AdminSummaryRetention
                {
                    Enabled = startup.Config.Memory.Retention.Enabled,
                    Status = retentionStatus
                },
                Plugins = new AdminSummaryPlugins
                {
                    Loaded = runtime.PluginReports.Count(static r => r.Loaded),
                    BlockedByMode = runtime.PluginReports.Count(static r => r.BlockedByRuntimeMode),
                    Reports = runtime.PluginReports,
                    Health = pluginHealth
                },
                Usage = new AdminSummaryUsage
                {
                    Providers = runtime.ProviderUsage.Snapshot(),
                    Routes = operations.LlmExecution.SnapshotRoutes(),
                    RecentTurns = runtime.ProviderUsage.RecentTurns(limit: 20)
                }
            };

            return Results.Json(response, CoreJsonContext.Default.AdminSummaryResponse);
        });

        app.MapGet("/admin/posture", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.posture");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(SecurityPostureBuilder.Build(startup, runtime), CoreJsonContext.Default.SecurityPostureResponse);
        });

        app.MapPost("/admin/approvals/simulate", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approvals.simulate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ApprovalSimulationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null || string.IsNullOrWhiteSpace(request.ToolName))
                return Results.BadRequest(new MutationResponse { Success = false, Error = "toolName is required." });

            var response = await SimulateApprovalAsync(startup, runtime, request, ctx.RequestAborted);
            return Results.Json(response, CoreJsonContext.Default.ApprovalSimulationResponse);
        });

        app.MapGet("/admin/incident/export", async (HttpContext ctx, int approvalLimit = 100, int eventLimit = 200) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.incident.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            runtime.RuntimeMetrics.SetActiveSessions(runtime.SessionManager.ActiveCount);
            runtime.RuntimeMetrics.SetCircuitBreakerState((int)runtime.AgentRuntime.CircuitBreakerState);
            var posture = SecurityPostureBuilder.Build(startup, runtime);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);

            var response = new IncidentBundleResponse
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Posture = posture,
                Metrics = runtime.RuntimeMetrics.Snapshot(),
                Retention = retentionStatus,
                ApprovalHistory = runtime.ApprovalAuditStore.Query(new ApprovalHistoryQuery { Limit = approvalLimit })
                    .Select(RedactApprovalHistory)
                    .ToArray(),
                ProviderPolicies = operations.ProviderPolicies.List(),
                ProviderRoutes = operations.LlmExecution.SnapshotRoutes(),
                ProviderUsage = runtime.ProviderUsage.Snapshot(),
                RuntimeEvents = operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = eventLimit })
                    .Select(RedactRuntimeEvent)
                    .ToArray(),
                WebhookDeadLetters = operations.WebhookDeliveries.List()
                    .Select(RedactDeadLetter)
                    .ToArray(),
                PluginHealth = operations.PluginHealth.ListSnapshots()
            };

            return Results.Json(response, CoreJsonContext.Default.IncidentBundleResponse);
        });

        app.MapGet("/admin/sessions", async (HttpContext ctx, int page = 1, int pageSize = 25, string? search = null, string? channelId = null, string? senderId = null, string? state = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, bool? starred = null, string? tag = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.sessions");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new SessionListQuery
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

            var metadataById = operations.SessionMetadata.GetAll();
            var persisted = await SessionAdminPersistedListing.ListPersistedAsync(
                sessionAdminStore,
                page,
                pageSize,
                query,
                metadataById,
                ctx.RequestAborted);
            var active = (await runtime.SessionManager.ListActiveAsync(ctx.RequestAborted))
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

            return Results.Json(new AdminSessionsResponse
            {
                Filters = query,
                Active = active,
                Persisted = persisted
            }, CoreJsonContext.Default.AdminSessionsResponse);
        });

        app.MapGet("/admin/sessions/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.session.detail");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var session = await runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null)
                return Results.NotFound(new OperationStatusResponse { Success = false, Error = "Session not found." });

            var branches = await runtime.SessionManager.ListBranchesAsync(id, ctx.RequestAborted);
            return Results.Json(new AdminSessionDetailResponse
            {
                Session = session,
                IsActive = runtime.SessionManager.IsActive(id),
                BranchCount = branches.Count,
                Metadata = operations.SessionMetadata.Get(id)
            }, CoreJsonContext.Default.AdminSessionDetailResponse);
        });

        app.MapGet("/admin/sessions/{id}/branches", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.session.branches");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var branches = await runtime.SessionManager.ListBranchesAsync(id, ctx.RequestAborted);
            return Results.Json(new SessionBranchListResponse { Items = branches }, CoreJsonContext.Default.SessionBranchListResponse);
        });

        app.MapGet("/admin/sessions/{id}/export", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.session.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var session = await runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null)
                return Results.NotFound("Session not found.");

            var transcript = BuildTranscript(session);
            return Results.Text(transcript, "text/plain; charset=utf-8");
        });

        app.MapPost("/admin/branches/{id}/restore", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.branch.restore");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var sessionId = TryExtractSessionIdFromBranchId(id);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Branch id is invalid."
                });
            }

            var session = await runtime.SessionManager.LoadAsync(sessionId, ctx.RequestAborted);
            if (session is null)
            {
                return Results.NotFound(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Session for branch was not found."
                });
            }

            var restored = await runtime.SessionManager.RestoreBranchAsync(session, id, ctx.RequestAborted);
            if (!restored)
            {
                return Results.NotFound(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Branch was not found."
                });
            }

            await runtime.SessionManager.PersistAsync(session, ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "branch_restore", id, $"Restored branch '{id}' to session '{session.Id}'.", success: true, before: null, after: new { sessionId = session.Id, branchId = id, turnCount = session.History.Count });
            return Results.Json(
                new BranchRestoreResponse { Success = true, SessionId = session.Id, BranchId = id, TurnCount = session.History.Count },
                CoreJsonContext.Default.BranchRestoreResponse);
        });

        app.MapGet("/admin/settings", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.settings");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = BuildSettingsResponse(startup, adminSettings, message: "Settings loaded.");
            return Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse);
        });

        app.MapPost("/admin/settings", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.settings.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var snapshotPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AdminSettingsSnapshot);
            if (snapshotPayload.Failure is not null)
                return snapshotPayload.Failure;

            var snapshot = snapshotPayload.Value;

            if (snapshot is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Settings payload is required."
                });
            }

            var result = adminSettings.Update(snapshot);
            RecordOperatorAudit(ctx, operations, auth, "settings_update", "gateway-settings", result.Success ? "Updated admin settings." : "Admin settings update failed.", result.Success, before: null, after: result.Snapshot);
            var response = BuildSettingsResponse(
                startup,
                adminSettings,
                result.Snapshot,
                result.Persistence,
                result.RestartRequired,
                result.RestartRequiredFields,
                result.Success ? "Settings saved." : "Settings validation failed.",
                result.Errors);

            return result.Success
                ? Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse)
                : Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapDelete("/admin/settings", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.settings.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var result = adminSettings.Reset();
            RecordOperatorAudit(ctx, operations, auth, "settings_reset", "gateway-settings", "Reset admin settings overrides.", success: true, before: null, after: result.Snapshot);
            var response = BuildSettingsResponse(
                startup,
                adminSettings,
                result.Snapshot,
                result.Persistence,
                result.RestartRequired,
                result.RestartRequiredFields,
                "Settings overrides cleared.");
            return Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse);
        });

        app.MapGet("/admin/heartbeat", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.heartbeat");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var preview = await heartbeat.BuildPreviewAsync(heartbeat.LoadConfig(), runtime, ctx.RequestAborted);
            return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapPost("/admin/heartbeat/preview", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.heartbeat.preview");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.HeartbeatConfigDto,
                ctx.RequestAborted);

            if (request is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Heartbeat config payload is required."
                });
            }

            var preview = await heartbeat.BuildPreviewAsync(request, runtime, ctx.RequestAborted);
            return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapPut("/admin/heartbeat", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.heartbeat.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.HeartbeatConfigDto,
                ctx.RequestAborted);

            if (request is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Heartbeat config payload is required."
                });
            }

            var before = heartbeat.LoadConfig();
            var preview = await heartbeat.BuildPreviewAsync(request, runtime, ctx.RequestAborted);
            var hasErrors = preview.Issues.Any(static issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
            if (hasErrors)
            {
                RecordOperatorAudit(ctx, operations, auth, "heartbeat_save", "heartbeat.default", "Heartbeat save rejected by validation.", success: false, before, after: request);
                return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var saved = heartbeat.SaveConfig(request);
            var savedPreview = await heartbeat.BuildPreviewAsync(saved, runtime, ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "heartbeat_save", "heartbeat.default", "Saved managed heartbeat configuration.", success: true, before, after: saved);
            return Results.Json(savedPreview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapGet("/admin/heartbeat/status", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.heartbeat.status");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var status = await heartbeat.BuildStatusAsync(runtime, ctx.RequestAborted);
            return Results.Json(status, CoreJsonContext.Default.HeartbeatStatusResponse);
        });

        app.MapGet("/admin/automations", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new IntegrationAutomationsResponse
                {
                    Items = await automationService.ListAsync(ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationsResponse);
        });

        app.MapPost("/admin/automations/migrate", async (HttpContext ctx, bool apply = false) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.migrate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var migrated = await automationService.MigrateLegacyAsync(apply, ctx.RequestAborted);
            return Results.Json(
                new IntegrationAutomationsResponse { Items = migrated },
                CoreJsonContext.Default.IntegrationAutomationsResponse);
        });

        app.MapPost("/admin/automations/preview", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.preview");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationDefinition);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var automation = requestPayload.Value;
            if (automation is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Automation payload is required." });

            return Results.Json(
                automationService.BuildPreview(automation),
                CoreJsonContext.Default.AutomationPreview);
        });

        app.MapGet("/admin/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations.detail");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var automation = await automationService.GetAsync(id, ctx.RequestAborted);
            if (automation is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Automation not found." });

            return Results.Json(
                new IntegrationAutomationDetailResponse
                {
                    Automation = automation,
                    RunState = await automationService.GetRunStateAsync(id, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationDetailResponse);
        });

        app.MapPut("/admin/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationDefinition);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var automation = requestPayload.Value;
            if (automation is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Automation payload is required." });

            var saved = await automationService.SaveAsync(new AutomationDefinition
            {
                Id = id,
                Name = automation.Name,
                Enabled = automation.Enabled,
                Schedule = automation.Schedule,
                Timezone = automation.Timezone,
                Prompt = automation.Prompt,
                ModelId = automation.ModelId,
                RunOnStartup = automation.RunOnStartup,
                SessionId = automation.SessionId,
                DeliveryChannelId = automation.DeliveryChannelId,
                DeliveryRecipientId = automation.DeliveryRecipientId,
                DeliverySubject = automation.DeliverySubject,
                Tags = automation.Tags,
                IsDraft = automation.IsDraft,
                Source = automation.Source,
                TemplateKey = automation.TemplateKey,
                CreatedAtUtc = automation.CreatedAtUtc,
                UpdatedAtUtc = automation.UpdatedAtUtc
            }, ctx.RequestAborted);

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                SessionId = saved.SessionId,
                ChannelId = saved.DeliveryChannelId,
                SenderId = saved.DeliveryRecipientId,
                Component = "automations",
                Action = "saved",
                Severity = "info",
                Summary = $"Automation '{saved.Id}' saved."
            });

            return Results.Json(
                new IntegrationAutomationDetailResponse
                {
                    Automation = saved,
                    RunState = await automationService.GetRunStateAsync(saved.Id, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationDetailResponse);
        });

        app.MapPost("/admin/automations/{id}/run", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.run");
            if (authResult.Failure is not null)
                return authResult.Failure;

            AutomationRunRequest? request = null;
            if (ctx.Request.ContentLength is > 0)
            {
                var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationRunRequest);
                if (requestPayload.Failure is not null)
                    return requestPayload.Failure;
                request = requestPayload.Value;
            }

            var result = await facade.RunAutomationAsync(id, request?.DryRun ?? false, ctx.RequestAborted);
            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status202Accepted : StatusCodes.Status400BadRequest);
        });

        app.MapGet("/admin/memory/notes", async (HttpContext ctx, string? prefix = null, string? memoryClass = null, string? projectId = null, int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (memoryCatalog is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Memory catalog is not available in this runtime." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            var items = await ListMemoryNotesAsync(memoryCatalog, prefix, memoryClass, projectId, limit, ctx.RequestAborted);
            return Results.Json(
                new MemoryNoteListResponse
                {
                    Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(),
                    MemoryClass = string.IsNullOrWhiteSpace(memoryClass) ? null : memoryClass.Trim(),
                    ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
                    Items = items
                },
                CoreJsonContext.Default.MemoryNoteListResponse);
        });

        app.MapGet("/admin/memory/search", async (HttpContext ctx, string query, string? memoryClass = null, string? projectId = null, int limit = 20) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (memorySearch is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Memory search is not available in this runtime." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "query is required."
                });
            }

            var normalizedClass = NormalizeMemoryClass(memoryClass);
            if (normalizedClass is null && !string.IsNullOrWhiteSpace(memoryClass))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unknown memoryClass '{memoryClass}'."
                });
            }

            var normalizedProjectId = NormalizeOptionalValue(projectId);
            var prefixFilter = BuildMemoryPrefix(normalizedClass, normalizedProjectId, prefixSuffix: null);
            var hits = await memorySearch.SearchNotesAsync(query.Trim(), prefixFilter, Math.Clamp(limit, 1, 50), ctx.RequestAborted);
            var items = hits
                .Select(static hit => MapMemoryNoteItem(hit.Key, hit.Content, hit.UpdatedAt))
                .Where(item => MatchesMemoryNoteFilter(item, normalizedClass, normalizedProjectId))
                .ToArray();

            return Results.Json(
                new MemoryNoteListResponse
                {
                    Query = query.Trim(),
                    MemoryClass = normalizedClass,
                    ProjectId = normalizedProjectId,
                    Items = items
                },
                CoreJsonContext.Default.MemoryNoteListResponse);
        });

        app.MapGet("/admin/memory/notes/{key}", async (HttpContext ctx, string key) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var keyError = InputSanitizer.CheckMemoryKey(key);
            if (keyError is not null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var content = await memoryStore.LoadNoteAsync(key, ctx.RequestAborted);
            if (content is null)
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note not found."
                });
            }

            var updatedAt = DateTimeOffset.UtcNow;
            if (memoryCatalog is not null)
                updatedAt = (await memoryCatalog.GetNoteEntryAsync(key, ctx.RequestAborted))?.UpdatedAt ?? updatedAt;

            return Results.Json(
                new MemoryNoteDetailResponse
                {
                    Note = MapMemoryNoteItem(key, content, updatedAt, includeContent: true)
                },
                CoreJsonContext.Default.MemoryNoteDetailResponse);
        });

        app.MapPost("/admin/memory/notes", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.MemoryNoteUpsertRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note payload is required."
                });
            }

            var normalizedClass = NormalizeMemoryClass(request.MemoryClass);
            if (normalizedClass is null && !string.IsNullOrWhiteSpace(request.MemoryClass))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unknown memoryClass '{request.MemoryClass}'."
                });
            }

            var resolvedKey = BuildMemoryNoteKey(request.Key, normalizedClass, request.ProjectId, out var keyError);
            if (!string.IsNullOrWhiteSpace(keyError))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var previousContent = await memoryStore.LoadNoteAsync(resolvedKey!, ctx.RequestAborted);
            await memoryStore.SaveNoteAsync(resolvedKey!, request.Content ?? string.Empty, ctx.RequestAborted);
            var savedEntry = memoryCatalog is null
                ? MapMemoryNoteItem(resolvedKey!, request.Content ?? string.Empty, DateTimeOffset.UtcNow, includeContent: true)
                : MapMemoryNoteItem(
                    resolvedKey!,
                    request.Content ?? string.Empty,
                    (await memoryCatalog.GetNoteEntryAsync(resolvedKey!, ctx.RequestAborted))?.UpdatedAt ?? DateTimeOffset.UtcNow,
                    includeContent: true);

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "note_saved",
                Severity = "info",
                Summary = $"Saved memory note '{resolvedKey}'."
            });
            RecordOperatorAudit(ctx, operations, auth, "memory_note_save", resolvedKey!, $"Saved memory note '{resolvedKey}'.", success: true, before: previousContent, after: savedEntry);

            return Results.Json(
                new MemoryNoteDetailResponse { Note = savedEntry },
                CoreJsonContext.Default.MemoryNoteDetailResponse);
        });

        app.MapDelete("/admin/memory/notes/{key}", async (HttpContext ctx, string key) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var keyError = InputSanitizer.CheckMemoryKey(key);
            if (keyError is not null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var previousContent = await memoryStore.LoadNoteAsync(key, ctx.RequestAborted);
            if (previousContent is null)
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note not found."
                });
            }

            await memoryStore.DeleteNoteAsync(key, ctx.RequestAborted);
            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "note_deleted",
                Severity = "warning",
                Summary = $"Deleted memory note '{key}'."
            });
            RecordOperatorAudit(ctx, operations, auth, "memory_note_delete", key, $"Deleted memory note '{key}'.", success: true, before: previousContent, after: null);

            return Results.Json(
                new MutationResponse
                {
                    Success = true,
                    Message = "Memory note deleted."
                },
                CoreJsonContext.Default.MutationResponse);
        });

        app.MapGet("/admin/memory/export", async (HttpContext ctx, string? actorId = null, string? projectId = null, bool includeProfiles = true, bool includeProposals = true, bool includeAutomations = true, bool includeNotes = true) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedActorId = NormalizeOptionalValue(actorId);
            var normalizedProjectId = NormalizeOptionalValue(projectId);

            IReadOnlyList<UserProfile> profiles = [];
            if (includeProfiles)
            {
                profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    profiles = profiles
                        .Where(profile => string.Equals(profile.ActorId, normalizedActorId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
            }

            IReadOnlyList<LearningProposal> proposals = [];
            if (includeProposals)
            {
                proposals = await proposalStore.ListProposalsAsync(status: null, kind: null, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    proposals = proposals
                        .Where(item => ProposalMatchesActor(item, normalizedActorId))
                        .ToArray();
                }
            }

            IReadOnlyList<AutomationDefinition> automations = [];
            if (includeAutomations)
            {
                automations = await automationService.ListAsync(ctx.RequestAborted);
            }

            IReadOnlyList<MemoryNoteItem> notes = [];
            if (includeNotes && memoryCatalog is not null)
            {
                var prefixFilter = BuildMemoryPrefix(memoryClass: null, normalizedProjectId, prefixSuffix: null);
                var entries = await memoryCatalog.ListNotesAsync(prefixFilter, 500, ctx.RequestAborted);
                notes = await MaterializeMemoryNoteItemsAsync(memoryStore, entries, includeContent: true, ctx.RequestAborted);
            }

            return Results.Json(
                new MemoryConsoleExportBundle
                {
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    Notes = notes,
                    Profiles = profiles,
                    Proposals = proposals,
                    Automations = automations
                },
                CoreJsonContext.Default.MemoryConsoleExportBundle);
        });

        app.MapPost("/admin/memory/import", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.MemoryConsoleExportBundle);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var bundle = requestPayload.Value;
            if (bundle is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Memory import payload is required."
                });
            }

            var notesImported = 0;
            var profilesImported = 0;
            var proposalsImported = 0;
            var automationsImported = 0;

            var invalidNoteKeys = bundle.Notes
                .Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null)
                .Select(static note => new { note.Key, Error = InputSanitizer.CheckMemoryKey(note.Key) })
                .Where(static item => item.Error is not null)
                .ToArray();
            if (invalidNoteKeys.Length > 0)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Memory import contains invalid note keys: {string.Join(", ", invalidNoteKeys.Select(static item => item.Key))}."
                });
            }

            foreach (var note in bundle.Notes.Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null))
            {
                await memoryStore.SaveNoteAsync(note.Key, note.Content!, ctx.RequestAborted);
                notesImported++;
            }

            foreach (var profile in bundle.Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.ActorId)))
            {
                await profileStore.SaveProfileAsync(NormalizeProfile(profile), ctx.RequestAborted);
                profilesImported++;
            }

            foreach (var proposal in bundle.Proposals.Where(static proposal => !string.IsNullOrWhiteSpace(proposal.Id)))
            {
                await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                proposalsImported++;
            }

            foreach (var automation in bundle.Automations.Where(static automation => !string.IsNullOrWhiteSpace(automation.Id)))
            {
                await automationService.SaveAsync(automation, ctx.RequestAborted);
                automationsImported++;
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "imported",
                Severity = "info",
                Summary = $"Imported {notesImported} memory notes, {profilesImported} profiles, {proposalsImported} proposals, and {automationsImported} automations."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "memory_import",
                "memory-bundle",
                $"Imported {notesImported} notes, {profilesImported} profiles, {proposalsImported} proposals, and {automationsImported} automations.",
                success: true,
                before: null,
                after: new { notesImported, profilesImported, proposalsImported, automationsImported });

            return Results.Json(
                new MemoryConsoleImportResponse
                {
                    Success = true,
                    NotesImported = notesImported,
                    ProfilesImported = profilesImported,
                    ProposalsImported = proposalsImported,
                    AutomationsImported = automationsImported,
                    Message = "Memory bundle imported."
                },
                CoreJsonContext.Default.MemoryConsoleImportResponse);
        });

        app.MapGet("/admin/profiles", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
            return Results.Json(
                new IntegrationProfilesResponse { Items = profiles },
                CoreJsonContext.Default.IntegrationProfilesResponse);
        });

        app.MapGet("/admin/profiles/export", async (HttpContext ctx, string? actorId = null, bool includeProposals = true) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedActorId = string.IsNullOrWhiteSpace(actorId) ? null : actorId.Trim();
            var profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(normalizedActorId))
            {
                profiles = profiles
                    .Where(profile => string.Equals(profile.ActorId, normalizedActorId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            IReadOnlyList<LearningProposal> proposals = [];
            if (includeProposals)
            {
                proposals = await proposalStore.ListProposalsAsync(status: null, kind: null, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    proposals = proposals
                        .Where(item => ProposalMatchesActor(item, normalizedActorId))
                        .ToArray();
                }
            }

            return Results.Json(
                new ProfileExportBundle
                {
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    Profiles = profiles,
                    Proposals = proposals
                },
                CoreJsonContext.Default.ProfileExportBundle);
        });

        app.MapPost("/admin/profiles/import", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.profiles.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ProfileExportBundle);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var bundle = requestPayload.Value;
            if (bundle is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Profile import payload is required."
                });
            }

            var importedProfiles = 0;
            var importedProposals = 0;

            foreach (var profile in bundle.Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.ActorId)))
            {
                await profileStore.SaveProfileAsync(NormalizeProfile(profile), ctx.RequestAborted);
                importedProfiles++;
            }

            foreach (var proposal in bundle.Proposals.Where(static proposal => !string.IsNullOrWhiteSpace(proposal.Id)))
            {
                await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                importedProposals++;
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "profiles",
                Action = "imported",
                Severity = "info",
                Summary = $"Imported {importedProfiles} profiles and {importedProposals} learning proposals."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "profiles_import",
                string.IsNullOrWhiteSpace(bundle.Profiles.FirstOrDefault()?.ActorId) ? "bulk" : bundle.Profiles.First().ActorId,
                $"Imported {importedProfiles} profiles and {importedProposals} learning proposals.",
                success: true,
                before: null,
                after: new { importedProfiles, importedProposals });

            return Results.Json(
                new ProfileImportResponse
                {
                    Success = true,
                    ProfilesImported = importedProfiles,
                    ProposalsImported = importedProposals,
                    Message = "Profiles imported."
                },
                CoreJsonContext.Default.ProfileImportResponse);
        });

        app.MapGet("/admin/profiles/{actorId}", async (HttpContext ctx, string actorId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var profile = await profileStore.GetProfileAsync(actorId, ctx.RequestAborted);
            if (profile is null)
            {
                return Results.NotFound(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Profile not found."
                });
            }

            return Results.Json(
                new IntegrationProfileResponse { Profile = profile },
                CoreJsonContext.Default.IntegrationProfileResponse);
        });

        app.MapGet("/admin/learning/proposals", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.learning");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var status = ctx.Request.Query.TryGetValue("status", out var statusValues) ? statusValues.ToString() : null;
            var kind = ctx.Request.Query.TryGetValue("kind", out var kindValues) ? kindValues.ToString() : null;
            var items = await learningService.ListAsync(status, kind, ctx.RequestAborted);
            return Results.Json(
                new LearningProposalListResponse { Items = items },
                CoreJsonContext.Default.LearningProposalListResponse);
        });

        app.MapGet("/admin/learning/proposals/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.learning");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var detail = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            if (detail is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            return Results.Json(detail, CoreJsonContext.Default.LearningProposalDetailResponse);
        });

        app.MapPost("/admin/learning/proposals/{id}/approve", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);

            var approved = await learningService.ApproveAsync(id, runtime.AgentRuntime, ctx.RequestAborted);
            if (approved is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = approved.ProfileUpdate?.ChannelId ?? approved.AutomationDraft?.DeliveryChannelId,
                SenderId = approved.ProfileUpdate?.SenderId ?? approved.AutomationDraft?.DeliveryRecipientId,
                Component = "learning",
                Action = "approved",
                Severity = "info",
                Summary = $"Learning proposal '{approved.Id}' approved."
            });
            RecordOperatorAudit(ctx, operations, auth, "learning_approve", approved.Id, $"Approved learning proposal '{approved.Id}'.", success: true, before, after: approved);

            return Results.Json(approved, CoreJsonContext.Default.LearningProposal);
        });

        app.MapPost("/admin/learning/proposals/{id}/reject", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.LearningProposalReviewRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            var rejected = await learningService.RejectAsync(id, requestPayload.Value?.Reason, ctx.RequestAborted);
            if (rejected is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = rejected.ProfileUpdate?.ChannelId ?? rejected.AutomationDraft?.DeliveryChannelId,
                SenderId = rejected.ProfileUpdate?.SenderId ?? rejected.AutomationDraft?.DeliveryRecipientId,
                Component = "learning",
                Action = "rejected",
                Severity = "info",
                Summary = $"Learning proposal '{rejected.Id}' rejected."
            });
            RecordOperatorAudit(ctx, operations, auth, "learning_reject", rejected.Id, $"Rejected learning proposal '{rejected.Id}'.", success: true, before, after: rejected);

            return Results.Json(rejected, CoreJsonContext.Default.LearningProposal);
        });

        app.MapPost("/admin/learning/proposals/{id}/rollback", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.LearningProposalReviewRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            if (before?.Proposal is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            if (!string.Equals(before.Proposal.Kind, LearningProposalKind.ProfileUpdate, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Only profile update proposals support rollback."
                });
            }

            if (!before.CanRollback)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Proposal is not in a rollbackable state."
                });
            }

            var rolledBack = await learningService.RollbackAsync(id, requestPayload.Value?.Reason, ctx.RequestAborted);
            if (rolledBack is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = rolledBack.ProfileUpdate?.ChannelId,
                SenderId = rolledBack.ProfileUpdate?.SenderId,
                Component = "learning",
                Action = "rolled_back",
                Severity = "warning",
                Summary = $"Learning proposal '{rolledBack.Id}' rolled back."
            });
            RecordOperatorAudit(ctx, operations, auth, "learning_rollback", rolledBack.Id, $"Rolled back learning proposal '{rolledBack.Id}'.", success: true, before, after: rolledBack);

            return Results.Json(rolledBack, CoreJsonContext.Default.LearningProposal);
        });

        app.MapGet("/tools/approvals", (HttpContext ctx, string? channelId, string? senderId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.approvals");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new ApprovalListResponse
                {
                    Items = runtime.ToolApprovalService.ListPending(channelId, senderId)
                },
                CoreJsonContext.Default.ApprovalListResponse);
        });

        app.MapGet("/tools/approvals/history", (HttpContext ctx, int limit = 50, string? channelId = null, string? senderId = null, string? toolName = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.approvals.history");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = runtime.ApprovalAuditStore.Query(new ApprovalHistoryQuery
            {
                Limit = limit,
                ChannelId = channelId,
                SenderId = senderId,
                ToolName = toolName
            });

            return Results.Json(new ApprovalHistoryResponse { Items = items }, CoreJsonContext.Default.ApprovalHistoryResponse);
        });

        app.MapGet("/admin/providers", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.providers");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ProviderAdminResponse
            {
                ModelProfiles = new ModelProfilesStatusResponse
                {
                    DefaultProfileId = operations.ModelProfiles.DefaultProfileId,
                    Profiles = operations.ModelProfiles.ListStatuses()
                },
                Routes = operations.LlmExecution.SnapshotRoutes(),
                Usage = runtime.ProviderUsage.Snapshot(),
                Policies = operations.ProviderPolicies.List(),
                RecentTurns = runtime.ProviderUsage.RecentTurns(limit: 50)
            }, CoreJsonContext.Default.ProviderAdminResponse);
        });

        app.MapGet("/admin/models", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.models");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new ModelProfilesStatusResponse
                {
                    DefaultProfileId = operations.ModelProfiles.DefaultProfileId,
                    Profiles = operations.ModelProfiles.ListStatuses()
                },
                CoreJsonContext.Default.ModelProfilesStatusResponse);
        });

        app.MapGet("/admin/models/doctor", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.models.doctor");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(modelEvaluationRunner.BuildDoctor(), CoreJsonContext.Default.ModelSelectionDoctorResponse);
        });

        app.MapPost("/admin/models/evaluations", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.models.evaluate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ModelEvaluationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value ?? new ModelEvaluationRequest();
            var report = await modelEvaluationRunner.RunAsync(request, ctx.RequestAborted);
            return Results.Json(report, CoreJsonContext.Default.ModelEvaluationReport);
        });

        app.MapGet("/admin/providers/policies", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.provider-policies");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new ProviderPolicyListResponse { Items = operations.ProviderPolicies.List() },
                CoreJsonContext.Default.ProviderPolicyListResponse);
        });

        app.MapPost("/admin/providers/policies", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.provider-policies.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ProviderPolicyRule);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Provider policy payload is required." });

            try
            {
                var before = operations.ProviderPolicies.List().FirstOrDefault(item => string.Equals(item.Id, request.Id, StringComparison.Ordinal));
                var saved = operations.ProviderPolicies.AddOrUpdate(request);
                RecordOperatorAudit(ctx, operations, auth, "provider_policy_upsert", saved.Id, $"Updated provider policy '{saved.Id}'.", success: true, before, saved);
                return Results.Json(saved, CoreJsonContext.Default.ProviderPolicyRule);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "provider_policy_upsert", request.Id, ex.Message, success: false, before: null, after: request);
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
        });

        app.MapDelete("/admin/providers/policies/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.provider-policies.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            try
            {
                var before = operations.ProviderPolicies.List().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                var removed = operations.ProviderPolicies.Delete(id);
                RecordOperatorAudit(ctx, operations, auth, "provider_policy_delete", id, removed ? $"Deleted provider policy '{id}'." : $"Provider policy '{id}' was not found.", removed, before, after: null);
                return removed
                    ? Results.Json(new MutationResponse { Success = true, Message = "Provider policy deleted." }, CoreJsonContext.Default.MutationResponse)
                    : Results.NotFound(new MutationResponse { Success = false, Error = "Provider policy not found." });
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "provider_policy_delete", id, ex.Message, success: false, before: null, after: null);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/admin/providers/{providerId}/circuit/reset", (HttpContext ctx, string providerId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.providers.reset");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            operations.LlmExecution.ResetProvider(providerId);
            RecordOperatorAudit(ctx, operations, auth, "provider_circuit_reset", providerId, $"Reset provider circuit for '{providerId}'.", success: true, before: null, after: null);
            return Results.Json(new MutationResponse { Success = true, Message = "Provider circuit reset." }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapGet("/admin/events", (HttpContext ctx, int limit = 100, string? sessionId = null, string? channelId = null, string? senderId = null, string? component = null, string? action = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.events");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = operations.RuntimeEvents.Query(new RuntimeEventQuery
            {
                Limit = limit,
                SessionId = sessionId,
                ChannelId = channelId,
                SenderId = senderId,
                Component = component,
                Action = action
            });

            return Results.Json(new RuntimeEventListResponse { Items = items }, CoreJsonContext.Default.RuntimeEventListResponse);
        });

        app.MapGet("/admin/sessions/{id}/timeline", async (HttpContext ctx, string id, int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.session.timeline");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var session = await runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Session not found." });

            return Results.Json(new SessionTimelineResponse
            {
                SessionId = id,
                Events = operations.RuntimeEvents.Query(new RuntimeEventQuery { SessionId = id, Limit = limit }),
                ProviderTurns = runtime.ProviderUsage.RecentTurns(id, limit)
            }, CoreJsonContext.Default.SessionTimelineResponse);
        });

        app.MapGet("/admin/sessions/{id}/diff", async (HttpContext ctx, string id, string branchId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.session.diff");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var session = await runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Session not found." });

            var diff = await runtime.SessionManager.BuildBranchDiffAsync(session, branchId, operations.SessionMetadata.Get(id), ctx.RequestAborted);
            return diff is null
                ? Results.NotFound(new MutationResponse { Success = false, Error = "Branch not found." })
                : Results.Json(diff, CoreJsonContext.Default.SessionDiffResponse);
        });

        app.MapPost("/admin/sessions/{id}/metadata", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.session.metadata");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.SessionMetadataUpdateRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Session metadata payload is required." });

            try
            {
                var before = operations.SessionMetadata.Get(id);
                var updated = operations.SessionMetadata.Set(id, request);
                RecordOperatorAudit(ctx, operations, auth, "session_metadata_update", id, $"Updated session metadata for '{id}'.", success: true, before, updated);
                return Results.Json(updated, CoreJsonContext.Default.SessionMetadataSnapshot);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "session_metadata_update", id, ex.Message, success: false, before: null, after: request);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/admin/sessions/export", async (HttpContext ctx, string? search = null, string? channelId = null, string? senderId = null, string? state = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, bool? starred = null, string? tag = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.sessions.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new SessionListQuery
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

            var metadataById = operations.SessionMetadata.GetAll();
            var summaries = await SessionAdminPersistedListing.ListAllMatchingSummariesAsync(
                sessionAdminStore,
                query,
                metadataById,
                ctx.RequestAborted);
            var items = new List<SessionExportItem>();
            foreach (var summary in summaries)
            {
                var session = await runtime.SessionManager.LoadAsync(summary.Id, ctx.RequestAborted);
                if (session is null)
                    continue;

                items.Add(new SessionExportItem
                {
                    Session = session,
                    Metadata = metadataById.TryGetValue(summary.Id, out var metadata) ? metadata : null
                });
            }

            return Results.Json(new SessionExportResponse
            {
                Filters = query,
                Items = items
            }, CoreJsonContext.Default.SessionExportResponse);
        });

        app.MapGet("/admin/plugins", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new PluginListResponse
            {
                Items = operations.PluginHealth.ListSnapshots()
            }, CoreJsonContext.Default.PluginListResponse);
        });

        app.MapGet("/admin/plugins/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var item = operations.PluginHealth.ListSnapshots().FirstOrDefault(snapshot => string.Equals(snapshot.PluginId, id, StringComparison.Ordinal));
            return item is null
                ? Results.NotFound(new MutationResponse { Success = false, Error = "Plugin not found." })
                : Results.Json(item, CoreJsonContext.Default.PluginHealthSnapshot);
        });

        app.MapPost("/admin/plugins/{id}/disable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_disable", id, $"Disabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin disabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/enable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_enable", id, $"Enabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin enabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_quarantine", id, $"Quarantined plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantined.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/clear-quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_clear_quarantine", id, $"Cleared quarantine for plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantine cleared.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapGet("/tools/approval-policies", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.approval-policies");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ApprovalGrantListResponse { Items = operations.ApprovalGrants.List() }, CoreJsonContext.Default.ApprovalGrantListResponse);
        });

        app.MapPost("/tools/approval-policies", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approval-policies.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ToolApprovalGrant);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Approval policy payload is required." });

            try
            {
                var saved = operations.ApprovalGrants.AddOrUpdate(new ToolApprovalGrant
                {
                    Id = string.IsNullOrWhiteSpace(request.Id) ? $"apg_{Guid.NewGuid():N}"[..20] : request.Id,
                    Scope = request.Scope,
                    ChannelId = request.ChannelId,
                    SenderId = request.SenderId,
                    SessionId = request.SessionId,
                    ToolName = request.ToolName,
                    CreatedAtUtc = request.CreatedAtUtc == default ? DateTimeOffset.UtcNow : request.CreatedAtUtc,
                    ExpiresAtUtc = request.ExpiresAtUtc,
                    GrantedBy = string.IsNullOrWhiteSpace(request.GrantedBy) ? EndpointHelpers.GetOperatorActorId(ctx, auth) : request.GrantedBy,
                    GrantSource = string.IsNullOrWhiteSpace(request.GrantSource) ? auth.AuthMode : request.GrantSource,
                    RemainingUses = Math.Max(1, request.RemainingUses)
                });

                RecordOperatorAudit(ctx, operations, auth, "approval_grant_upsert", saved.Id, $"Updated tool approval grant '{saved.Id}'.", success: true, before: null, after: saved);
                return Results.Json(saved, CoreJsonContext.Default.ToolApprovalGrant);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "approval_grant_upsert", request.Id ?? "new", ex.Message, success: false, before: null, after: request);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapDelete("/tools/approval-policies/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approval-policies.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            try
            {
                var removed = operations.ApprovalGrants.Delete(id);
                RecordOperatorAudit(ctx, operations, auth, "approval_grant_delete", id, removed ? $"Deleted tool approval grant '{id}'." : $"Tool approval grant '{id}' was not found.", removed, before: null, after: null);
                return removed
                    ? Results.Json(new MutationResponse { Success = true, Message = "Approval grant deleted." }, CoreJsonContext.Default.MutationResponse)
                    : Results.NotFound(new MutationResponse { Success = false, Error = "Approval grant not found." });
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "approval_grant_delete", id, ex.Message, success: false, before: null, after: null);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/admin/audit", (HttpContext ctx, int limit = 100, string? actorId = null, string? actionType = null, string? targetId = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.audit");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new OperatorAuditListResponse
            {
                Items = operations.OperatorAudit.Query(new OperatorAuditQuery
                {
                    Limit = limit,
                    ActorId = actorId,
                    ActionType = actionType,
                    TargetId = targetId
                })
            }, CoreJsonContext.Default.OperatorAuditListResponse);
        });

        app.MapGet("/admin/webhooks/dead-letter", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.webhooks");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new WebhookDeadLetterResponse
            {
                Items = operations.WebhookDeliveries.List()
            }, CoreJsonContext.Default.WebhookDeadLetterResponse);
        });

        app.MapPost("/admin/webhooks/dead-letter/{id}/replay", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.webhooks.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var record = operations.WebhookDeliveries.Get(id);
            if (record?.ReplayMessage is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Dead-letter item not found or replay is unavailable." });

            await runtime.Pipeline.InboundWriter.WriteAsync(record.ReplayMessage, ctx.RequestAborted);
            operations.WebhookDeliveries.MarkReplayed(id);
            RecordOperatorAudit(ctx, operations, auth, "webhook_replay", id, $"Replayed dead-letter item '{id}'.", success: true, before: null, after: record.Entry);
            return Results.Json(new MutationResponse { Success = true, Message = "Webhook replay queued." }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/webhooks/dead-letter/{id}/discard", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.webhooks.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var discarded = operations.WebhookDeliveries.MarkDiscarded(id);
            RecordOperatorAudit(ctx, operations, auth, "webhook_discard", id, discarded ? $"Discarded dead-letter item '{id}'." : $"Dead-letter item '{id}' was not found.", discarded, before: null, after: null);
            return discarded
                ? Results.Json(new MutationResponse { Success = true, Message = "Webhook dead-letter item discarded." }, CoreJsonContext.Default.MutationResponse)
                : Results.NotFound(new MutationResponse { Success = false, Error = "Dead-letter item not found." });
        });

        app.MapGet("/admin/rate-limits", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.rate-limits");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ActorRateLimitResponse
            {
                Policies = operations.ActorRateLimits.ListPolicies(),
                Active = operations.ActorRateLimits.SnapshotActive()
            }, CoreJsonContext.Default.ActorRateLimitResponse);
        });

        app.MapPost("/admin/rate-limits", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.rate-limits.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ActorRateLimitPolicy);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Rate-limit policy payload is required." });

            try
            {
                var saved = operations.ActorRateLimits.AddOrUpdate(request);
                RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_upsert", saved.Id, $"Updated rate-limit policy '{saved.Id}'.", success: true, before: null, after: saved);
                return Results.Json(saved, CoreJsonContext.Default.ActorRateLimitPolicy);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_upsert", request.Id ?? "new", ex.Message, success: false, before: null, after: request);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapDelete("/admin/rate-limits/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.rate-limits.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            try
            {
                var removed = operations.ActorRateLimits.Delete(id);
                RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_delete", id, removed ? $"Deleted rate-limit policy '{id}'." : $"Rate-limit policy '{id}' was not found.", removed, before: null, after: null);
                return removed
                    ? Results.Json(new MutationResponse { Success = true, Message = "Rate-limit policy deleted." }, CoreJsonContext.Default.MutationResponse)
                    : Results.NotFound(new MutationResponse { Success = false, Error = "Rate-limit policy not found." });
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_delete", id, ex.Message, success: false, before: null, after: null);
                return Results.Json(new MutationResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // ── Channel Auth Events ──────────────────────────────────────
        var authEventStore = runtime.ChannelAuthEvents;

        app.MapGet("/admin/channels/auth", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = authEventStore.GetAll().Select(MapChannelAuthStatusItem).ToArray()
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth", (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest(channelId, accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll(channelId).Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No auth event recorded for this channel." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth/stream", async (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, channelId, accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll("whatsapp").Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No WhatsApp auth event recorded." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/whatsapp/auth/stream", async (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, "whatsapp", accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth/qr.svg", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var evt = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId)
                : authEventStore.GetAll("whatsapp").FirstOrDefault(static item =>
                    string.Equals(item.State, "qr_code", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Data));
            if (evt is null || !string.Equals(evt.State, "qr_code", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(evt.Data))
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "No active WhatsApp QR code is available."
                });
            }

            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(evt.Data, QRCodeGenerator.ECCLevel.Q);
            var svg = new SvgQRCode(qrData).GetGraphic(6);
            return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
        });

        app.MapGet("/admin/channels/whatsapp/setup", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp setup loaded.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });

        app.MapPut("/admin/channels/whatsapp/setup", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.WhatsAppSetupRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "WhatsApp setup payload is required." });

            var normalizedRequestResult = NormalizeWhatsAppSetupRequest(request);
            if (normalizedRequestResult.Errors.Count > 0)
            {
                var invalidResponse = BuildWhatsAppSetupResponse(
                    startup,
                    runtime,
                    adminSettings,
                    pluginAdminSettings,
                    message: "WhatsApp setup validation failed.",
                    validationErrors: normalizedRequestResult.Errors);
                return Results.Json(invalidResponse, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var normalizedRequest = normalizedRequestResult.Request;
            var builtInResult = adminSettings.UpdateWhatsAppSettings(normalizedRequest);
            var validationErrors = ValidateWhatsAppPluginConfig(startup, runtime, normalizedRequest, out var pluginId, out var pluginConfig, out var pluginWarning);
            var pluginChanged = false;
            if (builtInResult.Success && validationErrors.Count == 0 && pluginId is not null)
            {
                pluginAdminSettings.Upsert(pluginId, pluginConfig, enabled: true);
                pluginChanged = true;
            }
            var response = BuildWhatsAppSetupResponse(
                startup,
                runtime,
                adminSettings,
                pluginAdminSettings,
                message: builtInResult.Success && validationErrors.Count == 0 ? "WhatsApp setup saved." : "WhatsApp setup validation failed.",
                restartRequired: builtInResult.RestartRequired || pluginChanged,
                validationErrors: [.. builtInResult.Errors, .. validationErrors],
                pluginWarningOverride: pluginWarning);

            if (builtInResult.Success && validationErrors.Count == 0)
            {
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "whatsapp_setup_update",
                    "whatsapp",
                    "Updated WhatsApp setup.",
                    success: true,
                    before: null,
                    after: normalizedRequest);
                return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
            }

            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapPost("/admin/channels/whatsapp/restart", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (!runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter) || adapter is not IRestartableChannelAdapter restartable)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Runtime restart is only available for plugin-backed WhatsApp channels." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            authEventStore.ClearChannel("whatsapp");
            await restartable.RestartAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "whatsapp_restart", "whatsapp", "Restarted WhatsApp channel.", success: true, before: null, after: null);

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp channel restarted.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });
    }

    private static async Task<JsonBodyReadResult<T>> ReadJsonBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength is > MaxAdminJsonBodyBytes)
            return new(default, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        if (ctx.Request.ContentLength is 0)
            return new(default, null);

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var payload = new MemoryStream();
            while (true)
            {
                var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted);
                if (read == 0)
                    break;

                if (payload.Length + read > MaxAdminJsonBodyBytes)
                    return new(default, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

                await payload.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            }

            if (payload.Length == 0)
                return new(default, null);

            payload.Position = 0;
            var value = await JsonSerializer.DeserializeAsync(payload, typeInfo, ctx.RequestAborted);
            return new(value, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AdminSettingsResponse BuildSettingsResponse(
        GatewayStartupContext startup,
        AdminSettingsService adminSettings,
        AdminSettingsSnapshot? snapshot = null,
        AdminSettingsPersistenceInfo? persistence = null,
        bool restartRequired = false,
        IReadOnlyList<string>? restartRequiredFields = null,
        string? message = null,
        IReadOnlyList<string>? extraWarnings = null)
    {
        var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind));
        var warnings = GetChannelWarnings(readiness);
        if (extraWarnings is { Count: > 0 })
            warnings.AddRange(extraWarnings);

        return new AdminSettingsResponse
        {
            Settings = snapshot ?? adminSettings.GetSnapshot(),
            Persistence = persistence ?? adminSettings.GetPersistence(),
            Message = message ?? "Settings loaded.",
            RestartRequired = restartRequired,
            RestartRequiredFields = restartRequiredFields ?? [],
            ImmediateFieldKeys = AdminSettingsService.ImmediateFieldKeys,
            RestartFieldKeys = AdminSettingsService.RestartFieldKeys,
            Warnings = warnings,
            ChannelReadiness = readiness
        };
    }

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
            FixGuidance = state.FixGuidance.Select(static item => new ChannelFixGuidanceDto
            {
                Label = item.Label,
                Href = item.Href,
                Reference = item.Reference
            }).ToArray()
        }).ToArray();

    private static List<string> GetChannelWarnings(IReadOnlyList<ChannelReadinessDto> readiness)
        => readiness
            .SelectMany(static item => item.Warnings.Select(warning => $"{item.DisplayName}: {warning}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static (EndpointHelpers.OperatorAuthorizationResult? Authorization, IResult? Failure) AuthorizeOperator(
        HttpContext ctx,
        GatewayStartupContext startup,
        BrowserSessionAuthService browserSessions,
        RuntimeOperationsState operations,
        bool requireCsrf,
        string endpointScope)
    {
        var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf);
        if (!auth.IsAuthorized)
            return (null, Results.Unauthorized());

        if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, endpointScope, out var blockedByPolicyId))
        {
            return (null, Results.Json(
                new OperationStatusResponse
                {
                    Success = false,
                    Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'."
                },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status429TooManyRequests));
        }

        return (auth, null);
    }

    private static void RecordOperatorAudit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        EndpointHelpers.OperatorAuthorizationResult auth,
        string actionType,
        string targetId,
        string summary,
        bool success,
        object? before,
        object? after)
    {
        operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActionType = actionType,
            TargetId = string.IsNullOrWhiteSpace(targetId) ? "unknown" : targetId,
            Summary = summary,
            Before = SerializeAuditValue(before),
            After = SerializeAuditValue(after),
            Success = success
        });
    }

    private static async Task<IReadOnlyList<MemoryNoteItem>> ListMemoryNotesAsync(
        IMemoryNoteCatalog catalog,
        string? prefix,
        string? memoryClass,
        string? projectId,
        int limit,
        CancellationToken ct)
    {
        var normalizedClass = NormalizeMemoryClass(memoryClass);
        var normalizedProjectId = NormalizeOptionalValue(projectId);
        var prefixFilter = BuildMemoryPrefix(normalizedClass, normalizedProjectId, NormalizeOptionalValue(prefix));
        var entries = await catalog.ListNotesAsync(prefixFilter, Math.Clamp(limit, 1, 500), ct);
        return entries
            .Select(static entry => MapMemoryNoteItem(entry.Key, entry.PreviewContent, entry.UpdatedAt))
            .Where(item => MatchesMemoryNoteFilter(item, normalizedClass, normalizedProjectId))
            .ToArray();
    }

    private static async Task<IReadOnlyList<MemoryNoteItem>> MaterializeMemoryNoteItemsAsync(
        IMemoryStore memoryStore,
        IReadOnlyList<MemoryNoteCatalogEntry> entries,
        bool includeContent,
        CancellationToken ct)
    {
        var items = new List<MemoryNoteItem>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string? content = null;
            if (includeContent)
                content = await memoryStore.LoadNoteAsync(entry.Key, ct) ?? entry.PreviewContent;

            items.Add(MapMemoryNoteItem(entry.Key, content ?? entry.PreviewContent, entry.UpdatedAt, includeContent));
        }

        return items;
    }

    private static MemoryNoteItem MapMemoryNoteItem(string key, string content, DateTimeOffset updatedAt, bool includeContent = false)
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
            Content = includeContent ? content : null,
            UpdatedAtUtc = updatedAt
        };
    }

    private static bool MatchesMemoryNoteFilter(MemoryNoteItem item, string? memoryClass, string? projectId)
    {
        if (!string.IsNullOrWhiteSpace(memoryClass) &&
            !string.Equals(item.MemoryClass, memoryClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(projectId) &&
            !string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? NormalizeMemoryClass(string? memoryClass)
    {
        if (string.IsNullOrWhiteSpace(memoryClass))
            return null;

        return memoryClass.Trim().ToLowerInvariant() switch
        {
            MemoryNoteClass.General => MemoryNoteClass.General,
            MemoryNoteClass.ProjectFact => MemoryNoteClass.ProjectFact,
            MemoryNoteClass.OperationalRunbook => MemoryNoteClass.OperationalRunbook,
            MemoryNoteClass.ApprovedSkill => MemoryNoteClass.ApprovedSkill,
            MemoryNoteClass.ApprovedAutomation => MemoryNoteClass.ApprovedAutomation,
            _ => null
        };
    }

    private static string? NormalizeOptionalValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildMemoryPrefix(string? memoryClass, string? projectId, string? prefixSuffix)
    {
        var prefix = memoryClass switch
        {
            MemoryNoteClass.ProjectFact when !string.IsNullOrWhiteSpace(projectId) => $"project:{projectId}:",
            MemoryNoteClass.ProjectFact => "project:",
            MemoryNoteClass.OperationalRunbook => "runbook:",
            MemoryNoteClass.ApprovedSkill => "skill:",
            MemoryNoteClass.ApprovedAutomation => "automation:",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(prefixSuffix))
            return prefix;

        return string.Concat(prefix, prefixSuffix.Trim());
    }

    private static string? BuildMemoryNoteKey(string? key, string? memoryClass, string? projectId, out string? error)
    {
        error = null;
        var normalizedClass = memoryClass ?? MemoryNoteClass.General;
        var normalizedKey = NormalizeOptionalValue(key);
        var normalizedProjectId = NormalizeOptionalValue(projectId);

        if (normalizedClass == MemoryNoteClass.ProjectFact)
        {
            if (string.IsNullOrWhiteSpace(normalizedProjectId))
            {
                error = "projectId is required for project_fact memory.";
                return null;
            }

            var projectError = InputSanitizer.CheckMemoryKey(normalizedProjectId);
            if (projectError is not null)
            {
                error = projectError;
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            error = "key is required.";
            return null;
        }

        var keyError = InputSanitizer.CheckMemoryKey(normalizedKey);
        if (keyError is not null)
        {
            error = keyError;
            return null;
        }

        return normalizedClass switch
        {
            MemoryNoteClass.ProjectFact => $"project:{normalizedProjectId}:{normalizedKey}",
            MemoryNoteClass.OperationalRunbook => $"runbook:{normalizedKey}",
            MemoryNoteClass.ApprovedSkill => $"skill:{normalizedKey}",
            MemoryNoteClass.ApprovedAutomation => $"automation:{normalizedKey}",
            _ => normalizedKey
        };
    }

    private static (string MemoryClass, string? ProjectId, string DisplayKey) ClassifyMemoryNoteKey(string key)
    {
        if (key.StartsWith("project:", StringComparison.Ordinal))
        {
            var segments = key.Split(':', 3, StringSplitOptions.None);
            if (segments.Length == 3)
            {
                return (MemoryNoteClass.ProjectFact, segments[1], segments[2]);
            }
        }

        if (key.StartsWith("runbook:", StringComparison.Ordinal))
            return (MemoryNoteClass.OperationalRunbook, null, key["runbook:".Length..]);

        if (key.StartsWith("skill:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedSkill, null, key["skill:".Length..]);

        if (key.StartsWith("automation:", StringComparison.Ordinal))
            return (MemoryNoteClass.ApprovedAutomation, null, key["automation:".Length..]);

        return (MemoryNoteClass.General, null, key);
    }

    private static bool ProposalMatchesActor(LearningProposal proposal, string actorId)
    {
        if (string.Equals(proposal.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(proposal.ProfileUpdate?.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(proposal.AppliedProfileBefore?.ActorId, actorId, StringComparison.OrdinalIgnoreCase);
    }

    private static UserProfile NormalizeProfile(UserProfile profile)
    {
        var normalizedActorId = string.IsNullOrWhiteSpace(profile.ActorId) ? $"{profile.ChannelId}:{profile.SenderId}" : profile.ActorId.Trim();
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

    private static ChannelAuthStatusItem MapChannelAuthStatusItem(BridgeChannelAuthEvent evt)
        => new()
        {
            ChannelId = evt.ChannelId,
            State = evt.State,
            Data = evt.Data,
            AccountId = evt.AccountId,
            UpdatedAtUtc = evt.UpdatedAtUtc
        };

    private static WhatsAppSetupResponse BuildWhatsAppSetupResponse(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        AdminSettingsService adminSettings,
        PluginAdminSettingsService pluginAdminSettings,
        string message = "",
        bool restartRequired = false,
        IReadOnlyList<string>? validationErrors = null,
        string? pluginWarningOverride = null)
    {
        var snapshot = adminSettings.GetSnapshot();
        var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind))
            .FirstOrDefault(static item => string.Equals(item.ChannelId, "whatsapp", StringComparison.Ordinal));
        var isFirstPartyWorker = string.Equals(snapshot.WhatsAppType, "first_party_worker", StringComparison.OrdinalIgnoreCase)
            || runtime.WhatsAppWorkerHost is not null;
        var pluginTarget = isFirstPartyWorker
            ? new WhatsAppPluginTarget(null, null, null)
            : ResolveWhatsAppPluginTarget(startup, runtime, pluginIdOverride: null);
        var pluginId = pluginTarget.PluginId;
        var pluginEntry = pluginId is null ? null : pluginAdminSettings.GetEntry(pluginId);
        if (pluginEntry is null && pluginId is not null && startup.Config.Plugins.Entries.TryGetValue(pluginId, out var configuredPluginEntry))
            pluginEntry = configuredPluginEntry;

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(pluginWarningOverride))
            warnings.Add(pluginWarningOverride);
        else if (!string.IsNullOrWhiteSpace(pluginTarget.Warning))
            warnings.Add(pluginTarget.Warning);
        if (readiness is not null)
            warnings.AddRange(readiness.Warnings);
        warnings.Add("WhatsApp secrets are redacted on read. Leave secret values blank to preserve them, or clear both the value and its corresponding *Ref field to remove them.");

        var restartSupported = runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter)
            && adapter is IRestartableChannelAdapter;

        return new WhatsAppSetupResponse
        {
            ActiveBackend = DetermineActiveWhatsAppBackend(runtime, snapshot),
            ConfiguredType = snapshot.WhatsAppType,
            Message = message,
            RestartRequired = restartRequired,
            Enabled = snapshot.WhatsAppEnabled,
            DmPolicy = snapshot.WhatsAppDmPolicy,
            WebhookPath = snapshot.WhatsAppWebhookPath,
            WebhookPublicBaseUrl = snapshot.WhatsAppWebhookPublicBaseUrl,
            WebhookVerifyToken = "",
            WebhookVerifyTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppWebhookVerifyToken, snapshot.WhatsAppWebhookVerifyTokenRef),
            WebhookVerifyTokenRef = snapshot.WhatsAppWebhookVerifyTokenRef,
            ValidateSignature = snapshot.WhatsAppValidateSignature,
            WebhookAppSecret = null,
            WebhookAppSecretConfigured = HasConfiguredSecretValue(snapshot.WhatsAppWebhookAppSecret, snapshot.WhatsAppWebhookAppSecretRef),
            WebhookAppSecretRef = snapshot.WhatsAppWebhookAppSecretRef,
            CloudApiToken = null,
            CloudApiTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppCloudApiToken, snapshot.WhatsAppCloudApiTokenRef),
            CloudApiTokenRef = snapshot.WhatsAppCloudApiTokenRef,
            PhoneNumberId = snapshot.WhatsAppPhoneNumberId,
            BusinessAccountId = snapshot.WhatsAppBusinessAccountId,
            BridgeUrl = snapshot.WhatsAppBridgeUrl,
            BridgeToken = null,
            BridgeTokenConfigured = HasConfiguredSecretValue(snapshot.WhatsAppBridgeToken, snapshot.WhatsAppBridgeTokenRef),
            BridgeTokenRef = snapshot.WhatsAppBridgeTokenRef,
            BridgeSuppressSendExceptions = snapshot.WhatsAppBridgeSuppressSendExceptions,
            FirstPartyWorker = snapshot.WhatsAppFirstPartyWorker,
            FirstPartyWorkerConfigJson = PrettyJson(JsonSerializer.SerializeToElement(snapshot.WhatsAppFirstPartyWorker, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)),
            FirstPartyWorkerConfigSchemaJson = GetWhatsAppFirstPartyWorkerConfigSchemaJson(),
            PluginDetected = pluginId is not null,
            PluginId = pluginId,
            PluginConfigJson = pluginEntry?.Config is { } pluginConfig ? PrettyJson(pluginConfig) : null,
            PluginConfigSchemaJson = pluginTarget.Plugin?.Manifest.ConfigSchema is { } pluginSchema ? PrettyJson(pluginSchema) : null,
            PluginUiHintsJson = pluginTarget.Plugin?.Manifest.UiHints is { } uiHints ? PrettyJson(uiHints) : null,
            PluginWarning = pluginWarningOverride ?? pluginTarget.Warning,
            RestartSupported = restartSupported,
            RestartHint = restartSupported
                ? "Runtime restart is available for the active plugin-backed WhatsApp channel."
                : "Built-in WhatsApp configuration changes require a gateway restart.",
            DerivedWebhookUrl = BuildDerivedWebhookUrl(snapshot.WhatsAppWebhookPublicBaseUrl, snapshot.WhatsAppWebhookPath),
            Readiness = readiness,
            AuthStates = runtime.ChannelAuthEvents.GetAll("whatsapp").Select(MapChannelAuthStatusItem).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            ValidationErrors = validationErrors?.ToArray() ?? []
        };
    }

    private static List<string> ValidateWhatsAppPluginConfig(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        WhatsAppSetupRequest request,
        out string? pluginId,
        out JsonElement? pluginConfig,
        out string? pluginWarning)
    {
        pluginId = null;
        pluginConfig = null;
        pluginWarning = null;

        if (string.IsNullOrWhiteSpace(request.PluginConfigJson) && string.IsNullOrWhiteSpace(request.PluginId))
            return [];

        var pluginTarget = ResolveWhatsAppPluginTarget(startup, runtime, request.PluginId);
        pluginWarning = pluginTarget.Warning;
        pluginId = pluginTarget.PluginId;
        if (pluginId is null)
            return [pluginWarning ?? "No unique plugin-backed WhatsApp channel is available for bridge configuration."];

        if (!string.IsNullOrWhiteSpace(request.PluginConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(request.PluginConfigJson);
                pluginConfig = document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                return [$"Plugin config JSON is invalid: {ex.Message}"];
            }
        }

        if (pluginTarget.Plugin?.Manifest is { } manifest)
        {
            var diagnostics = PluginConfigValidator.Validate(manifest, pluginConfig);
            var errors = diagnostics
                .Where(static diagnostic => !string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                .Select(static diagnostic => diagnostic.Message)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (errors.Count > 0)
                return errors;
        }
        return [];
    }

    private static string DetermineActiveWhatsAppBackend(GatewayAppRuntime runtime, AdminSettingsSnapshot snapshot)
    {
        if (runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter))
        {
            if (runtime.WhatsAppWorkerHost is not null && adapter is BridgedChannelAdapter)
                return "first_party_worker";

            return adapter switch
            {
                BridgedChannelAdapter => "plugin_bridge",
                WhatsAppBridgeChannel => "built_in_bridge",
                WhatsAppChannel => "official",
                _ => snapshot.WhatsAppType
            };
        }

        if (!snapshot.WhatsAppEnabled)
            return "disabled";

        if (string.Equals(snapshot.WhatsAppType, "first_party_worker", StringComparison.OrdinalIgnoreCase))
            return "first_party_worker";

        return string.Equals(snapshot.WhatsAppType, "bridge", StringComparison.OrdinalIgnoreCase)
            ? "built_in_bridge"
            : "official";
    }

    private static (WhatsAppSetupRequest Request, List<string> Errors) NormalizeWhatsAppSetupRequest(WhatsAppSetupRequest request)
    {
        var errors = new List<string>();
        var workerConfig = request.FirstPartyWorker;
        if (!string.IsNullOrWhiteSpace(request.FirstPartyWorkerConfigJson))
        {
            try
            {
                workerConfig = JsonSerializer.Deserialize(
                    request.FirstPartyWorkerConfigJson,
                    CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig);
            }
            catch (Exception ex)
            {
                errors.Add($"First-party worker config JSON is invalid: {ex.Message}");
            }
        }

        return (new WhatsAppSetupRequest
        {
            Enabled = request.Enabled,
            Type = request.Type,
            DmPolicy = request.DmPolicy,
            WebhookPath = request.WebhookPath,
            WebhookPublicBaseUrl = request.WebhookPublicBaseUrl,
            WebhookVerifyToken = request.WebhookVerifyToken,
            WebhookVerifyTokenRef = request.WebhookVerifyTokenRef,
            ValidateSignature = request.ValidateSignature,
            WebhookAppSecret = request.WebhookAppSecret,
            WebhookAppSecretRef = request.WebhookAppSecretRef,
            CloudApiToken = request.CloudApiToken,
            CloudApiTokenRef = request.CloudApiTokenRef,
            PhoneNumberId = request.PhoneNumberId,
            BusinessAccountId = request.BusinessAccountId,
            BridgeUrl = request.BridgeUrl,
            BridgeToken = request.BridgeToken,
            BridgeTokenRef = request.BridgeTokenRef,
            BridgeSuppressSendExceptions = request.BridgeSuppressSendExceptions,
            PluginId = request.PluginId,
            PluginConfigJson = request.PluginConfigJson,
            FirstPartyWorker = workerConfig,
            FirstPartyWorkerConfigJson = request.FirstPartyWorkerConfigJson
        }, errors);
    }

    private static string? BuildDerivedWebhookUrl(string? publicBaseUrl, string webhookPath)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl) || string.IsNullOrWhiteSpace(webhookPath))
            return null;

        try
        {
            var baseUri = new Uri(publicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(baseUri, webhookPath.TrimStart('/')).ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string PrettyJson(JsonElement value)
        => value.GetRawText();

    private static string GetWhatsAppFirstPartyWorkerConfigSchemaJson()
        => """
           {
             "type": "object",
             "properties": {
               "driver": { "type": "string", "enum": ["baileys", "baileys_csharp", "whatsmeow", "simulated"] },
               "executablePath": { "type": "string" },
               "workingDirectory": { "type": "string" },
               "storagePath": { "type": "string" },
               "mediaCachePath": { "type": "string" },
               "historySync": { "type": "boolean" },
               "proxy": { "type": "string" },
               "accounts": {
                 "type": "array",
                 "items": {
                   "type": "object",
                   "properties": {
                     "accountId": { "type": "string" },
                     "sessionPath": { "type": "string" },
                     "deviceName": { "type": "string" },
                     "pairingMode": { "type": "string", "enum": ["qr", "pairing_code"] },
                     "phoneNumber": { "type": "string" },
                     "sendReadReceipts": { "type": "boolean" },
                     "ackReaction": { "type": "boolean" },
                     "mediaCachePath": { "type": "string" },
                     "historySync": { "type": "boolean" },
                     "proxy": { "type": "string" }
                   },
                   "required": ["accountId", "sessionPath", "pairingMode"]
                 }
               }
             },
             "required": ["driver", "accounts"]
           }
           """;

    private static WhatsAppPluginTarget ResolveWhatsAppPluginTarget(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        string? pluginIdOverride)
    {
        if (runtime.PluginHost is null)
            return new(null, pluginIdOverride is null ? null : $"Plugin '{pluginIdOverride}' is not loaded.", null);

        var registrations = runtime.PluginHost.ChannelRegistrations
            .Where(registration => string.Equals(registration.ChannelId, "whatsapp", StringComparison.Ordinal))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(pluginIdOverride))
            registrations = registrations
                .Where(registration => string.Equals(registration.PluginId, pluginIdOverride, StringComparison.Ordinal))
                .ToArray();

        if (registrations.Length == 0)
        {
            return new(
                null,
                pluginIdOverride is null
                    ? null
                    : $"Plugin '{pluginIdOverride}' is not currently loaded for channel 'whatsapp'.",
                null);
        }

        if (registrations.Length > 1)
            return new(null, "Multiple plugins register channel 'whatsapp'. Configure a specific plugin id.", null);

        var pluginId = registrations[0].PluginId;
        var discovered = PluginDiscovery.DiscoverWithDiagnostics(startup.Config.Plugins, startup.WorkspacePath).Plugins
            .FirstOrDefault(plugin => string.Equals(plugin.Manifest.Id, pluginId, StringComparison.Ordinal));
        return new(pluginId, null, discovered);
    }

    private sealed record WhatsAppPluginTarget(string? PluginId, string? Warning, DiscoveredPlugin? Plugin);

    private static string? SerializeAuditValue(object? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            ProviderPolicyRule item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ProviderPolicyRule),
            PluginOperatorState item => JsonSerializer.Serialize(item, CoreJsonContext.Default.PluginOperatorState),
            ToolApprovalGrant item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ToolApprovalGrant),
            SessionMetadataSnapshot item => JsonSerializer.Serialize(item, CoreJsonContext.Default.SessionMetadataSnapshot),
            ActorRateLimitPolicy item => JsonSerializer.Serialize(item, CoreJsonContext.Default.ActorRateLimitPolicy),
            WebhookDeadLetterEntry item => JsonSerializer.Serialize(item, CoreJsonContext.Default.WebhookDeadLetterEntry),
            AdminSettingsSnapshot item => JsonSerializer.Serialize(item, CoreJsonContext.Default.AdminSettingsSnapshot),
            HeartbeatConfigDto item => JsonSerializer.Serialize(item, CoreJsonContext.Default.HeartbeatConfigDto),
            _ => value.ToString()
        };
    }

    private static SessionState? ParseSessionState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<SessionState>(value, ignoreCase: true, out var state)
            ? state
            : null;
    }

    private static string BuildTranscript(Session session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {session.Id}");
        sb.AppendLine($"Channel: {session.ChannelId}");
        sb.AppendLine($"Sender: {session.SenderId}");
        sb.AppendLine($"Created: {session.CreatedAt:O}");
        sb.AppendLine($"LastActive: {session.LastActiveAt:O}");
        sb.AppendLine();

        foreach (var turn in session.History)
        {
            sb.AppendLine($"[{turn.Timestamp:O}] {turn.Role}:");
            sb.AppendLine(turn.Content);
            if (turn.ToolCalls is { Count: > 0 })
            {
                sb.AppendLine("Tools:");
                foreach (var call in turn.ToolCalls)
                {
                    sb.AppendLine($"- {call.ToolName}");
                    sb.AppendLine($"  args: {call.Arguments}");
                    if (!string.IsNullOrWhiteSpace(call.Result))
                        sb.AppendLine($"  result: {call.Result}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? TryExtractSessionIdFromBranchId(string branchId)
    {
        var marker = ":branch:";
        var index = branchId.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? branchId[..index] : null;
    }

    private static async Task<ApprovalSimulationResponse> SimulateApprovalAsync(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        ApprovalSimulationRequest request,
        CancellationToken ct)
    {
        var effectiveTooling = CloneToolingConfig(startup.Config.Tooling, request.AutonomyMode);
        var argumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson;
        var normalizedToolName = NormalizeApprovalToolName(request.ToolName!);
        var requireToolApproval = request.RequireToolApproval ?? runtime.EffectiveRequireToolApproval;
        var approvalRequiredTools = (request.ApprovalRequiredTools is { Length: > 0 }
                ? request.ApprovalRequiredTools
                : runtime.EffectiveApprovalRequiredTools.ToArray())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeApprovalToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var autonomyHook = new AutonomyHook(effectiveTooling, NullLogger.Instance);
        var allowed = await autonomyHook.BeforeExecuteAsync(normalizedToolName, argumentsJson, ct);
        if (!allowed)
        {
            return new ApprovalSimulationResponse
            {
                Decision = "deny",
                Reason = "Autonomy or path policy would deny this tool execution.",
                ToolName = request.ToolName!,
                AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
                RequireToolApproval = requireToolApproval,
                ApprovalRequiredTools = approvalRequiredTools
            };
        }

        if (requireToolApproval && approvalRequiredTools.Contains(normalizedToolName, StringComparer.OrdinalIgnoreCase))
        {
            return new ApprovalSimulationResponse
            {
                Decision = "requires_approval",
                Reason = "The effective approval policy requires approval for this tool.",
                ToolName = request.ToolName!,
                AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
                RequireToolApproval = requireToolApproval,
                ApprovalRequiredTools = approvalRequiredTools
            };
        }

        return new ApprovalSimulationResponse
        {
            Decision = "allow",
            Reason = "The tool passes autonomy checks and is not currently approval-gated.",
            ToolName = request.ToolName!,
            AutonomyMode = (effectiveTooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
            RequireToolApproval = requireToolApproval,
            ApprovalRequiredTools = approvalRequiredTools
        };
    }

    private static ToolingConfig CloneToolingConfig(ToolingConfig source, string? autonomyModeOverride)
        => new()
        {
            AutonomyMode = string.IsNullOrWhiteSpace(autonomyModeOverride) ? source.AutonomyMode : autonomyModeOverride,
            WorkspaceRoot = source.WorkspaceRoot,
            WorkspaceOnly = source.WorkspaceOnly,
            AllowedShellCommandGlobs = source.AllowedShellCommandGlobs,
            ForbiddenPathGlobs = source.ForbiddenPathGlobs,
            AllowShell = source.AllowShell,
            ReadOnlyMode = source.ReadOnlyMode,
            AllowedReadRoots = source.AllowedReadRoots,
            AllowedWriteRoots = source.AllowedWriteRoots,
            ToolTimeoutSeconds = source.ToolTimeoutSeconds,
            ParallelToolExecution = source.ParallelToolExecution,
            RequireToolApproval = source.RequireToolApproval,
            ApprovalRequiredTools = source.ApprovalRequiredTools,
            ToolApprovalTimeoutSeconds = source.ToolApprovalTimeoutSeconds,
            EnableBrowserTool = source.EnableBrowserTool,
            AllowBrowserEvaluate = source.AllowBrowserEvaluate,
            BrowserHeadless = source.BrowserHeadless,
            BrowserTimeoutSeconds = source.BrowserTimeoutSeconds
        };

    private static string NormalizeApprovalToolName(string toolName)
        => string.Equals(toolName.Trim(), "file_write", StringComparison.Ordinal)
            ? "write_file"
            : toolName.Trim();

    private static ApprovalHistoryEntry RedactApprovalHistory(ApprovalHistoryEntry entry)
        => new()
        {
            EventType = entry.EventType,
            ApprovalId = entry.ApprovalId,
            SessionId = entry.SessionId,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            ToolName = entry.ToolName,
            ArgumentsPreview = RedactSensitiveText(entry.ArgumentsPreview),
            TimestampUtc = entry.TimestampUtc,
            DecisionAtUtc = entry.DecisionAtUtc,
            ActorChannelId = entry.ActorChannelId,
            ActorSenderId = entry.ActorSenderId,
            DecisionSource = entry.DecisionSource,
            Approved = entry.Approved
        };

    private static RuntimeEventEntry RedactRuntimeEvent(RuntimeEventEntry entry)
        => new()
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            SessionId = entry.SessionId,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            CorrelationId = entry.CorrelationId,
            Component = entry.Component,
            Action = entry.Action,
            Severity = entry.Severity,
            Summary = RedactSensitiveText(entry.Summary),
            Metadata = entry.Metadata?.ToDictionary(
                static kvp => kvp.Key,
                static kvp => ShouldRedactKey(kvp.Key) ? "[redacted]" : RedactSensitiveText(kvp.Value),
                StringComparer.Ordinal)
        };

    private static WebhookDeadLetterEntry RedactDeadLetter(WebhookDeadLetterEntry entry)
        => new()
        {
            Id = entry.Id,
            Source = entry.Source,
            DeliveryKey = entry.DeliveryKey,
            EndpointName = entry.EndpointName,
            ChannelId = entry.ChannelId,
            SenderId = entry.SenderId,
            SessionId = entry.SessionId,
            CreatedAtUtc = entry.CreatedAtUtc,
            Error = RedactSensitiveText(entry.Error),
            PayloadPreview = RedactSensitiveText(entry.PayloadPreview),
            Discarded = entry.Discarded,
            ReplayedAtUtc = entry.ReplayedAtUtc
        };

    private static string RedactSensitiveText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var redacted = value.Replace("raw:", "raw:[redacted]", StringComparison.OrdinalIgnoreCase);
        foreach (var marker in new[] { "token", "secret", "password", "apikey", "authorization" })
        {
            if (redacted.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return "[redacted]";
        }

        return redacted;
    }

    private static bool ShouldRedactKey(string key)
        => key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
           || key.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static bool HasConfiguredSecretValue(string? value, string? valueRef)
        => !string.IsNullOrWhiteSpace(SecretResolver.Resolve(valueRef) ?? value);

    internal static async Task StreamChannelAuthEventsAsync(
        HttpContext ctx,
        ChannelAuthEventStore authEventStore,
        string channelId,
        string? accountId)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        using var subscription = authEventStore.Subscribe();
        var ct = ctx.RequestAborted;

        try
        {
            await WriteSseCommentAsync(ctx, "stream-open", ct);

            var currentItems = accountId is not null
                ? authEventStore.GetLatest(channelId, accountId) is { } currentEvt ? [currentEvt] : []
                : authEventStore.GetAll(channelId);
            foreach (var current in currentItems)
            {
                await WriteChannelAuthEventAsync(ctx, current, ct);
            }

            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                if (!string.Equals(evt.ChannelId, channelId, StringComparison.Ordinal))
                    continue;
                if (accountId is not null && !string.Equals(evt.AccountId, accountId, StringComparison.Ordinal))
                    continue;

                await WriteChannelAuthEventAsync(ctx, evt, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RequestAborted is the normal shutdown path for disconnected SSE clients.
        }
    }

    private static async Task WriteChannelAuthEventAsync(HttpContext ctx, BridgeChannelAuthEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(MapChannelAuthStatusItem(evt), CoreJsonContext.Default.ChannelAuthStatusItem);
        await ctx.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteSseCommentAsync(HttpContext ctx, string comment, CancellationToken cancellationToken)
    {
        await ctx.Response.WriteAsync($": {comment}\n\n", cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private readonly record struct JsonBodyReadResult<T>(
        T? Value,
        IResult? Failure)
        where T : class;
}
