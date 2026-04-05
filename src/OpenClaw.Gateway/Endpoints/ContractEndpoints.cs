using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class ContractEndpoints
{
    public static void MapOpenClawContractEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var governance = app.Services.GetRequiredService<ContractGovernanceService>();
        var operatorAudit = app.Services.GetService<OperatorAuditStore>();
        var group = app.MapGroup("/api/contracts").WithTags("OpenClaw Contracts");

        // POST /api/contracts/validate — pre-flight validation without creating
        group.MapPost("/validate", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "contract_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            ContractValidateRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.ContractValidateRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Invalid request body." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Request body is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Build a temporary policy for validation
            var policy = new ContractPolicy
            {
                Id = "validate-only",
                RequiredRuntimeMode = request.RequiredRuntimeMode,
                RequestedTools = request.RequestedTools ?? [],
                ScopedCapabilities = request.ScopedCapabilities ?? [],
                MaxCostUsd = request.MaxCostUsd,
                SoftCostWarningUsd = request.SoftCostWarningUsd,
                MaxTokens = request.MaxTokens,
                MaxToolCalls = request.MaxToolCalls,
                MaxRuntimeSeconds = request.MaxRuntimeSeconds
            };

            var result = governance.ValidatePreFlight(policy, runtime.RegisteredToolNames);
            RecordAudit(ctx, startup, browserSessions, operatorAudit, "contract_validate", "validate-only", "Validated contract policy.");
            return Results.Json(result, CoreJsonContext.Default.ContractValidationResult);
        });

        // POST /api/contracts — create contract and attach to session
        group.MapPost("/", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "contract_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            ContractCreateRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.ContractCreateRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Invalid request body." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Request body is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var sessionId = request.SessionId ?? $"contract:{Guid.NewGuid():N}"[..24];
            var response = governance.CreateContract(request, sessionId, runtime.RegisteredToolNames);

            // If a real session is targeted, attach the contract policy
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                var session = await runtime.SessionManager.LoadAsync(request.SessionId, ctx.RequestAborted);
                if (session is not null)
                {
                    governance.AttachToSession(session, response.Policy);
                    await runtime.SessionManager.PersistAsync(session, ctx.RequestAborted);
                }
            }

            RecordAudit(ctx, startup, browserSessions, operatorAudit, "contract_create", response.Policy.Id, $"Created contract '{response.Policy.Id}'.");

            return Results.Json(response, CoreJsonContext.Default.ContractCreateResponse,
                statusCode: StatusCodes.Status201Created);
        });

        // GET /api/contracts — list contracts
        group.MapGet("/", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "contract_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var sessionId = GetOptionalQueryString(ctx, "sessionId");
            var limit = GetQueryInt(ctx, "limit", 50);

            RecordAudit(ctx, startup, browserSessions, operatorAudit, "contract_list", sessionId ?? "*", "Listed contracts.");
            return Results.Json(governance.ListContracts(sessionId, limit), CoreJsonContext.Default.ContractListResponse);
        });

        // GET /api/contracts/{id} — get single contract
        group.MapGet("/{id}", (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "contract_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var response = governance.GetContract(id);
            if (response is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Contract '{id}' not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            RecordAudit(ctx, startup, browserSessions, operatorAudit, "contract_get", id, $"Viewed contract '{id}'.");
            return Results.Json(response, CoreJsonContext.Default.ContractStatusResponse);
        });

        // POST /api/contracts/{id}/cancel — cancel a contract
        group.MapPost("/{id}/cancel", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "contract_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            // Find the contract snapshot
            var existing = governance.GetContract(id);
            if (existing is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = $"Contract '{id}' not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Emit a cancellation event
            var runtimeEvents = app.Services.GetRequiredService<RuntimeEventStore>();
            runtimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                CorrelationId = id,
                Component = "Contract",
                Action = "cancelled",
                Severity = "info",
                Summary = $"Contract '{id}' cancelled by operator."
            });

            if (governance.CancelContract(id, runtime.SessionManager, out var detachedSession) && detachedSession is not null)
                await runtime.SessionManager.PersistAsync(detachedSession, ctx.RequestAborted);

            RecordAudit(ctx, startup, browserSessions, operatorAudit, "contract_cancel", id, $"Cancelled contract '{id}'.");

            return Results.Json(
                new OperationStatusResponse { Success = true },
                CoreJsonContext.Default.OperationStatusResponse);
        });
    }

    private static IResult? AuthorizeAndConsume(
        HttpContext ctx,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        BrowserSessionAuthService browserSessions,
        string endpointScope,
        bool requireCsrf)
    {
        var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf);
        if (!auth.IsAuthorized)
            return Results.Unauthorized();

        if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, runtime.Operations, auth, endpointScope, out var blockedByPolicyId))
        {
            return Results.Json(
                new OperationStatusResponse
                {
                    Success = false,
                    Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'."
                },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return null;
    }

    private static string? GetOptionalQueryString(HttpContext ctx, string key)
    {
        if (!ctx.Request.Query.TryGetValue(key, out var values))
            return null;

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetQueryInt(HttpContext ctx, string key, int fallback)
    {
        var value = GetOptionalQueryString(ctx, key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static void RecordAudit(
        HttpContext ctx,
        GatewayStartupContext startup,
        BrowserSessionAuthService browserSessions,
        OperatorAuditStore? operatorAudit,
        string actionType,
        string targetId,
        string summary)
    {
        if (operatorAudit is null)
            return;

        var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
        if (!auth.IsAuthorized)
            return;

        operatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActionType = actionType,
            TargetId = targetId,
            Summary = summary,
            Success = true
        });
    }
}
