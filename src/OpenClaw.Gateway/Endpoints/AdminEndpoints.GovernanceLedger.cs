using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapGovernanceLedgerEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var governanceLedger = services.GovernanceLedger;

        app.MapGet("/admin/governance/ledger", async (
            HttpContext ctx,
            string? decision = null,
            string? status = null,
            string? toolName = null,
            string? actionType = null,
            string? riskLevel = null,
            string? scope = null,
            string? sessionId = null,
            string? actorId = null,
            string? channelId = null,
            string? decidedBy = null,
            string? tag = null,
            int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.governance");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new GovernanceLedgerListQuery
            {
                Decision = decision,
                Status = status,
                ToolName = toolName,
                ActionType = actionType,
                RiskLevel = riskLevel,
                Scope = scope,
                SessionId = sessionId,
                ActorId = actorId,
                ChannelId = channelId,
                DecidedBy = decidedBy,
                Tag = tag,
                CreatedFromUtc = GetQueryDateTimeOffset(ctx.Request, "createdFromUtc"),
                CreatedToUtc = GetQueryDateTimeOffset(ctx.Request, "createdToUtc"),
                Limit = limit
            };
            var items = await governanceLedger.ListAsync(query, ctx.RequestAborted);
            return Results.Json(
                new GovernanceLedgerListResponse { Items = items },
                CoreJsonContext.Default.GovernanceLedgerListResponse);
        });

        app.MapGet("/admin/governance/ledger/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.governance");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var entry = await governanceLedger.GetAsync(id, ctx.RequestAborted);
                if (entry is null)
                {
                    return Results.Json(
                        new GovernanceLedgerMutationResponse { Success = false, Error = "Governance ledger entry not found." },
                        CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(
                    new GovernanceLedgerDetailResponse { Entry = entry },
                    CoreJsonContext.Default.GovernanceLedgerDetailResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/governance/ledger", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.governance.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.GovernanceLedgerEntry);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = false, Error = "Governance ledger entry payload is required." },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await governanceLedger.CreateAsync(requestPayload.Value, ctx.RequestAborted);
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "governance_ledger_create",
                    created.Id,
                    $"Created governance ledger entry '{created.Id}'.",
                    success: true,
                    before: null,
                    after: created);

                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = true, Entry = created, Message = "Governance ledger entry created." },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "governance_ledger_create", requestPayload.Value.Id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/governance/ledger/{id}/revoke", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.governance.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.GovernanceLedgerRevokeRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value ?? new GovernanceLedgerRevokeRequest();
            try
            {
                var before = await governanceLedger.GetAsync(id, ctx.RequestAborted);
                var revoked = await governanceLedger.RevokeAsync(
                    id,
                    request.RevokedBy ?? EndpointHelpers.GetOperatorActorId(ctx, auth),
                    request.Reason ?? "revoked by operator",
                    ctx.RequestAborted);
                if (revoked is null)
                {
                    return Results.Json(
                        new GovernanceLedgerMutationResponse { Success = false, Error = "Governance ledger entry not found." },
                        CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "governance_ledger_revoke",
                    revoked.Id,
                    $"Revoked governance ledger entry '{revoked.Id}'.",
                    success: true,
                    before,
                    after: revoked);

                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = true, Entry = revoked, Message = "Governance ledger entry revoked." },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "governance_ledger_revoke", id, ex.Message, success: false, before: null, after: request);
                return Results.Json(
                    new GovernanceLedgerMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.GovernanceLedgerMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }
}
