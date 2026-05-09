using OpenClaw.Core.ExternalCli;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapExternalCliEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var registry = services.ExternalCliRegistry;
        var runner = services.ExternalCliRunner;
        var audit = services.ExternalCliAudit;
        var events = services.ExternalCliEvents;

        app.MapGet("/admin/external-cli/connectors", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.external-cli");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ExternalCliConnectorListResponse
            {
                Items = registry.ListConnectors()
            }, CoreJsonContext.Default.ExternalCliConnectorListResponse);
        });

        app.MapGet("/admin/external-cli/connectors/{connector}", async (HttpContext ctx, string connector) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.external-cli");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                var status = await registry.GetStatusAsync(connector, ctx.RequestAborted);
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = "connector_status_checked",
                    Severity = "info",
                    Summary = $"External CLI connector '{connector}' status checked.",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connector"] = connector
                    }
                });
                return Results.Json(status, CoreJsonContext.Default.ExternalCliConnectorStatus);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
        });

        app.MapGet("/admin/external-cli/connectors/{connector}/commands", (HttpContext ctx, string connector) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.external-cli");
            if (authResult.Failure is not null)
                return authResult.Failure;

            try
            {
                return Results.Json(registry.ListCommands(connector), CoreJsonContext.Default.ExternalCliCommandListResponse);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
        });

        app.MapPost("/admin/external-cli/preview", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.external-cli.preview");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ExternalCliPreviewRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value ?? new ExternalCliPreviewRequest();
            try
            {
                var prepared = registry.BuildPreview(request, request.ExecuteDryRun);
                ExternalCliExecutionResult? dryRunResult = null;
                if (request.ExecuteDryRun)
                {
                    dryRunResult = await runner.ExecuteAsync(prepared, ctx.RequestAborted);
                    RecordExternalCliAudit(audit, dryRunResult, auth, ctx, approvalFingerprint: null);
                }

                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = request.ExecuteDryRun ? "dry_run_executed" : "command_previewed",
                    Severity = dryRunResult is { Success: false } ? "warning" : "info",
                    Summary = request.ExecuteDryRun
                        ? $"Dry-run executed for external CLI command {prepared.ConnectorName}/{prepared.CommandName}."
                        : $"Previewed external CLI command {prepared.ConnectorName}/{prepared.CommandName}.",
                    Metadata = BuildEventMetadata(prepared.Preview)
                });

                return Results.Json(new ExternalCliPreviewResponse
                {
                    Preview = prepared.Preview,
                    DryRunResult = dryRunResult
                }, CoreJsonContext.Default.ExternalCliPreviewResponse);
            }
            catch (InvalidOperationException ex)
            {
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = "command_blocked_by_policy",
                    Severity = "warning",
                    Summary = ex.Message
                });
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = "command_blocked_by_policy",
                    Severity = "warning",
                    Summary = ex.Message
                });
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
        });

        app.MapPost("/admin/external-cli/execute", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.external-cli.execute");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ExternalCliExecuteRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value ?? new ExternalCliExecuteRequest();
            try
            {
                var prepared = registry.BuildPreview(new ExternalCliPreviewRequest
                {
                    Connector = request.Connector,
                    Command = request.Command,
                    Parameters = request.Parameters
                }, dryRun: false);

                if (prepared.Preview.RequiresApproval &&
                    !string.Equals(request.ApprovedFingerprint, prepared.Preview.Fingerprint, StringComparison.Ordinal))
                {
                    return Results.Json(
                        new OperationStatusResponse
                        {
                            Success = false,
                            Error = "External CLI command requires approval. Preview the command and resubmit with the matching approved_fingerprint."
                        },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status409Conflict);
                }

                var result = await runner.ExecuteAsync(prepared, ctx.RequestAborted);
                RecordExternalCliAudit(audit, result, auth, ctx, request.ApprovedFingerprint);
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "external_cli_execute",
                    $"{prepared.ConnectorName}/{prepared.CommandName}",
                    $"Executed external CLI command {prepared.ConnectorName}/{prepared.CommandName} with exit code {result.ExitCode}.",
                    success: result.Success,
                    before: null,
                    after: new { prepared.Preview.Fingerprint, result.ExitCode, result.TimedOut });
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = result.TimedOut ? "command_timed_out" : result.Success ? "command_executed" : "command_failed",
                    Severity = result.Success ? "info" : "warning",
                    Summary = $"External CLI command {prepared.ConnectorName}/{prepared.CommandName} completed with exit code {result.ExitCode}.",
                    Metadata = BuildEventMetadata(prepared.Preview)
                });

                return Results.Json(result, CoreJsonContext.Default.ExternalCliExecutionResult);
            }
            catch (InvalidOperationException ex)
            {
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = "command_blocked_by_policy",
                    Severity = "warning",
                    Summary = ex.Message
                });
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                events.Record(new ExternalCliRuntimeEvent
                {
                    Action = "command_blocked_by_policy",
                    Severity = "warning",
                    Summary = ex.Message
                });
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = ex.Message });
            }
        });
    }

    private static void RecordExternalCliAudit(
        IExternalCliAuditSink audit,
        ExternalCliExecutionResult result,
        EndpointHelpers.OperatorAuthorizationResult auth,
        HttpContext ctx,
        string? approvalFingerprint)
    {
        audit.Record(new ExternalCliAuditEntry
        {
            Id = $"ecli_{Guid.NewGuid():N}"[..21],
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = "admin",
            ChannelId = "admin",
            SenderId = auth.AccountId ?? auth.Username ?? EndpointHelpers.GetRemoteIpKey(ctx),
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            Connector = result.Preview.Connector,
            Command = result.Preview.Command,
            Executable = result.Preview.Executable,
            ArgsHash = ExternalCliConnectorRegistry.ComputeArgsHash(result.Preview.Arguments.ToArray()),
            RedactedArgsPreview = result.Preview.RedactedCommandLine,
            ParametersHash = result.Preview.ParametersHash,
            ApprovalFingerprint = approvalFingerprint,
            ExitCode = result.ExitCode,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Failed = !result.Success,
            StdoutTruncated = result.StdoutTruncated,
            StderrTruncated = result.StderrTruncated,
            RiskLevel = result.Preview.RiskLevel,
            ReadOnly = result.Preview.ReadOnly,
            WorkingDirectory = result.Preview.WorkingDirectory
        });
    }

    private static Dictionary<string, string> BuildEventMetadata(ExternalCliInvocationPreview preview)
        => new(StringComparer.Ordinal)
        {
            ["connector"] = preview.Connector,
            ["command"] = preview.Command,
            ["riskLevel"] = preview.RiskLevel,
            ["readOnly"] = preview.ReadOnly.ToString().ToLowerInvariant(),
            ["requiresApproval"] = preview.RequiresApproval.ToString().ToLowerInvariant(),
            ["fingerprint"] = preview.Fingerprint
        };
}
