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
    private static void MapRuntimeEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var modelEvaluationRunner = services.ModelEvaluationRunner;
        var operations = services.Operations;
        var pulse = services.Pulse;

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

        app.MapGet("/tools/approvals/history", (HttpContext ctx, int limit = 50, string? channelId = null, string? senderId = null, string? toolName = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.approvals.history");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = runtime.ApprovalAuditStore.Query(new ApprovalHistoryQuery
            {
                Limit = limit,
                ChannelId = channelId,
                SenderId = senderId,
                ToolName = toolName,
                FromUtc = fromUtc,
                ToUtc = toUtc
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

        app.MapGet("/admin/events", (HttpContext ctx, int limit = 100, string? sessionId = null, string? channelId = null, string? senderId = null, string? component = null, string? action = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null) =>
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
                Action = action,
                FromUtc = fromUtc,
                ToUtc = toUtc
            });

            return Results.Json(new RuntimeEventListResponse { Items = items }, CoreJsonContext.Default.RuntimeEventListResponse);
        });

        app.MapGet("/admin/pulse/status", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.pulse.status");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(pulse.GetStatus(), CoreJsonContext.Default.PulseStatusResponse);
        });

        app.MapGet("/admin/pulse/events", (HttpContext ctx, int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.pulse.events");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = operations.RuntimeEvents.Query(new RuntimeEventQuery
            {
                Limit = limit,
                Component = "runtime_pulse"
            });
            return Results.Json(new RuntimeEventListResponse { Items = items }, CoreJsonContext.Default.RuntimeEventListResponse);
        });

        app.MapPost("/admin/pulse/run", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.pulse.run");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var payload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PulseRunRequest);
            if (payload.Failure is not null)
                return payload.Failure;

            var request = payload.Value ?? new PulseRunRequest();
            var result = string.Equals(request.Mode, "next-heartbeat", StringComparison.OrdinalIgnoreCase)
                ? pulse.EnqueueForNextPulse(request.Text ?? "")
                : await pulse.RunNowAsync(request.Text, ctx.RequestAborted);

            RecordOperatorAudit(ctx, operations, auth, "pulse_manual_wake", "runtime-pulse", $"Runtime Pulse manual wake outcome={result.Outcome}.", result.Success, before: null, after: request);
            return Results.Json(result, CoreJsonContext.Default.PulseRunResponse);
        });

        app.MapPost("/admin/pulse/enable", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.pulse.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = startup.Config.Pulse.Enabled;
            startup.Config.Pulse.Enabled = true;
            RecordOperatorAudit(ctx, operations, auth, "pulse_enable", "runtime-pulse", "Enabled Runtime Pulse for the current gateway process.", success: true, before, after: startup.Config.Pulse.Enabled);
            return Results.Json(pulse.GetStatus(), CoreJsonContext.Default.PulseStatusResponse);
        });

        app.MapPost("/admin/pulse/disable", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.pulse.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = startup.Config.Pulse.Enabled;
            startup.Config.Pulse.Enabled = false;
            RecordOperatorAudit(ctx, operations, auth, "pulse_disable", "runtime-pulse", "Disabled Runtime Pulse for the current gateway process.", success: true, before, after: startup.Config.Pulse.Enabled);
            return Results.Json(pulse.GetStatus(), CoreJsonContext.Default.PulseStatusResponse);
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

        app.MapGet("/admin/audit", (HttpContext ctx, int limit = 100, string? actorId = null, string? actionType = null, string? targetId = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null) =>
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
                    TargetId = targetId,
                    FromUtc = fromUtc,
                    ToUtc = toUtc
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
    }
}
