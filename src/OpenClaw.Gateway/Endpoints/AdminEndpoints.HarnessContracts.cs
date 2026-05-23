using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapHarnessContractEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var harnessContracts = services.HarnessContracts;

        app.MapGet("/admin/harness/contracts", async (
            HttpContext ctx,
            string? status = null,
            string? riskLevel = null,
            string? sourceSessionId = null,
            string? actorId = null,
            string? channelId = null,
            string? tag = null,
            int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var query = new HarnessContractListQuery
            {
                Status = status,
                RiskLevel = riskLevel,
                SourceSessionId = sourceSessionId,
                ActorId = actorId,
                ChannelId = channelId,
                Tag = tag,
                CreatedFromUtc = GetQueryDateTimeOffset(ctx.Request, "createdFromUtc"),
                CreatedToUtc = GetQueryDateTimeOffset(ctx.Request, "createdToUtc"),
                Limit = limit
            };
            var items = await harnessContracts.ListAsync(query, ctx.RequestAborted);
            return Results.Json(
                new HarnessContractListResponse { Items = items },
                CoreJsonContext.Default.HarnessContractListResponse);
        });

        app.MapGet("/admin/harness/contracts/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var contract = await harnessContracts.GetAsync(id, ctx.RequestAborted);
                if (contract is null)
                {
                    return Results.Json(
                        new HarnessContractMutationResponse { Success = false, Error = "Harness contract not found." },
                        CoreJsonContext.Default.HarnessContractMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(
                    new HarnessContractDetailResponse { Contract = contract },
                    CoreJsonContext.Default.HarnessContractDetailResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    new HarnessContractMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/contracts", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.HarnessContract);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new HarnessContractMutationResponse { Success = false, Error = "Harness contract payload is required." },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var created = await harnessContracts.CreateAsync(requestPayload.Value, ctx.RequestAborted);
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "harness_contract_create",
                    created.Id,
                    $"Created harness contract '{created.Id}'.",
                    success: true,
                    before: null,
                    after: created);

                return Results.Json(
                    new HarnessContractMutationResponse { Success = true, Contract = created, Message = "Harness contract created." },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_contract_create", requestPayload.Value.Id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new HarnessContractMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/admin/harness/contracts/{id}/status", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.HarnessContractStatusUpdateRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            if (requestPayload.Value is null)
            {
                return Results.Json(
                    new HarnessContractMutationResponse { Success = false, Error = "Status update payload is required." },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var before = await harnessContracts.GetAsync(id, ctx.RequestAborted);
                var updated = await harnessContracts.MarkStatusAsync(id, requestPayload.Value.Status, ctx.RequestAborted);
                if (updated is null)
                {
                    return Results.Json(
                        new HarnessContractMutationResponse { Success = false, Error = "Harness contract not found." },
                        CoreJsonContext.Default.HarnessContractMutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "harness_contract_status",
                    updated.Id,
                    $"Updated harness contract '{updated.Id}' status to '{updated.Status}'.",
                    success: true,
                    before,
                    after: updated);

                return Results.Json(
                    new HarnessContractMutationResponse { Success = true, Contract = updated, Message = "Harness contract status updated." },
                    CoreJsonContext.Default.HarnessContractMutationResponse);
            }
            catch (ArgumentException ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "harness_contract_status", id, ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new HarnessContractMutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.HarnessContractMutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }
}
