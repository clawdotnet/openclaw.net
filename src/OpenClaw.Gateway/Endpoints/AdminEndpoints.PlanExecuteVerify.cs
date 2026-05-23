using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapPlanExecuteVerifyEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var pev = services.PlanExecuteVerify;

        app.MapGet("/admin/harness/pev/runs", (
            HttpContext ctx,
            int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new PlanExecuteVerifyRunListResponse { Items = pev.ListRuns(limit) },
                CoreJsonContext.Default.PlanExecuteVerifyRunListResponse);
        });

        app.MapGet("/admin/harness/pev/runs/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var run = pev.GetRun(id);
            if (run is null)
            {
                return Results.Json(
                    new PlanExecuteVerifyRunDetailResponse { Run = null },
                    CoreJsonContext.Default.PlanExecuteVerifyRunDetailResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                new PlanExecuteVerifyRunDetailResponse { Run = run },
                CoreJsonContext.Default.PlanExecuteVerifyRunDetailResponse);
        });

        app.MapPost("/admin/harness/pev/runs/{id}/verify", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = pev.GetRun(id);
            var updated = await pev.VerifyRunAsync(id, ctx.RequestAborted);
            if (updated is null)
            {
                return Results.Json(
                    new PlanExecuteVerifyRunMutationResponse { Success = false, Error = "PEV run not found." },
                    CoreJsonContext.Default.PlanExecuteVerifyRunMutationResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "harness_pev_verify",
                updated.Id,
                $"Verified PEV run '{updated.Id}'.",
                success: true,
                before,
                after: updated);

            return Results.Json(
                new PlanExecuteVerifyRunMutationResponse { Success = true, Run = updated, Message = "PEV run verified." },
                CoreJsonContext.Default.PlanExecuteVerifyRunMutationResponse);
        });

        app.MapPost("/admin/harness/pev/runs/{id}/cancel", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.harness.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var before = pev.GetRun(id);
            var updated = await pev.CancelRunAsync(id, ctx.RequestAborted);
            if (updated is null)
            {
                return Results.Json(
                    new PlanExecuteVerifyRunMutationResponse { Success = false, Error = "PEV run not found." },
                    CoreJsonContext.Default.PlanExecuteVerifyRunMutationResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            RecordOperatorAudit(
                ctx,
                operations,
                authResult.Authorization!,
                "harness_pev_cancel",
                updated.Id,
                $"Cancelled PEV run '{updated.Id}'.",
                success: true,
                before,
                after: updated);

            return Results.Json(
                new PlanExecuteVerifyRunMutationResponse
                {
                    Success = true,
                    Run = updated,
                    Message = "PEV run cancelled."
                },
                CoreJsonContext.Default.PlanExecuteVerifyRunMutationResponse);
        });
    }
}
