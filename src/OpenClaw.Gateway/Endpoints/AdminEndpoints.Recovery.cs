using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Actions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Recovery;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapRecoveryEndpoints(WebApplication app, AdminEndpointServices services)
    {
        app.MapGet("/admin/sessions/{id}/recovery", async (HttpContext ctx, string id) =>
        {
            var authorization = AuthorizeOperator(ctx, services.Startup, services.BrowserSessions, services.Operations,
                requireCsrf: false, endpointScope: "admin.sessions.recovery");
            if (authorization.Failure is not null) return authorization.Failure;
            var session = await services.Runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null) return Results.NotFound();
            // Avoid waiting behind an active dispatch just to show the stop guidance.
            var running = services.Runtime.AbortRegistry.ActiveSessionIds.Contains(id);
            using var journal = !running && services.Startup.Config.Tooling.DurableActionJournal
                ? await new DurableActionJournal(services.Startup.Config.Memory.StoragePath).OpenAsync(id, ctx.RequestAborted) : null;
            return Results.Json(GuidedRecovery.Describe(session, app.Services.GetService<IGoalService>()?.GetGoal(id), journal?.Records ?? [],
                EndpointHelpers.IsRoleAllowed(authorization.Authorization!.Role, "admin.sessions.recovery.mutate", out _), running,
                services.Runtime.ToolApprovalService.ListPending().Any(a => a.SessionId == id), services.Startup.Config.SessionTokenBudget), RecoveryJsonContext.Default.RecoveryControls);
        });
        app.MapPost("/admin/sessions/{id}/recovery", async (HttpContext ctx, string id) =>
        {
            var authorization = AuthorizeOperator(ctx, services.Startup, services.BrowserSessions, services.Operations,
                requireCsrf: true, endpointScope: "admin.sessions.recovery.mutate");
            if (authorization.Failure is not null) return authorization.Failure;
            var auth = authorization.Authorization!;
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, services.Operations, auth, "admin.control", out _)) return Results.StatusCode(429);
            var body = await EndpointHelpers.TryReadBodyTextAsync(ctx, 100_000, ctx.RequestAborted);
            if (!body.Success) return Results.BadRequest();
            RecoveryRequest? request;
            try { request = JsonSerializer.Deserialize(body.Text, RecoveryJsonContext.Default.RecoveryRequest); }
            catch (JsonException) { return Results.BadRequest(); }
            if (request is null) return Results.BadRequest();
            var redaction = app.Services.GetService<IRedactionPipeline>() ?? new NoopRedactionPipeline();
            request.Evidence = request.Evidence is null ? null : new BaselineSecretRedactor().Redact(redaction.Redact(request.Evidence));
            request.Result = request.Result is null ? null : new BaselineSecretRedactor().Redact(redaction.Redact(request.Result));
            if (services.Runtime.AbortRegistry.ActiveSessionIds.Contains(id)) return Results.Conflict();
            await using var sessionLock = await services.Runtime.SessionManager.AcquireSessionLockAsync(id, ctx.RequestAborted);
            var session = await services.Runtime.SessionManager.LoadAsync(id, ctx.RequestAborted);
            if (session is null) return Results.NotFound();
            using var journal = services.Startup.Config.Tooling.DurableActionJournal
                ? await new DurableActionJournal(services.Startup.Config.Memory.StoragePath).OpenAsync(id, ctx.RequestAborted) : null;
            try
            {
                await GuidedRecovery.ApplyAsync(session, app.Services.GetService<IGoalService>(), services.MemoryStore, journal, request,
                    services.Runtime.AbortRegistry.ActiveSessionIds.Contains(id), services.Runtime.ToolApprovalService.ListPending().Any(a => a.SessionId == id), ctx.RequestAborted, services.Startup.Config.SessionTokenBudget);
                RecordOperatorAudit(ctx, services.Operations, auth, "session_recovery", id,
                    $"Recovery action {request.Command}; action id {request.ActionId ?? "none"}.", true, null, null);
                return Results.Json(new OperationStatusResponse { Success = true, Message = "Recovery state updated. No work was dispatched." }, CoreJsonContext.Default.OperationStatusResponse);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                RecordOperatorAudit(ctx, services.Operations, auth, "session_recovery", id, "Recovery rejected after eligibility/revision check.", false, null, null);
                return Results.Json(new OperationStatusResponse { Success = false, Message = ex.Message }, CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: ex is ArgumentException ? 400 : 409);
            }
        });
    }
}
