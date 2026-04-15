using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class IntegrationEndpoints
{
    public static void MapOpenClawIntegrationEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var facade = IntegrationApiFacade.Create(startup, runtime, app.Services);
        var group = app.MapGroup("/api/integration").WithTags("OpenClaw Integration");

        group.MapGet("/dashboard", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                await facade.GetDashboardAsync(ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationDashboardResponse);
        });

        group.MapGet("/status", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(facade.BuildStatusResponse(), CoreJsonContext.Default.IntegrationStatusResponse);
        });

        group.MapGet("/approvals", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var channelId = GetOptionalQueryString(ctx, "channelId");
            var senderId = GetOptionalQueryString(ctx, "senderId");

            return Results.Json(
                facade.GetApprovals(channelId, senderId),
                CoreJsonContext.Default.IntegrationApprovalsResponse);
        });

        group.MapGet("/approval-history", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var limit = GetQueryInt(ctx, "limit", 50);
            var channelId = GetOptionalQueryString(ctx, "channelId");
            var senderId = GetOptionalQueryString(ctx, "senderId");
            var toolName = GetOptionalQueryString(ctx, "toolName");

            return Results.Json(
                facade.GetApprovalHistory(new ApprovalHistoryQuery
                {
                    Limit = limit,
                    ChannelId = channelId,
                    SenderId = senderId,
                    ToolName = toolName
                }),
                CoreJsonContext.Default.IntegrationApprovalHistoryResponse);
        });

        group.MapGet("/providers", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var recentTurnsLimit = GetQueryInt(ctx, "recentTurnsLimit", 50);

            return Results.Json(
                facade.GetProviders(recentTurnsLimit),
                CoreJsonContext.Default.IntegrationProvidersResponse);
        });

        group.MapGet("/plugins", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                facade.GetPlugins(),
                CoreJsonContext.Default.IntegrationPluginsResponse);
        });

        group.MapGet("/compatibility/catalog", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var compatibilityStatus = GetOptionalQueryString(ctx, "compatibilityStatus");
            var kind = GetOptionalQueryString(ctx, "kind");
            var category = GetOptionalQueryString(ctx, "category");

            return Results.Json(
                facade.GetCompatibilityCatalog(compatibilityStatus, kind, category),
                CoreJsonContext.Default.IntegrationCompatibilityCatalogResponse);
        });

        group.MapGet("/operator-audit", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var limit = GetQueryInt(ctx, "limit", 100);
            var actorId = GetOptionalQueryString(ctx, "actorId");
            var actionType = GetOptionalQueryString(ctx, "actionType");
            var targetId = GetOptionalQueryString(ctx, "targetId");

            return Results.Json(
                facade.GetOperatorAudit(new OperatorAuditQuery
                {
                    Limit = limit,
                    ActorId = actorId,
                    ActionType = actionType,
                    TargetId = targetId
                }),
                CoreJsonContext.Default.IntegrationOperatorAuditResponse);
        });

        group.MapGet("/sessions", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var page = GetQueryInt(ctx, "page", 1);
            var pageSize = GetQueryInt(ctx, "pageSize", 50);
            var search = GetOptionalQueryString(ctx, "search");
            var channelId = GetOptionalQueryString(ctx, "channelId");
            var senderId = GetOptionalQueryString(ctx, "senderId");
            var fromUtc = GetQueryDateTimeOffset(ctx, "fromUtc");
            var toUtc = GetQueryDateTimeOffset(ctx, "toUtc");
            var state = GetOptionalQueryString(ctx, "state");
            var starred = GetQueryBool(ctx, "starred");
            var tag = GetOptionalQueryString(ctx, "tag");

            var query = IntegrationApiFacade.BuildSessionQuery(search, channelId, senderId, fromUtc, toUtc, state, starred, tag);
            return Results.Json(
                await facade.ListSessionsAsync(page, pageSize, query, ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationSessionsResponse);
        });

        group.MapGet("/sessions/{id}", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var session = await facade.GetSessionAsync(id, ctx.RequestAborted);
            if (session is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Session not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(session, CoreJsonContext.Default.IntegrationSessionDetailResponse);
        });

        group.MapGet("/sessions/{id}/timeline", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var limit = GetQueryInt(ctx, "limit", 100);

            var timeline = await facade.GetSessionTimelineAsync(id, limit, ctx.RequestAborted);
            if (timeline is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Session not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(timeline, CoreJsonContext.Default.IntegrationSessionTimelineResponse);
        });

        group.MapGet("/session-search", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var text = GetOptionalQueryString(ctx, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "text is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                await facade.SearchSessionsAsync(new SessionSearchQuery
                {
                    Text = text,
                    ChannelId = GetOptionalQueryString(ctx, "channelId"),
                    SenderId = GetOptionalQueryString(ctx, "senderId"),
                    FromUtc = GetQueryDateTimeOffset(ctx, "fromUtc"),
                    ToUtc = GetQueryDateTimeOffset(ctx, "toUtc"),
                    Limit = GetQueryInt(ctx, "limit", 25),
                    SnippetLength = GetQueryInt(ctx, "snippetLength", 180)
                }, ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationSessionSearchResponse);
        });

        group.MapGet("/profiles", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                await facade.ListProfilesAsync(ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationProfilesResponse);
        });

        group.MapPost("/text-to-speech", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            IntegrationTextToSpeechRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.IntegrationTextToSpeechRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "text is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var response = await facade.SynthesizeSpeechAsync(request, ctx.RequestAborted);
                return Results.Json(response, CoreJsonContext.Default.IntegrationTextToSpeechResponse);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapGet("/tool-presets", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                facade.ListToolPresets(),
                CoreJsonContext.Default.IntegrationToolPresetsResponse);
        });

        group.MapGet("/profiles/{actorId}", async (HttpContext ctx, string actorId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var response = await facade.GetProfileAsync(actorId, ctx.RequestAborted);
            if (response.Profile is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Profile not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(response, CoreJsonContext.Default.IntegrationProfileResponse);
        });

        group.MapPut("/profiles/{actorId}", async (HttpContext ctx, string actorId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            IntegrationProfileUpdateRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.IntegrationProfileUpdateRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request?.Profile is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "profile is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                await facade.SaveProfileAsync(actorId, request.Profile, ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationProfileResponse);
        });

        group.MapGet("/automations", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                await facade.ListAutomationsAsync(ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationAutomationsResponse);
        });

        group.MapGet("/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var detail = await facade.GetAutomationAsync(id, ctx.RequestAborted);
            if (detail.Automation is null)
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Automation not found." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(detail, CoreJsonContext.Default.IntegrationAutomationDetailResponse);
        });

        group.MapPost("/automations/{id}/run", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            AutomationRunRequest? request = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    request = await JsonSerializer.DeserializeAsync(
                        ctx.Request.Body,
                        CoreJsonContext.Default.AutomationRunRequest,
                        ctx.RequestAborted);
                }
                catch
                {
                    return Results.Json(
                        new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var result = await facade.RunAutomationAsync(id, request?.DryRun ?? false, ctx.RequestAborted);
            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status202Accepted : StatusCodes.Status404NotFound);
        });

        group.MapGet("/runtime-events", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: false);
            if (failure is not null)
                return failure;

            var limit = GetQueryInt(ctx, "limit", 100);
            var sessionId = GetOptionalQueryString(ctx, "sessionId");
            var channelId = GetOptionalQueryString(ctx, "channelId");
            var senderId = GetOptionalQueryString(ctx, "senderId");
            var component = GetOptionalQueryString(ctx, "component");
            var action = GetOptionalQueryString(ctx, "action");

            var query = new RuntimeEventQuery
            {
                Limit = limit,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId,
                SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId,
                Component = string.IsNullOrWhiteSpace(component) ? null : component,
                Action = string.IsNullOrWhiteSpace(action) ? null : action
            };

            return Results.Json(facade.QueryRuntimeEvents(query), CoreJsonContext.Default.IntegrationRuntimeEventsResponse);
        });

        group.MapPost("/messages", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration_http", requireCsrf: true);
            if (failure is not null)
                return failure;

            IntegrationMessageRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    CoreJsonContext.Default.IntegrationMessageRequest,
                    ctx.RequestAborted);
            }
            catch
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.Json(
                    new OperationStatusResponse { Success = false, Error = "text is required." },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                await facade.QueueMessageAsync(request, ctx.RequestAborted),
                CoreJsonContext.Default.IntegrationMessageResponse,
                statusCode: StatusCodes.Status202Accepted);
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

    private static bool? GetQueryBool(HttpContext ctx, string key)
    {
        var value = GetOptionalQueryString(ctx, key);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? GetQueryDateTimeOffset(HttpContext ctx, string key)
    {
        var value = GetOptionalQueryString(ctx, key);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
