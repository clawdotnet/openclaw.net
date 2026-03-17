using System.Text;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class DiagnosticsEndpoints
{
    public static void MapOpenClawDiagnosticsEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();

        app.MapGet("/health", (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(
                new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 },
                CoreJsonContext.Default.HealthResponse);
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

            var wsRoot = EndpointHelpers.ResolveWorkspaceRoot(startup.Config.Tooling.WorkspaceRoot);
            var wsExists = !string.IsNullOrWhiteSpace(wsRoot) && Directory.Exists(wsRoot);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);
            var pluginReports = runtime.PluginReports;
            var pluginHealth = runtime.Operations.PluginHealth.ListSnapshots();
            var routeHealth = runtime.Operations.LlmExecution.SnapshotRoutes();
            const long retentionDisabledWarningThreshold = 2_000;
            string? retentionWarning = null;
            if (!startup.Config.Memory.Retention.Enabled && retentionStatus.StoreStats is not null)
            {
                var totalPersisted = retentionStatus.StoreStats.PersistedSessions + retentionStatus.StoreStats.PersistedBranches;
                if (totalPersisted >= retentionDisabledWarningThreshold)
                {
                    retentionWarning =
                        $"Retention is disabled while persisted sessions+branches={totalPersisted} (threshold={retentionDisabledWarningThreshold}).";
                }
            }

            var report = new
            {
                nowUtc = DateTimeOffset.UtcNow,
                bind = new
                {
                    startup.Config.BindAddress,
                    startup.Config.Port,
                    startup.IsNonLoopbackBind,
                    authEnabled = startup.IsNonLoopbackBind,
                    authTokenConfigured = !string.IsNullOrWhiteSpace(startup.Config.AuthToken)
                },
                tooling = new
                {
                    autonomyMode = (startup.Config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant(),
                    workspaceOnly = startup.Config.Tooling.WorkspaceOnly,
                    workspaceRoot = wsRoot,
                    workspaceRootExists = wsExists,
                    forbiddenPathGlobs = startup.Config.Tooling.ForbiddenPathGlobs,
                    allowedShellCommandGlobs = startup.Config.Tooling.AllowedShellCommandGlobs,
                    runtime.EffectiveRequireToolApproval,
                    runtime.EffectiveApprovalRequiredTools,
                    toolApprovalTimeoutSeconds = startup.Config.Tooling.ToolApprovalTimeoutSeconds
                },
                channels = new
                {
                    allowlistSemantics = startup.Config.Channels.AllowlistSemantics,
                    sms = new { enabled = startup.Config.Channels.Sms.Twilio.Enabled, dmPolicy = startup.Config.Channels.Sms.DmPolicy },
                    telegram = new { enabled = startup.Config.Channels.Telegram.Enabled, dmPolicy = startup.Config.Channels.Telegram.DmPolicy },
                    whatsapp = new { enabled = startup.Config.Channels.WhatsApp.Enabled, dmPolicy = startup.Config.Channels.WhatsApp.DmPolicy }
                },
                allowlists = new
                {
                    sms = new
                    {
                        dynamic = runtime.Allowlists.TryGetDynamic("sms"),
                        effective = runtime.Allowlists.GetEffective("sms", EndpointHelpers.GetConfigAllowlist(startup.Config, "sms"))
                    },
                    telegram = new
                    {
                        dynamic = runtime.Allowlists.TryGetDynamic("telegram"),
                        effective = runtime.Allowlists.GetEffective("telegram", EndpointHelpers.GetConfigAllowlist(startup.Config, "telegram"))
                    },
                    whatsapp = new
                    {
                        dynamic = runtime.Allowlists.TryGetDynamic("whatsapp"),
                        effective = runtime.Allowlists.GetEffective("whatsapp", EndpointHelpers.GetConfigAllowlist(startup.Config, "whatsapp"))
                    }
                },
                recentSenders = new
                {
                    sms = runtime.RecentSenders.GetSnapshot("sms").Senders.Take(10).ToArray(),
                    telegram = runtime.RecentSenders.GetSnapshot("telegram").Senders.Take(10).ToArray(),
                    whatsapp = runtime.RecentSenders.GetSnapshot("whatsapp").Senders.Take(10).ToArray()
                },
                pairing = new
                {
                    approved = runtime.PairingManager.GetApprovedList().ToArray()
                },
                memory = new
                {
                    provider = startup.Config.Memory.Provider,
                    storagePath = startup.Config.Memory.StoragePath,
                    sqlite = new
                    {
                        startup.Config.Memory.Sqlite.DbPath,
                        startup.Config.Memory.Sqlite.EnableFts,
                        startup.Config.Memory.Sqlite.EnableVectors
                    },
                    recall = new
                    {
                        startup.Config.Memory.Recall.Enabled,
                        startup.Config.Memory.Recall.MaxNotes,
                        startup.Config.Memory.Recall.MaxChars
                    },
                    retention = new
                    {
                        startup.Config.Memory.Retention.Enabled,
                        startup.Config.Memory.Retention.RunOnStartup,
                        startup.Config.Memory.Retention.SweepIntervalMinutes,
                        startup.Config.Memory.Retention.SessionTtlDays,
                        startup.Config.Memory.Retention.BranchTtlDays,
                        startup.Config.Memory.Retention.ArchiveEnabled,
                        startup.Config.Memory.Retention.ArchivePath,
                        startup.Config.Memory.Retention.ArchiveRetentionDays,
                        startup.Config.Memory.Retention.MaxItemsPerSweep,
                        status = retentionStatus
                    }
                },
                cron = new
                {
                    enabled = startup.Config.Cron.Enabled,
                    jobs = startup.Config.Cron.Jobs.Select(j => new { j.Name, j.CronExpression, j.ChannelId, j.SessionId, j.RunOnStartup }).ToArray()
                },
                runtime = new
                {
                    requestedMode = startup.RuntimeState.RequestedMode,
                    effectiveMode = startup.RuntimeState.EffectiveModeName,
                    orchestrator = runtime.OrchestratorId,
                    dynamicCodeSupported = startup.RuntimeState.DynamicCodeSupported,
                    circuitBreaker = runtime.AgentRuntime.CircuitBreakerState.ToString(),
                    activeSessions = runtime.SessionManager.ActiveCount
                },
                plugins = new
                {
                    loaded = pluginReports.Count(r => r.Loaded),
                    blockedByMode = pluginReports.Count(r => r.BlockedByRuntimeMode),
                    reports = pluginReports,
                    health = pluginHealth
                },
                skills = new
                {
                    count = runtime.AgentRuntime.LoadedSkillNames.Count,
                    names = runtime.AgentRuntime.LoadedSkillNames.ToArray()
                },
                usage = new
                {
                    providers = runtime.ProviderUsage.Snapshot(),
                    routes = routeHealth,
                    recentTurns = runtime.ProviderUsage.RecentTurns(limit: 25)
                },
                warnings = retentionWarning is null ? Array.Empty<string>() : [retentionWarning]
            };

            return Results.Ok(report);
        });

        app.MapGet("/doctor/text", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false).IsAuthorized)
                return Results.Unauthorized();

            var wsRoot = EndpointHelpers.ResolveWorkspaceRoot(startup.Config.Tooling.WorkspaceRoot);
            var wsExists = !string.IsNullOrWhiteSpace(wsRoot) && Directory.Exists(wsRoot);
            var retentionStatus = await runtime.RetentionCoordinator.GetStatusAsync(ctx.RequestAborted);
            var pluginReports = runtime.PluginReports;
            var pluginHealth = runtime.Operations.PluginHealth.ListSnapshots();
            const long retentionDisabledWarningThreshold = 2_000;
            var persistedScopedItems = retentionStatus.StoreStats is null
                ? 0
                : retentionStatus.StoreStats.PersistedSessions + retentionStatus.StoreStats.PersistedBranches;

            var sb = new StringBuilder();
            sb.AppendLine("OpenClaw.NET Doctor");
            sb.AppendLine($"- time_utc: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"- bind: {startup.Config.BindAddress}:{startup.Config.Port} non_loopback={EndpointHelpers.ToBoolWord(startup.IsNonLoopbackBind)} auth_token_set={EndpointHelpers.ToBoolWord(!string.IsNullOrWhiteSpace(startup.Config.AuthToken))}");
            sb.AppendLine();

            var autonomyMode = (startup.Config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
            sb.AppendLine("Tooling");
            sb.AppendLine($"- autonomy_mode: {autonomyMode}");
            sb.AppendLine($"- workspace_only: {EndpointHelpers.ToBoolWord(startup.Config.Tooling.WorkspaceOnly)}");
            sb.AppendLine($"- workspace_root: {wsRoot} exists={EndpointHelpers.ToBoolWord(wsExists)}");
            sb.AppendLine($"- approvals_required_effective: {EndpointHelpers.ToBoolWord(runtime.EffectiveRequireToolApproval)}");
            sb.AppendLine($"- approval_timeout_seconds: {startup.Config.Tooling.ToolApprovalTimeoutSeconds}");
            sb.AppendLine();

            sb.AppendLine("Runtime");
            sb.AppendLine($"- requested_mode: {startup.RuntimeState.RequestedMode}");
            sb.AppendLine($"- effective_mode: {startup.RuntimeState.EffectiveModeName}");
            sb.AppendLine($"- orchestrator: {runtime.OrchestratorId}");
            sb.AppendLine($"- dynamic_code_supported: {EndpointHelpers.ToBoolWord(startup.RuntimeState.DynamicCodeSupported)}");
            sb.AppendLine();

            sb.AppendLine("Plugins");
            sb.AppendLine($"- loaded: {pluginReports.Count(r => r.Loaded)}");
            sb.AppendLine($"- blocked_by_mode: {pluginReports.Count(r => r.BlockedByRuntimeMode)}");
            foreach (var report in pluginReports.OrderBy(r => r.PluginId, StringComparer.Ordinal))
            {
                var status = report.Loaded ? "loaded" : report.BlockedByRuntimeMode ? "blocked" : "failed";
                var capabilities = report.RequestedCapabilities.Length == 0 ? "-" : string.Join(",", report.RequestedCapabilities);
                sb.AppendLine($"- {report.PluginId}: {status} origin={report.Origin} mode={report.EffectiveRuntimeMode} capabilities={capabilities}");
                if (!string.IsNullOrWhiteSpace(report.BlockedReason))
                    sb.AppendLine($"  reason: {report.BlockedReason}");
                else if (!string.IsNullOrWhiteSpace(report.Error))
                    sb.AppendLine($"  error: {report.Error}");
            }
            if (pluginHealth.Count > 0)
            {
                sb.AppendLine("- health:");
                foreach (var snapshot in pluginHealth.OrderBy(item => item.PluginId, StringComparer.Ordinal))
                    sb.AppendLine($"  - {snapshot.PluginId}: disabled={EndpointHelpers.ToBoolWord(snapshot.Disabled)} quarantined={EndpointHelpers.ToBoolWord(snapshot.Quarantined)} restarts={snapshot.RestartCount}");
            }
            sb.AppendLine();

            sb.AppendLine("Skills");
            sb.AppendLine($"- loaded: {runtime.AgentRuntime.LoadedSkillNames.Count}");
            if (runtime.AgentRuntime.LoadedSkillNames.Count > 0)
                sb.AppendLine($"- names: {string.Join(", ", runtime.AgentRuntime.LoadedSkillNames)}");
            sb.AppendLine();

            sb.AppendLine("Provider Usage");
            foreach (var item in runtime.ProviderUsage.Snapshot())
            {
                sb.AppendLine($"- {item.ProviderId}/{item.ModelId}: requests={item.Requests} retries={item.Retries} errors={item.Errors} tokens={item.InputTokens}in/{item.OutputTokens}out");
            }
            sb.AppendLine("- routes:");
            foreach (var route in runtime.Operations.LlmExecution.SnapshotRoutes())
                sb.AppendLine($"  - {route.ProviderId}/{route.ModelId}: circuit={route.CircuitState} requests={route.Requests} retries={route.Retries} errors={route.Errors}");
            sb.AppendLine();

            sb.AppendLine("Allowlists");
            sb.AppendLine($"- semantics: {startup.Config.Channels.AllowlistSemantics}");
            foreach (var channel in new[] { "telegram", "sms", "whatsapp" })
            {
                var dynamicAllowlist = runtime.Allowlists.TryGetDynamic(channel);
                var effective = runtime.Allowlists.GetEffective(channel, EndpointHelpers.GetConfigAllowlist(startup.Config, channel));
                sb.AppendLine($"- {channel}: dynamic_file={EndpointHelpers.ToBoolWord(dynamicAllowlist is not null)} allowed_from={effective.AllowedFrom.Length} allowed_to={effective.AllowedTo.Length}");
                var latest = runtime.RecentSenders.TryGetLatest(channel);
                if (latest is not null)
                    sb.AppendLine($"  latest_sender: {latest.SenderId} last_seen_utc={latest.LastSeenUtc:O}");
            }
            sb.AppendLine();

            sb.AppendLine("Pairing");
            var approved = runtime.PairingManager.GetApprovedList().ToArray();
            sb.AppendLine($"- approved_pairs: {approved.Length}");
            if (approved.Length > 0)
                sb.AppendLine($"- approved: {string.Join(", ", approved.Take(20))}{(approved.Length > 20 ? ", …" : "")}");
            sb.AppendLine();

            sb.AppendLine("Memory");
            sb.AppendLine($"- provider: {startup.Config.Memory.Provider}");
            sb.AppendLine($"- sqlite_fts: {EndpointHelpers.ToBoolWord(startup.Config.Memory.Sqlite.EnableFts)}");
            sb.AppendLine($"- recall_enabled: {EndpointHelpers.ToBoolWord(startup.Config.Memory.Recall.Enabled)} max_notes={startup.Config.Memory.Recall.MaxNotes} max_chars={startup.Config.Memory.Recall.MaxChars}");
            sb.AppendLine($"- retention_enabled: {EndpointHelpers.ToBoolWord(startup.Config.Memory.Retention.Enabled)} interval_minutes={startup.Config.Memory.Retention.SweepIntervalMinutes} startup_sweep={EndpointHelpers.ToBoolWord(startup.Config.Memory.Retention.RunOnStartup)}");
            sb.AppendLine($"- retention_ttls_days: sessions={startup.Config.Memory.Retention.SessionTtlDays} branches={startup.Config.Memory.Retention.BranchTtlDays}");
            sb.AppendLine($"- retention_archive: enabled={EndpointHelpers.ToBoolWord(startup.Config.Memory.Retention.ArchiveEnabled)} path={startup.Config.Memory.Retention.ArchivePath} ttl_days={startup.Config.Memory.Retention.ArchiveRetentionDays}");
            sb.AppendLine($"- retention_max_items_per_sweep: {startup.Config.Memory.Retention.MaxItemsPerSweep}");
            sb.AppendLine($"- retention_store_support: {EndpointHelpers.ToBoolWord(retentionStatus.StoreSupportsRetention)} backend={retentionStatus.StoreStats?.Backend ?? "n/a"}");
            if (retentionStatus.StoreStats is not null)
            {
                sb.AppendLine($"- persisted_sessions: {retentionStatus.StoreStats.PersistedSessions}");
                sb.AppendLine($"- persisted_branches: {retentionStatus.StoreStats.PersistedBranches}");
            }
            sb.AppendLine($"- retention_last_run_success: {EndpointHelpers.ToBoolWord(retentionStatus.LastRunSucceeded)} duration_ms={retentionStatus.LastRunDurationMs}");
            if (retentionStatus.LastRunStartedAtUtc is not null)
                sb.AppendLine($"- retention_last_run_started_utc: {retentionStatus.LastRunStartedAtUtc:O}");
            if (retentionStatus.LastRunCompletedAtUtc is not null)
                sb.AppendLine($"- retention_last_run_completed_utc: {retentionStatus.LastRunCompletedAtUtc:O}");
            sb.AppendLine($"- retention_totals: runs={retentionStatus.TotalRuns} errors={retentionStatus.TotalSweepErrors} archived={retentionStatus.TotalArchivedItems} deleted={retentionStatus.TotalDeletedItems}");
            if (!string.IsNullOrWhiteSpace(retentionStatus.LastError))
                sb.AppendLine($"- retention_last_error: {retentionStatus.LastError}");

            if (!startup.Config.Memory.Retention.Enabled && persistedScopedItems >= retentionDisabledWarningThreshold)
            {
                sb.AppendLine($"- warning: retention is disabled while persisted sessions+branches={persistedScopedItems} (threshold={retentionDisabledWarningThreshold})");
            }
            sb.AppendLine();

            sb.AppendLine("Cron");
            sb.AppendLine($"- enabled: {EndpointHelpers.ToBoolWord(startup.Config.Cron.Enabled)} jobs={startup.Config.Cron.Jobs.Count}");
            foreach (var job in startup.Config.Cron.Jobs.Take(20))
                sb.AppendLine($"  - {job.Name} cron={job.CronExpression} run_on_startup={EndpointHelpers.ToBoolWord(job.RunOnStartup)} session={job.SessionId}");
            if (startup.Config.Cron.Jobs.Count > 20)
                sb.AppendLine("  - …");
            sb.AppendLine();

            sb.AppendLine("Suggested next steps");
            if (startup.Config.Channels.AllowlistSemantics.Equals("strict", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("- If a sender is blocked, run: POST /allowlists/{channel}/add_latest then retry.");
            else
                sb.AppendLine("- Consider setting OpenClaw:Channels:AllowlistSemantics=strict for safer defaults.");
            if (autonomyMode == "supervised")
                sb.AppendLine("- Approvals: when prompted, reply with `/approve <approvalId> yes` (or use POST /tools/approve).");
            if (startup.Config.Memory.Provider.Equals("file", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("- Consider Memory.Provider=sqlite and Memory.Sqlite.EnableFts=true for faster recall.");
            if (!startup.Config.Memory.Retention.Enabled)
                sb.AppendLine("- Consider enabling OpenClaw:Memory:Retention:Enabled=true and start with POST /memory/retention/sweep?dryRun=true.");
            if (startup.Config.Memory.EnableCompaction)
                sb.AppendLine("- Compaction is enabled; ensure CompactionThreshold remains greater than MaxHistoryTurns.");
            else
                sb.AppendLine("- History compaction remains disabled by default; enable only after validating prompt/summary quality.");

            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });
    }
}
