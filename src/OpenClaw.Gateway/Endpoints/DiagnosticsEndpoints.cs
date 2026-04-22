using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static class DiagnosticsEndpoints
{
    public static void MapOpenClawDiagnosticsEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var organizationPolicy = app.Services.GetRequiredService<OrganizationPolicyService>();
        var operatorAccounts = app.Services.GetRequiredService<OperatorAccountService>();
        var modelProfiles = app.Services.GetService<IModelProfileRegistry>()
            ?? runtime.Operations.ModelProfiles as IModelProfileRegistry
            ?? new ConfiguredModelProfileRegistry(startup.Config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var providerSmokeRegistry = app.Services.GetService<ProviderSmokeRegistry>()
            ?? new ProviderSmokeRegistry();

        app.MapGet("/health", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(
                new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 },
                CoreJsonContext.Default.HealthResponse);
        });

        // Unauthenticated liveness probe: the process is up. Intended for load balancer / K8s probes.
        app.MapGet("/health/live", () =>
            Results.Json(
                new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 },
                CoreJsonContext.Default.HealthResponse));

        // Unauthenticated readiness probe: the runtime is initialized and can serve traffic.
        // Does not leak config. Returns 503 if the session manager is missing or an internal error occurs.
        app.MapGet("/health/ready", () =>
        {
            try
            {
                _ = runtime.SessionManager.ActiveCount;
                return Results.Json(
                    new HealthResponse { Status = "ready", Uptime = Environment.TickCount64 },
                    CoreJsonContext.Default.HealthResponse);
            }
            catch
            {
                return Results.Json(
                    new HealthResponse { Status = "not_ready", Uptime = Environment.TickCount64 },
                    CoreJsonContext.Default.HealthResponse,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/metrics", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            runtime.RuntimeMetrics.SetActiveSessions(runtime.SessionManager.ActiveCount);
            runtime.RuntimeMetrics.SetCircuitBreakerState((int)runtime.AgentRuntime.CircuitBreakerState);
            return Results.Json(runtime.RuntimeMetrics.Snapshot(), CoreJsonContext.Default.MetricsSnapshot);
        });

        app.MapGet("/metrics/providers", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(runtime.ProviderUsage.Snapshot(), CoreJsonContext.Default.ListProviderUsageSnapshot);
        });

        app.MapGet("/metrics/tools", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var toolTracker = app.Services.GetRequiredService<OpenClaw.Core.Observability.ToolUsageTracker>();
            return Results.Json(toolTracker.Snapshot(), CoreJsonContext.Default.ListToolUsageSnapshot);
        });

        app.MapGet("/admin/audit/tools", (HttpContext ctx, int? limit) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var auditLog = app.Services.GetRequiredService<OpenClaw.Core.Observability.ToolAuditLog>();
            var effectiveLimit = limit is > 0 and <= 1000 ? limit.Value : 100;
            var recent = auditLog.SnapshotRecent(effectiveLimit);
            return Results.Json(recent, CoreJsonContext.Default.IReadOnlyListToolAuditEntry);
        });

        app.MapGet("/memory/retention/status", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var status = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);
            return Results.Json(
                new RetentionStatusResponse { Retention = startup.Config.Memory.Retention, Status = status },
                CoreJsonContext.Default.RetentionStatusResponse);
        });

        app.MapPost("/memory/retention/sweep", async (HttpContext ctx, bool dryRun) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true).IsAuthorized)
                return Results.Unauthorized();

            try
            {
                var result = await runtime.RetentionCoordinator.SweepNowAsync(dryRun, ctx.RequestAborted);
                return Results.Json(
                    new RetentionSweepResponse { Success = true, DryRun = dryRun, Result = result },
                    CoreJsonContext.Default.RetentionSweepResponse);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new RetentionSweepErrorResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.RetentionSweepErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/doctor", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var report = await SetupVerificationService.BuildDoctorReportAsync(new DoctorReportRequest
            {
                Config = startup.Config,
                RuntimeState = startup.RuntimeState,
                Policy = organizationPolicy.GetSnapshot(),
                OperatorAccountCount = operatorAccounts.List().Count,
                Offline = false,
                RequireProvider = false,
                CheckPortAvailability = false,
                WorkspacePath = startup.Config.Tooling.WorkspaceRoot,
                ModelDoctor = ModelDoctorEvaluator.Build(startup.Config, modelProfiles),
                ModelProfiles = modelProfiles,
                ProviderSmokeRegistry = providerSmokeRegistry
            }, ctx.RequestAborted);

            return Results.Json(report, CoreJsonContext.Default.DoctorReportResponse);
        });

        app.MapGet("/doctor/text", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var report = await SetupVerificationService.BuildDoctorReportAsync(new DoctorReportRequest
            {
                Config = startup.Config,
                RuntimeState = startup.RuntimeState,
                Policy = organizationPolicy.GetSnapshot(),
                OperatorAccountCount = operatorAccounts.List().Count,
                Offline = false,
                RequireProvider = false,
                CheckPortAvailability = false,
                WorkspacePath = startup.Config.Tooling.WorkspaceRoot,
                ModelDoctor = ModelDoctorEvaluator.Build(startup.Config, modelProfiles),
                ModelProfiles = modelProfiles,
                ProviderSmokeRegistry = providerSmokeRegistry
            }, ctx.RequestAborted);

            return Results.Text(SetupVerificationService.RenderDoctorText(report), "text/plain; charset=utf-8");
        });
    }
}
