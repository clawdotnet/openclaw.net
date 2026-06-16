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
    private static void MapSessionEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var automationService = services.AutomationService;
        var proposalStore = services.ProposalStore;
        var operations = services.Operations;
        var sessionAdminStore = services.SessionAdminStore;

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
                    StableSessionId = session.StableSessionBinding?.ExternalSessionId,
                    StableSessionNamespace = session.StableSessionBinding?.Namespace,
                    StableSessionOwnerKey = session.StableSessionBinding?.OwnerKey,
                    CreatedAt = session.CreatedAt,
                    LastActiveAt = session.LastActiveAt,
                    State = session.State,
                    HistoryTurns = session.History.Count,
                    TotalInputTokens = session.TotalInputTokens,
                    TotalOutputTokens = session.TotalOutputTokens,
                    TotalCacheReadTokens = session.TotalCacheReadTokens,
                    TotalCacheWriteTokens = session.TotalCacheWriteTokens,
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

        app.MapPost("/admin/sessions/{id}/promote", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.session.promote");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.SessionPromotionRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
            {
                return Results.BadRequest(new SessionPromotionResponse
                {
                    Success = false,
                    Target = string.Empty,
                    Error = "Session promotion payload is required."
                });
            }

            var session = await runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null)
            {
                return Results.NotFound(new SessionPromotionResponse
                {
                    Success = false,
                    Target = request.Target,
                    Error = "Session not found."
                });
            }

            try
            {
                SessionPromotionResponse response;
                var normalizedTarget = NormalizeSessionPromotionTarget(request.Target);
                switch (normalizedTarget)
                {
                    case SessionPromotionTarget.Automation:
                    {
                        var automation = await automationService.SaveAsync(
                            BuildAutomationPromotion(session, request),
                            ctx.RequestAborted);
                        operations.RuntimeEvents.Append(new RuntimeEventEntry
                        {
                            Id = $"evt_{Guid.NewGuid():N}"[..20],
                            TimestampUtc = DateTimeOffset.UtcNow,
                            SessionId = session.Id,
                            ChannelId = automation.DeliveryChannelId,
                            SenderId = automation.DeliveryRecipientId,
                            Component = "session-promotion",
                            Action = "automation",
                            Severity = "info",
                            Summary = $"Promoted session '{session.Id}' into automation '{automation.Id}'."
                        });
                        response = new SessionPromotionResponse
                        {
                            Success = true,
                            Target = normalizedTarget,
                            Message = "Automation draft created.",
                            CreatedId = automation.Id,
                            Automation = automation
                        };
                        RecordOperatorAudit(ctx, operations, auth, "session_promote_automation", session.Id, $"Promoted session '{session.Id}' into automation '{automation.Id}'.", success: true, before: null, after: automation);
                        return Results.Json(response, CoreJsonContext.Default.SessionPromotionResponse);
                    }
                    case SessionPromotionTarget.ProviderPolicy:
                    {
                        var policy = BuildProviderPolicyPromotion(session, request, runtime.ProviderUsage);
                        var saved = operations.ProviderPolicies.AddOrUpdate(policy);
                        operations.RuntimeEvents.Append(new RuntimeEventEntry
                        {
                            Id = $"evt_{Guid.NewGuid():N}"[..20],
                            TimestampUtc = DateTimeOffset.UtcNow,
                            SessionId = session.Id,
                            ChannelId = session.ChannelId,
                            SenderId = session.SenderId,
                            Component = "session-promotion",
                            Action = "provider-policy",
                            Severity = "info",
                            Summary = $"Promoted session '{session.Id}' into provider policy '{saved.Id}'."
                        });
                        response = new SessionPromotionResponse
                        {
                            Success = true,
                            Target = normalizedTarget,
                            Message = "Provider policy created.",
                            CreatedId = saved.Id,
                            ProviderPolicy = saved
                        };
                        RecordOperatorAudit(ctx, operations, auth, "session_promote_provider_policy", session.Id, $"Promoted session '{session.Id}' into provider policy '{saved.Id}'.", success: true, before: null, after: saved);
                        return Results.Json(response, CoreJsonContext.Default.SessionPromotionResponse);
                    }
                    case SessionPromotionTarget.SkillDraft:
                    {
                        var proposal = BuildSkillPromotion(session, request);
                        await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                        operations.RuntimeEvents.Append(new RuntimeEventEntry
                        {
                            Id = $"evt_{Guid.NewGuid():N}"[..20],
                            TimestampUtc = DateTimeOffset.UtcNow,
                            SessionId = session.Id,
                            ChannelId = session.ChannelId,
                            SenderId = session.SenderId,
                            Component = "session-promotion",
                            Action = "skill-draft",
                            Severity = "info",
                            Summary = $"Promoted session '{session.Id}' into skill draft proposal '{proposal.Id}'."
                        });
                        response = new SessionPromotionResponse
                        {
                            Success = true,
                            Target = normalizedTarget,
                            Message = "Skill draft proposal created.",
                            CreatedId = proposal.Id,
                            Proposal = proposal
                        };
                        RecordOperatorAudit(ctx, operations, auth, "session_promote_skill_draft", session.Id, $"Promoted session '{session.Id}' into skill draft proposal '{proposal.Id}'.", success: true, before: null, after: proposal);
                        return Results.Json(response, CoreJsonContext.Default.SessionPromotionResponse);
                    }
                    default:
                    {
                        response = new SessionPromotionResponse
                        {
                            Success = false,
                            Target = request.Target,
                            Error = $"Unsupported session promotion target '{request.Target}'."
                        };
                        RecordOperatorAudit(ctx, operations, auth, "session_promote_invalid", session.Id, response.Error!, success: false, before: null, after: request);
                        return Results.Json(response, CoreJsonContext.Default.SessionPromotionResponse, statusCode: StatusCodes.Status400BadRequest);
                    }
                }
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "session_promote_failed", session.Id, ex.Message, success: false, before: null, after: request);
                return Results.Json(new SessionPromotionResponse
                {
                    Success = false,
                    Target = request.Target,
                    Error = ex.Message
                }, CoreJsonContext.Default.SessionPromotionResponse, statusCode: StatusCodes.Status400BadRequest);
            }
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
    }
}
