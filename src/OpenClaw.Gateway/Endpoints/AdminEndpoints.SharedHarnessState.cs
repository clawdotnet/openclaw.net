using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapSharedHarnessStateEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var sharedState = services.SharedHarnessState;

        app.MapGet("/admin/harness/shared-state", async (
            HttpContext ctx,
            string? sessionId = null,
            string? parentSessionId = null,
            string? harnessContractId = null,
            string? status = null,
            string? tag = null,
            int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new SharedHarnessStateListQuery
            {
                SessionId = sessionId,
                ParentSessionId = parentSessionId,
                HarnessContractId = harnessContractId,
                Status = status,
                Tag = tag,
                CreatedFromUtc = GetQueryDateTimeOffset(ctx.Request, "createdFromUtc"),
                CreatedToUtc = GetQueryDateTimeOffset(ctx.Request, "createdToUtc"),
                Limit = limit
            };
            var items = await sharedState.ListAsync(query, ctx.RequestAborted);
            return Results.Json(
                new SharedHarnessStateListResponse { Items = items },
                CoreJsonContext.Default.SharedHarnessStateListResponse);
        });

        app.MapGet("/admin/harness/shared-state/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var state = await sharedState.GetAsync(id, ctx.RequestAborted);
                if (state is null)
                    return SharedStateNotFound("Shared harness state not found.");

                return Results.Json(
                    new SharedHarnessStateDetailResponse { State = state },
                    CoreJsonContext.Default.SharedHarnessStateDetailResponse);
            }
            catch (ArgumentException ex)
            {
                return BadSharedStateRequest(ex.Message);
            }
        });

        app.MapGet("/admin/sessions/{sessionId}/harness-state", async (HttpContext ctx, string sessionId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var state = await sharedState.GetBySessionAsync(sessionId, ctx.RequestAborted);
                if (state is null)
                    return SharedStateNotFound("Shared harness state not found for session.");

                return Results.Json(
                    new SharedHarnessStateDetailResponse { State = state },
                    CoreJsonContext.Default.SharedHarnessStateDetailResponse);
            }
            catch (ArgumentException ex)
            {
                return BadSharedStateRequest(ex.Message);
            }
        });

        app.MapPost("/admin/harness/shared-state", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.SharedHarnessState);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = false, Error = "Shared harness state payload is required." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await sharedState.CreateAsync(requestPayload.Value, ctx.RequestAborted);
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "shared_harness_state_create",
                    created.Id,
                    $"Created shared harness state '{created.Id}'.",
                    success: true,
                    before: null,
                    after: created);

                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = true, State = created, Message = "Shared harness state created." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "shared_harness_state_create", requestPayload.Value.Id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return BadSharedStateRequest(ex.Message);
            }
        });

        app.MapPost("/admin/harness/shared-state/{id}/participants", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.HarnessParticipant);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = false, Error = "Participant payload is required." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await sharedState.GetAsync(id, ctx.RequestAborted);
                var updated = await sharedState.AddParticipantAsync(id, requestPayload.Value, ctx.RequestAborted);
                if (updated is null)
                    return SharedStateNotFound("Shared harness state not found.");

                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "shared_harness_state_participant_add",
                    updated.Id,
                    $"Added participant to shared harness state '{updated.Id}'.",
                    success: true,
                    before,
                    after: updated);

                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = true, State = updated, Message = "Participant added." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "shared_harness_state_participant_add", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return BadSharedStateRequest(ex.Message);
            }
        });

        app.MapPost("/admin/harness/shared-state/{id}/actions", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.HarnessStateAction);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = false, Error = "Action payload is required." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await sharedState.GetAsync(id, ctx.RequestAborted);
                var updated = await sharedState.AddActionAsync(id, requestPayload.Value, ctx.RequestAborted);
                if (updated is null)
                    return SharedStateNotFound("Shared harness state not found.");

                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "shared_harness_state_action_add",
                    updated.Id,
                    $"Added action to shared harness state '{updated.Id}'.",
                    success: true,
                    before,
                    after: updated);

                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = true, State = updated, Message = "Action added." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "shared_harness_state_action_add", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return BadSharedStateRequest(ex.Message);
            }
        });

        app.MapPost("/admin/harness/shared-state/{id}/detect-conflicts", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            try
            {
                var before = await sharedState.GetAsync(id, ctx.RequestAborted);
                var updated = await sharedState.DetectConflictsAsync(id, ctx.RequestAborted);
                if (updated is null)
                    return SharedStateNotFound("Shared harness state not found.");

                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "shared_harness_state_conflict_detect",
                    updated.Id,
                    $"Detected {updated.Conflicts.Count} shared harness state conflict(s).",
                    success: true,
                    before,
                    after: updated);

                return Results.Json(
                    new SharedHarnessStateMutationResponse { Success = true, State = updated, Message = "Conflict detection completed." },
                    CoreJsonContext.Default.SharedHarnessStateMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "shared_harness_state_conflict_detect", id, ex.Message, success: false, before: null, after: null);
                return BadSharedStateRequest(ex.Message);
            }
        });
    }

    private static IResult SharedStateNotFound(string error)
        => Results.Json(
            new SharedHarnessStateMutationResponse { Success = false, Error = error },
            CoreJsonContext.Default.SharedHarnessStateMutationResponse,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult BadSharedStateRequest(string error)
        => Results.Json(
            new SharedHarnessStateMutationResponse { Success = false, Error = error },
            CoreJsonContext.Default.SharedHarnessStateMutationResponse,
            statusCode: StatusCodes.Status400BadRequest);
}
