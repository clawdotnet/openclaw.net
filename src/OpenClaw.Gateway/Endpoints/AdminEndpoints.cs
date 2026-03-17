using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class AdminEndpoints
{
    public static void MapOpenClawAdminEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var adminSettings = app.Services.GetRequiredService<AdminSettingsService>();
        var sessionAdminStore = (ISessionAdminStore)app.Services.GetRequiredService<IMemoryStore>();
        var operations = runtime.Operations;

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
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.AuthSessionRequest,
                    ctx.RequestAborted);
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
            var persisted = await sessionAdminStore.ListSessionsAsync(page, pageSize, query, ctx.RequestAborted);
            var active = (await runtime.SessionManager.ListActiveAsync(ctx.RequestAborted))
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

            var persistedFiltered = new PagedSessionList
            {
                Page = persisted.Page,
                PageSize = persisted.PageSize,
                HasMore = persisted.HasMore,
                Items = persisted.Items
                    .Where(item => MatchesSummaryQuery(item, query, metadataById))
                    .ToArray()
            };

            return Results.Json(new AdminSessionsResponse
            {
                Filters = query,
                Active = active,
                Persisted = persistedFiltered
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

            var snapshot = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.AdminSettingsSnapshot,
                ctx.RequestAborted);

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
                Routes = operations.LlmExecution.SnapshotRoutes(),
                Usage = runtime.ProviderUsage.Snapshot(),
                Policies = operations.ProviderPolicies.List(),
                RecentTurns = runtime.ProviderUsage.RecentTurns(limit: 50)
            }, CoreJsonContext.Default.ProviderAdminResponse);
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

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.ProviderPolicyRule,
                ctx.RequestAborted);
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

            var before = operations.ProviderPolicies.List().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var removed = operations.ProviderPolicies.Delete(id);
            RecordOperatorAudit(ctx, operations, auth, "provider_policy_delete", id, removed ? $"Deleted provider policy '{id}'." : $"Provider policy '{id}' was not found.", removed, before, after: null);
            return removed
                ? Results.Json(new MutationResponse { Success = true, Message = "Provider policy deleted." }, CoreJsonContext.Default.MutationResponse)
                : Results.NotFound(new MutationResponse { Success = false, Error = "Provider policy not found." });
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

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.SessionMetadataUpdateRequest,
                ctx.RequestAborted);
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Session metadata payload is required." });

            var before = operations.SessionMetadata.Get(id);
            var updated = operations.SessionMetadata.Set(id, request);
            RecordOperatorAudit(ctx, operations, auth, "session_metadata_update", id, $"Updated session metadata for '{id}'.", success: true, before, updated);
            return Results.Json(updated, CoreJsonContext.Default.SessionMetadataSnapshot);
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
            var persisted = await sessionAdminStore.ListSessionsAsync(1, 200, query, ctx.RequestAborted);
            var items = new List<SessionExportItem>();
            foreach (var summary in persisted.Items.Where(item => MatchesSummaryQuery(item, query, metadataById)))
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.PluginMutationRequest, ctx.RequestAborted);
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.PluginMutationRequest, ctx.RequestAborted);
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.PluginMutationRequest, ctx.RequestAborted);
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.PluginMutationRequest, ctx.RequestAborted);
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.ToolApprovalGrant, ctx.RequestAborted);
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Approval policy payload is required." });

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
        });

        app.MapDelete("/tools/approval-policies/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approval-policies.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var removed = operations.ApprovalGrants.Delete(id);
            RecordOperatorAudit(ctx, operations, auth, "approval_grant_delete", id, removed ? $"Deleted tool approval grant '{id}'." : $"Tool approval grant '{id}' was not found.", removed, before: null, after: null);
            return removed
                ? Results.Json(new MutationResponse { Success = true, Message = "Approval grant deleted." }, CoreJsonContext.Default.MutationResponse)
                : Results.NotFound(new MutationResponse { Success = false, Error = "Approval grant not found." });
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

            var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.ActorRateLimitPolicy, ctx.RequestAborted);
            if (request is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Rate-limit policy payload is required." });

            var saved = operations.ActorRateLimits.AddOrUpdate(request);
            RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_upsert", saved.Id, $"Updated rate-limit policy '{saved.Id}'.", success: true, before: null, after: saved);
            return Results.Json(saved, CoreJsonContext.Default.ActorRateLimitPolicy);
        });

        app.MapDelete("/admin/rate-limits/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.rate-limits.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var removed = operations.ActorRateLimits.Delete(id);
            RecordOperatorAudit(ctx, operations, auth, "rate_limit_policy_delete", id, removed ? $"Deleted rate-limit policy '{id}'." : $"Rate-limit policy '{id}' was not found.", removed, before: null, after: null);
            return removed
                ? Results.Json(new MutationResponse { Success = true, Message = "Rate-limit policy deleted." }, CoreJsonContext.Default.MutationResponse)
                : Results.NotFound(new MutationResponse { Success = false, Error = "Rate-limit policy not found." });
        });
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
}
