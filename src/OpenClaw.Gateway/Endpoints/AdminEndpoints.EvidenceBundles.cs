using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapEvidenceBundleEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var evidenceBundles = services.EvidenceBundles;

        app.MapGet("/admin/harness/evidence", async (
            HttpContext ctx,
            string? sourceSessionId = null,
            string? harnessContractId = null,
            string? learningProposalId = null,
            string? actorId = null,
            string? channelId = null,
            string? confidence = null,
            string? tag = null,
            int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new EvidenceBundleListQuery
            {
                SourceSessionId = sourceSessionId,
                HarnessContractId = harnessContractId,
                LearningProposalId = learningProposalId,
                ActorId = actorId,
                ChannelId = channelId,
                Confidence = confidence,
                Tag = tag,
                CreatedFromUtc = GetQueryDateTimeOffset(ctx.Request, "createdFromUtc"),
                CreatedToUtc = GetQueryDateTimeOffset(ctx.Request, "createdToUtc"),
                Limit = limit
            };
            var items = await evidenceBundles.ListAsync(query, ctx.RequestAborted);
            return Results.Json(
                new EvidenceBundleListResponse { Items = items },
                CoreJsonContext.Default.EvidenceBundleListResponse);
        });

        app.MapGet("/admin/harness/evidence/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var bundle = await evidenceBundles.GetAsync(id, ctx.RequestAborted);
                if (bundle is null)
                {
                    return Results.Json(
                        new EvidenceBundleMutationResponse { Success = false, Error = "Evidence bundle not found." },
                        CoreJsonContext.Default.EvidenceBundleMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(
                    new EvidenceBundleDetailResponse { Bundle = bundle },
                    CoreJsonContext.Default.EvidenceBundleDetailResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/evidence", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.EvidenceBundle);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = "Evidence bundle payload is required." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await evidenceBundles.CreateAsync(requestPayload.Value, ctx.RequestAborted);
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "harness_evidence_create",
                    created.Id,
                    $"Created evidence bundle '{created.Id}'.",
                    success: true,
                    before: null,
                    after: created);

                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = true, Bundle = created, Message = "Evidence bundle created." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_create", requestPayload.Value.Id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/evidence/{id}/items", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.EvidenceItem);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = "Evidence item payload is required." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await evidenceBundles.GetAsync(id, ctx.RequestAborted);
                var updated = await evidenceBundles.AddItemAsync(id, requestPayload.Value, ctx.RequestAborted);
                if (updated is null)
                {
                    return Results.Json(
                        new EvidenceBundleMutationResponse { Success = false, Error = "Evidence bundle not found." },
                        CoreJsonContext.Default.EvidenceBundleMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_item", updated.Id, $"Appended evidence item to bundle '{updated.Id}'.", success: true, before, after: updated);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = true, Bundle = updated, Message = "Evidence item appended." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_item", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/evidence/{id}/checks", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.EvidenceCheck);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = "Evidence check payload is required." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await evidenceBundles.GetAsync(id, ctx.RequestAborted);
                var updated = await evidenceBundles.AddCheckAsync(id, requestPayload.Value, ctx.RequestAborted);
                if (updated is null)
                {
                    return Results.Json(
                        new EvidenceBundleMutationResponse { Success = false, Error = "Evidence bundle not found." },
                        CoreJsonContext.Default.EvidenceBundleMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_check", updated.Id, $"Appended evidence check to bundle '{updated.Id}'.", success: true, before, after: updated);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = true, Bundle = updated, Message = "Evidence check appended." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_check", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/evidence/{id}/reviews", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.EvidenceHumanReview);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = "Evidence review payload is required." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await evidenceBundles.GetAsync(id, ctx.RequestAborted);
                var updated = await evidenceBundles.AddHumanReviewAsync(id, requestPayload.Value, ctx.RequestAborted);
                if (updated is null)
                {
                    return Results.Json(
                        new EvidenceBundleMutationResponse { Success = false, Error = "Evidence bundle not found." },
                        CoreJsonContext.Default.EvidenceBundleMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_review", updated.Id, $"Appended human review to evidence bundle '{updated.Id}'.", success: true, before, after: updated);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = true, Bundle = updated, Message = "Evidence review appended." },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_evidence_review", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new EvidenceBundleMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.EvidenceBundleMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }
}
