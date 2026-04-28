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
    private static void MapSetupEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var organizationPolicy = services.OrganizationPolicy;
        var operatorAccounts = services.OperatorAccounts;
        var adminSettings = services.AdminSettings;
        var heartbeat = services.Heartbeat;
        var facade = services.Facade;
        var observability = services.Observability;
        var maintenance = services.Maintenance;
        var operations = services.Operations;
        var providerSmokeRegistry = services.ProviderSmokeRegistry;
        var setupVerificationSnapshots = services.SetupVerificationSnapshots;
        var modelProfiles = services.ModelProfiles;

        app.MapGet("/admin/summary", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.summary");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var persistence = adminSettings.GetPersistence();
            var readiness = MapChannelReadiness(ChannelReadinessEvaluator.Evaluate(startup.Config, startup.IsNonLoopbackBind));
            var settingsWarnings = GetChannelWarnings(readiness);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);
            var pluginHealth = operations.PluginHealth.ListSnapshots();

            var setupStatus = await BuildSetupStatusResponseAsync(
                startup,
                organizationPolicy,
                operatorAccounts,
                modelProfiles,
                providerSmokeRegistry,
                setupVerificationSnapshots,
                maintenance,
                includeReliability: true,
                ctx.RequestAborted);

            var response = new AdminSummaryResponse
            {
                Auth = new AdminSummaryAuth
                {
                    Mode = auth.AuthMode,
                    BrowserSessionActive = auth.UsedBrowserSession
                },
                Runtime = new AdminSummaryRuntime
                {
                    RequestedMode = startup.RuntimeState.RequestedMode,
                    EffectiveMode = startup.RuntimeState.EffectiveModeName,
                    Orchestrator = runtime.OrchestratorId,
                    DynamicCodeSupported = startup.RuntimeState.DynamicCodeSupported,
                    ActiveSessions = runtime.SessionManager.ActiveCount,
                    PendingApprovals = runtime.ToolApprovalService.ListPending().Count,
                    ActiveApprovalGrants = operations.ApprovalGrants.List().Count,
                    LiveSkillCount = runtime.AgentRuntime.LoadedSkillNames.Count,
                    LiveSkillNames = runtime.AgentRuntime.LoadedSkillNames
                },
                Settings = new AdminSummarySettings
                {
                    Persistence = persistence,
                    OverridesActive = persistence.Exists,
                    Warnings = settingsWarnings
                },
                Channels = new AdminSummaryChannels
                {
                    AllowlistSemantics = startup.Config.Channels.AllowlistSemantics,
                    Readiness = readiness
                },
                Retention = new AdminSummaryRetention
                {
                    Enabled = startup.Config.Memory.Retention.Enabled,
                    Status = retentionStatus
                },
                Plugins = new AdminSummaryPlugins
                {
                    Loaded = runtime.PluginReports.Count(static r => r.Loaded),
                    BlockedByMode = runtime.PluginReports.Count(static r => r.BlockedByRuntimeMode),
                    Reports = runtime.PluginReports,
                    Health = pluginHealth
                },
                Usage = new AdminSummaryUsage
                {
                    Providers = runtime.ProviderUsage.Snapshot(),
                    Routes = operations.LlmExecution.SnapshotRoutes(),
                    RecentTurns = runtime.ProviderUsage.RecentTurns(limit: 20)
                },
                Dashboard = await facade.GetOperatorDashboardAsync(setupStatus.Reliability, ctx.RequestAborted),
                Reliability = setupStatus.Reliability
            };

            return Results.Json(response, CoreJsonContext.Default.AdminSummaryResponse);
        });

        app.MapGet("/admin/setup/status", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.setup.status");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                await BuildSetupStatusResponseAsync(
                    startup,
                    organizationPolicy,
                    operatorAccounts,
                    modelProfiles,
                    providerSmokeRegistry,
                    setupVerificationSnapshots,
                    maintenance,
                    includeReliability: true,
                    ctx.RequestAborted),
                CoreJsonContext.Default.SetupStatusResponse);
        });

        app.MapGet("/admin/maintenance", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.observability");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var setupStatus = await BuildSetupStatusResponseAsync(
                startup,
                organizationPolicy,
                operatorAccounts,
                modelProfiles,
                providerSmokeRegistry,
                setupVerificationSnapshots,
                maintenance,
                includeReliability: false,
                ctx.RequestAborted);
            var report = await maintenance.ScanAsync(setupStatus, ctx.RequestAborted);
            return Results.Json(report, CoreJsonContext.Default.MaintenanceReportResponse);
        });

        app.MapPost("/admin/maintenance/fix", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.observability");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.MaintenanceFixRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var setupStatus = await BuildSetupStatusResponseAsync(
                startup,
                organizationPolicy,
                operatorAccounts,
                modelProfiles,
                providerSmokeRegistry,
                setupVerificationSnapshots,
                maintenance,
                includeReliability: false,
                ctx.RequestAborted);
            var result = await maintenance.FixAsync(requestPayload.Value ?? new MaintenanceFixRequest(), setupStatus, ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.MaintenanceFixResponse);
        });

        app.MapGet("/admin/setup/verify", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.setup.status");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var verification = await SetupVerificationService.VerifyAsync(new SetupVerificationRequest
            {
                Config = startup.Config,
                RuntimeState = startup.RuntimeState,
                Policy = organizationPolicy.GetSnapshot(),
                OperatorAccountCount = operatorAccounts.List().Count,
                Offline = false,
                RequireProvider = false,
                WorkspacePath = startup.Config.Tooling.WorkspaceRoot,
                ModelDoctor = ModelDoctorEvaluator.Build(startup.Config, modelProfiles),
                ModelProfiles = modelProfiles,
                ProviderSmokeRegistry = providerSmokeRegistry
            }, ctx.RequestAborted);
            _ = setupVerificationSnapshots.Save(new SetupVerificationSnapshot
            {
                Source = SetupVerificationSources.Admin,
                Offline = false,
                RequireProvider = false,
                Verification = verification
            });

            return Results.Json(verification, CoreJsonContext.Default.SetupVerificationResponse);
        });

        app.MapGet("/admin/observability/summary", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.observability");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var summary = await observability.BuildSummaryAsync(
                GetQueryDateTimeOffset(ctx.Request, "fromUtc"),
                GetQueryDateTimeOffset(ctx.Request, "toUtc"),
                ctx.RequestAborted);
            return Results.Json(summary, CoreJsonContext.Default.ObservabilitySummaryResponse);
        });

        app.MapGet("/admin/insights", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.observability");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var insights = await observability.BuildInsightsAsync(
                GetQueryDateTimeOffset(ctx.Request, "fromUtc"),
                GetQueryDateTimeOffset(ctx.Request, "toUtc"),
                ctx.RequestAborted);
            return Results.Json(insights, CoreJsonContext.Default.OperatorInsightsResponse);
        });

        app.MapGet("/admin/observability/series", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.observability");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var series = await observability.BuildSeriesAsync(
                GetQueryDateTimeOffset(ctx.Request, "fromUtc"),
                GetQueryDateTimeOffset(ctx.Request, "toUtc"),
                GetQueryInt32(ctx.Request, "bucketMinutes") ?? 60,
                ctx.RequestAborted);
            return Results.Json(series, CoreJsonContext.Default.ObservabilitySeriesResponse);
        });

        app.MapGet("/admin/audit/export", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.audit.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var bytes = await observability.ExportAuditBundleAsync(
                GetQueryDateTimeOffset(ctx.Request, "fromUtc"),
                GetQueryDateTimeOffset(ctx.Request, "toUtc"),
                ctx.RequestAborted);
            var fileName = $"openclaw-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(bytes, "application/zip", fileName);
        });

        app.MapGet("/admin/trajectory/export", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.trajectory.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var sessionId = ctx.Request.Query.TryGetValue("sessionId", out var rawSessionId)
                ? rawSessionId.ToString()
                : null;
            var anonymize = GetQueryBool(ctx.Request, "anonymize") ?? false;
            var bytes = await observability.ExportTrajectoryJsonlAsync(
                GetQueryDateTimeOffset(ctx.Request, "fromUtc"),
                GetQueryDateTimeOffset(ctx.Request, "toUtc"),
                sessionId,
                anonymize,
                ctx.RequestAborted);
            var scope = string.IsNullOrWhiteSpace(sessionId) ? "range" : "session";
            var fileName = $"openclaw-trajectory-{scope}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl";
            return Results.File(bytes, "application/x-ndjson", fileName);
        });

        app.MapGet("/admin/posture", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.posture");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(SecurityPostureBuilder.Build(startup, runtime), CoreJsonContext.Default.SecurityPostureResponse);
        });

        app.MapPost("/admin/approvals/simulate", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.approvals.simulate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ApprovalSimulationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null || string.IsNullOrWhiteSpace(request.ToolName))
                return Results.BadRequest(new MutationResponse { Success = false, Error = "toolName is required." });

            var response = await SimulateApprovalAsync(startup, runtime, request, ctx.RequestServices, ctx.RequestAborted);
            return Results.Json(response, CoreJsonContext.Default.ApprovalSimulationResponse);
        });

        app.MapGet("/admin/incident/export", async (HttpContext ctx, int approvalLimit = 100, int eventLimit = 200) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.incident.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            runtime.RuntimeMetrics.SetActiveSessions(runtime.SessionManager.ActiveCount);
            runtime.RuntimeMetrics.SetCircuitBreakerState((int)runtime.AgentRuntime.CircuitBreakerState);
            var posture = SecurityPostureBuilder.Build(startup, runtime);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);

            var response = new IncidentBundleResponse
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Posture = posture,
                Metrics = runtime.RuntimeMetrics.Snapshot(),
                Retention = retentionStatus,
                ApprovalHistory = runtime.ApprovalAuditStore.Query(new ApprovalHistoryQuery { Limit = approvalLimit })
                    .Select(RedactApprovalHistory)
                    .ToArray(),
                ProviderPolicies = operations.ProviderPolicies.List(),
                ProviderRoutes = operations.LlmExecution.SnapshotRoutes(),
                ProviderUsage = runtime.ProviderUsage.Snapshot(),
                RuntimeEvents = operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = eventLimit })
                    .Select(RedactRuntimeEvent)
                    .ToArray(),
                WebhookDeadLetters = operations.WebhookDeliveries.List()
                    .Select(RedactDeadLetter)
                    .ToArray(),
                PluginHealth = operations.PluginHealth.ListSnapshots()
            };

            return Results.Json(response, CoreJsonContext.Default.IncidentBundleResponse);
        });
        app.MapGet("/admin/settings", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.settings");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = BuildSettingsResponse(startup, adminSettings, message: "Settings loaded.");
            return Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse);
        });

        app.MapPost("/admin/settings", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.settings.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var snapshotPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AdminSettingsSnapshot);
            if (snapshotPayload.Failure is not null)
                return snapshotPayload.Failure;

            var snapshot = snapshotPayload.Value;

            if (snapshot is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Settings payload is required."
                });
            }

            var result = adminSettings.Update(snapshot);
            RecordOperatorAudit(ctx, operations, auth, "settings_update", "gateway-settings", result.Success ? "Updated admin settings." : "Admin settings update failed.", result.Success, before: null, after: result.Snapshot);
            var response = BuildSettingsResponse(
                startup,
                adminSettings,
                result.Snapshot,
                result.Persistence,
                result.RestartRequired,
                result.RestartRequiredFields,
                result.Success ? "Settings saved." : "Settings validation failed.",
                result.Errors);

            return result.Success
                ? Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse)
                : Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapDelete("/admin/settings", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.settings.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var result = adminSettings.Reset();
            RecordOperatorAudit(ctx, operations, auth, "settings_reset", "gateway-settings", "Reset admin settings overrides.", success: true, before: null, after: result.Snapshot);
            var response = BuildSettingsResponse(
                startup,
                adminSettings,
                result.Snapshot,
                result.Persistence,
                result.RestartRequired,
                result.RestartRequiredFields,
                "Settings overrides cleared.");
            return Results.Json(response, CoreJsonContext.Default.AdminSettingsResponse);
        });

        app.MapGet("/admin/heartbeat", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.heartbeat");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var preview = await heartbeat.BuildPreviewAsync(heartbeat.LoadConfig(), runtime, ctx.RequestAborted);
            return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapPost("/admin/heartbeat/preview", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.heartbeat.preview");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.HeartbeatConfigDto,
                ctx.RequestAborted);

            if (request is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Heartbeat config payload is required."
                });
            }

            var preview = await heartbeat.BuildPreviewAsync(request, runtime, ctx.RequestAborted);
            return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapPut("/admin/heartbeat", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.heartbeat.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var request = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                CoreJsonContext.Default.HeartbeatConfigDto,
                ctx.RequestAborted);

            if (request is null)
            {
                return Results.BadRequest(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Heartbeat config payload is required."
                });
            }

            var before = heartbeat.LoadConfig();
            var preview = await heartbeat.BuildPreviewAsync(request, runtime, ctx.RequestAborted);
            var hasErrors = preview.Issues.Any(static issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
            if (hasErrors)
            {
                RecordOperatorAudit(ctx, operations, auth, "heartbeat_save", "heartbeat.default", "Heartbeat save rejected by validation.", success: false, before, after: request);
                return Results.Json(preview, CoreJsonContext.Default.HeartbeatPreviewResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var saved = heartbeat.SaveConfig(request);
            var savedPreview = await heartbeat.BuildPreviewAsync(saved, runtime, ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "heartbeat_save", "heartbeat.default", "Saved managed heartbeat configuration.", success: true, before, after: saved);
            return Results.Json(savedPreview, CoreJsonContext.Default.HeartbeatPreviewResponse);
        });

        app.MapGet("/admin/heartbeat/status", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.heartbeat.status");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var status = await heartbeat.BuildStatusAsync(runtime, ctx.RequestAborted);
            return Results.Json(status, CoreJsonContext.Default.HeartbeatStatusResponse);
        });
    }
}
