using System.Text;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class ControlEndpoints
{
    public static void MapOpenClawControlEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var governanceLedger = FeatureFallbackServices.ResolveGovernanceLedgerService(startup, app.Services);
        var operations = runtime.Operations;

        app.MapPost("/pairing/approve", (HttpContext ctx, string channelId, string senderId, string code) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.control");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (runtime.PairingManager.TryApprove(channelId, senderId, code, out var error))
            {
                AppendAudit(ctx, operations, auth, "pairing_approve", $"{channelId}:{senderId}", "Approved pairing.", true);
                return Results.Json(
                    new PairingApproveResponse
                    {
                        Success = true,
                        Message = "Approved successfully."
                    },
                    CoreJsonContext.Default.PairingApproveResponse);
            }

            if (error.Contains("Too many invalid attempts", StringComparison.Ordinal))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = error },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = error },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapPost("/pairing/revoke", (HttpContext ctx, string channelId, string senderId) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.control");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            runtime.PairingManager.Revoke(channelId, senderId);
            AppendAudit(ctx, operations, auth, "pairing_revoke", $"{channelId}:{senderId}", "Revoked pairing.", true);
            return Results.Json(
                new PairingRevokeResponse { Success = true },
                CoreJsonContext.Default.PairingRevokeResponse);
        });

        app.MapGet("/pairing/list", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(runtime.PairingManager.GetApprovedList().ToList(), CoreJsonContext.Default.ListString);
        });

        app.MapGet("/allowlists/{channelId}", (HttpContext ctx, string channelId) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var cfg = EndpointHelpers.GetConfigAllowlist(startup.Config, channelId);
            var dyn = runtime.Allowlists.TryGetDynamic(channelId);
            var effective = runtime.Allowlists.GetEffective(channelId, cfg);
            return Results.Json(
                new AllowlistSnapshotResponse
                {
                    ChannelId = channelId,
                    Semantics = runtime.AllowlistSemantics.ToString().ToLowerInvariant(),
                    Config = cfg,
                    Dynamic = dyn,
                    Effective = effective
                },
                CoreJsonContext.Default.AllowlistSnapshotResponse);
        });

        app.MapPost("/allowlists/{channelId}/add_latest", (HttpContext ctx, string channelId) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.control");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var latest = runtime.RecentSenders.TryGetLatest(channelId);
            if (latest is null)
                return Results.Json(
                    new SenderMutationResponse { Success = false, Error = "No recent sender found for that channel." },
                    CoreJsonContext.Default.SenderMutationResponse,
                    statusCode: StatusCodes.Status404NotFound);

            runtime.Allowlists.AddAllowedFrom(channelId, latest.SenderId);
            AppendAudit(ctx, operations, auth, "allowlist_add_latest", channelId, $"Added latest sender '{latest.SenderId}' to allowlist for '{channelId}'.", true);
            return Results.Json(
                new SenderMutationResponse { Success = true, SenderId = latest.SenderId },
                CoreJsonContext.Default.SenderMutationResponse);
        });

        app.MapPost("/allowlists/{channelId}/tighten", (HttpContext ctx, string channelId) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.control");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var paired = runtime.PairingManager.GetApprovedList()
                .Select(s =>
                {
                    var idx = s.IndexOf(':', StringComparison.Ordinal);
                    if (idx <= 0 || idx + 1 >= s.Length) return (Channel: "", Sender: "");
                    return (Channel: s[..idx], Sender: s[(idx + 1)..]);
                })
                .Where(t => string.Equals(t.Channel, channelId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Sender))
                .Select(t => t.Sender)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (paired.Length == 0)
                return Results.Json(
                    new CountMutationResponse { Success = false, Error = "No paired senders found for that channel." },
                    CoreJsonContext.Default.CountMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            runtime.Allowlists.SetAllowedFrom(channelId, paired);
            AppendAudit(ctx, operations, auth, "allowlist_tighten", channelId, $"Tightened allowlist for '{channelId}' to {paired.Length} paired sender(s).", true);
            return Results.Json(
                new CountMutationResponse { Success = true, Count = paired.Length },
                CoreJsonContext.Default.CountMutationResponse);
        });

        app.MapPost("/admin/reload-skills", async (HttpContext ctx) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.control");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var loadedSkillNames = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            AppendAudit(ctx, operations, auth, "skills_reload", "skills", $"Reloaded {loadedSkillNames.Count} skill(s).", true);
            return Results.Json(
                new SkillsReloadResponse
                {
                    Reloaded = loadedSkillNames.Count,
                    Skills = loadedSkillNames
                },
                CoreJsonContext.Default.SkillsReloadResponse);
        });

        app.MapPost("/tools/approve", async (HttpContext ctx, string approvalId, bool approved, string? requesterChannelId, string? requesterSenderId) =>
        {
            var authResult = EndpointHelpers.AuthorizeOperatorEndpoint(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approvals.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (string.IsNullOrWhiteSpace(approvalId))
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "approvalId is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            var hasRequesterIdentity =
                !string.IsNullOrWhiteSpace(requesterChannelId) &&
                !string.IsNullOrWhiteSpace(requesterSenderId);

            if (!startup.Config.Security.RequireRequesterMatchForHttpToolApproval && !hasRequesterIdentity)
            {
                if (!OperatorRoleNames.CanAccess(auth.Role, OperatorRoleNames.Admin))
                {
                    return Results.Json(
                        new OperationStatusResponse
                        {
                            Success = false,
                            Error = "Endpoint 'admin.approvals.override' requires role 'admin'."
                        },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var adminOutcome = runtime.ToolApprovalService.TrySetDecisionWithRequest(approvalId, approved, requesterChannelId: null, requesterSenderId: null, requireRequesterMatch: false);
                if (adminOutcome.Result == ToolApprovalDecisionResult.Recorded && adminOutcome.Request is not null)
                {
                    runtime.RuntimeMetrics.IncrementApprovalDecisionsRecorded();
                    runtime.ApprovalAuditStore.RecordDecision(
                        adminOutcome.Request,
                        approved,
                        "http_admin",
                        auth.AuthMode == "browser-session" ? "browser" : "http",
                        auth.AuthMode);
                    if (governanceLedger is not null)
                    {
                        await governanceLedger.TryRecordApprovalDecisionAsync(
                            adminOutcome.Request,
                            approved,
                            GovernanceLedgerSources.ToolApproval,
                            EndpointHelpers.GetOperatorActorId(ctx, auth),
                            auth.AuthMode == "browser-session" ? "browser" : "http",
                            auth.AuthMode,
                            ctx.RequestAborted);
                    }
                    AppendApprovalRuntimeEvent(
                        runtime,
                        adminOutcome.Request,
                        approved,
                        "http_admin",
                        auth.AuthMode == "browser-session" ? "browser" : "http",
                        auth.AuthMode);
                    AppendAudit(ctx, operations, auth, "tool_approval_admin", approvalId, $"Admin {(approved ? "approved" : "denied")} tool approval '{approvalId}'.", true);
                }
                else if (adminOutcome.Result == ToolApprovalDecisionResult.Unauthorized)
                {
                    runtime.RuntimeMetrics.IncrementApprovalDecisionsRejected();
                    AppendRejectedApprovalRuntimeEvent(runtime, adminOutcome.Request, approvalId, "requester_mismatch", "http", auth.AuthMode);
                }

                return adminOutcome.Result == ToolApprovalDecisionResult.Recorded
                    ? Results.Json(
                        new OperationStatusResponse
                        {
                            Success = true,
                            Mode = "admin_override"
                        },
                        CoreJsonContext.Default.OperationStatusResponse)
                    : Results.Json(
                        new OperationStatusResponse
                        {
                            Success = false,
                            Error = "No pending approval found for that id."
                        },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (!hasRequesterIdentity)
            {
                return Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "requesterChannelId and requesterSenderId are required when RequireRequesterMatchForHttpToolApproval=true."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var outcome = runtime.ToolApprovalService.TrySetDecisionWithRequest(
                approvalId,
                approved,
                requesterChannelId,
                requesterSenderId,
                requireRequesterMatch: true);

            if (outcome.Result == ToolApprovalDecisionResult.Recorded && outcome.Request is not null)
            {
                runtime.RuntimeMetrics.IncrementApprovalDecisionsRecorded();
                runtime.ApprovalAuditStore.RecordDecision(
                    outcome.Request,
                    approved,
                    "http_requester",
                    requesterChannelId,
                    requesterSenderId);
                if (governanceLedger is not null)
                {
                    await governanceLedger.TryRecordApprovalDecisionAsync(
                        outcome.Request,
                        approved,
                        GovernanceLedgerSources.ToolApproval,
                        EndpointHelpers.GetOperatorActorId(ctx, auth),
                        requesterChannelId,
                        requesterSenderId,
                        ctx.RequestAborted);
                }
                AppendApprovalRuntimeEvent(
                    runtime,
                    outcome.Request,
                        approved,
                        "http_requester",
                        requesterChannelId,
                        requesterSenderId);
                AppendAudit(ctx, operations, auth, "tool_approval_admin_requester_match", approvalId, $"Admin {(approved ? "approved" : "denied")} tool approval '{approvalId}' with requester-match guard.", true);
            }
            else if (outcome.Result == ToolApprovalDecisionResult.Unauthorized)
            {
                runtime.RuntimeMetrics.IncrementApprovalDecisionsRejected();
                AppendRejectedApprovalRuntimeEvent(runtime, outcome.Request, approvalId, "requester_mismatch", requesterChannelId, requesterSenderId);
            }

            return outcome.Result switch
            {
                ToolApprovalDecisionResult.Recorded => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = true,
                        Mode = startup.Config.Security.RequireRequesterMatchForHttpToolApproval
                            ? "requester_match_required"
                            : "requester_match_optional"
                    },
                    CoreJsonContext.Default.OperationStatusResponse),
                ToolApprovalDecisionResult.Unauthorized => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "Requester does not match the pending approval owner for this admin approval request."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "No pending approval found for that id."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound)
            };
        });
    }

    private static void AppendAudit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        EndpointHelpers.OperatorAuthorizationResult auth,
        string actionType,
        string targetId,
        string summary,
        bool success)
    {
        operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActionType = actionType,
            TargetId = targetId,
            Summary = summary,
            Success = success
        });
    }

    private static void AppendApprovalRuntimeEvent(
        GatewayAppRuntime runtime,
        ToolApprovalRequest request,
        bool approved,
        string decisionSource,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = request.ApprovalId,
            ["toolName"] = request.ToolName,
            ["approved"] = approved ? "true" : "false",
            ["decisionSource"] = decisionSource
        };

        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "decision_recorded",
            Severity = "info",
            Summary = $"{decisionSource} {(approved ? "approved" : "denied")} tool approval '{request.ApprovalId}'.",
            Metadata = metadata
        });
    }

    private static void AppendRejectedApprovalRuntimeEvent(
        GatewayAppRuntime runtime,
        ToolApprovalRequest? request,
        string approvalId,
        string reason,
        string? actorChannelId,
        string? actorSenderId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["approvalId"] = approvalId,
            ["reason"] = reason
        };

        if (request is not null)
            metadata["toolName"] = request.ToolName;
        if (!string.IsNullOrWhiteSpace(actorChannelId))
            metadata["actorChannelId"] = actorChannelId;
        if (!string.IsNullOrWhiteSpace(actorSenderId))
            metadata["actorSenderId"] = actorSenderId;

        runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request?.SessionId,
            ChannelId = request?.ChannelId,
            SenderId = request?.SenderId,
            Component = "approval",
            Action = "decision_rejected",
            Severity = "warning",
            Summary = $"Rejected approval decision attempt for '{approvalId}'.",
            Metadata = metadata
        });
    }
}
