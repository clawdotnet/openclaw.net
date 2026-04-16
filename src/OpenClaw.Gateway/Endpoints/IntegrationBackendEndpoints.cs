using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Backends;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class IntegrationBackendEndpoints
{
    public static void MapOpenClawIntegrationBackendEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var coordinator = app.Services.GetRequiredService<BackendSessionCoordinator>();
        var liveEvents = app.Services.GetRequiredService<BackendSessionEventStreamStore>();
        var group = app.MapGroup("/api/integration/backends").WithTags("OpenClaw Integration");

        group.MapGet("", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(new IntegrationBackendsResponse { Items = coordinator.ListBackends() }, CoreJsonContext.Default.IntegrationBackendsResponse);
        });

        group.MapGet("/{id}", (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            var backend = coordinator.GetBackend(id);
            if (backend is null)
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Backend not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            return Results.Json(new IntegrationBackendResponse { Backend = backend }, CoreJsonContext.Default.IntegrationBackendResponse);
        });

        group.MapPost("/{id}/probe", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            var request = await TryReadJsonAsync(ctx, CoreJsonContext.Default.BackendProbeRequest) ?? new BackendProbeRequest();
            try
            {
                var result = await coordinator.ProbeAsync(id, request, ctx.RequestAborted);
                return Results.Json(result, CoreJsonContext.Default.BackendProbeResult);
            }
            catch (Exception ex)
            {
                return Results.Json(new OperationStatusResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{id}/sessions", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            var request = await TryReadJsonAsync(ctx, CoreJsonContext.Default.StartBackendSessionRequest);
            if (request is null)
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var session = await coordinator.StartSessionAsync(request with { BackendId = id }, ctx.RequestAborted);
                return Results.Json(new IntegrationBackendSessionResponse { Session = session }, CoreJsonContext.Default.IntegrationBackendSessionResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new OperationStatusResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{id}/sessions/{sessionId}/input", async (HttpContext ctx, string id, string sessionId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            var input = await TryReadJsonAsync(ctx, CoreJsonContext.Default.BackendInput);
            if (input is null)
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Invalid JSON request body." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                await coordinator.SendInputAsync(id, sessionId, input, ctx.RequestAborted);
                var session = await coordinator.GetSessionAsync(sessionId, ctx.RequestAborted);
                return Results.Json(new IntegrationBackendSessionResponse { Session = session }, CoreJsonContext.Default.IntegrationBackendSessionResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new OperationStatusResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapDelete("/{id}/sessions/{sessionId}", async (HttpContext ctx, string id, string sessionId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.mutate", requireCsrf: true);
            if (failure is not null)
                return failure;

            await coordinator.StopSessionAsync(id, sessionId, ctx.RequestAborted);
            return Results.Json(new OperationStatusResponse { Success = true, Message = "Session stop requested." }, CoreJsonContext.Default.OperationStatusResponse);
        });

        group.MapGet("/{id}/sessions/{sessionId}", async (HttpContext ctx, string id, string sessionId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            var session = await coordinator.GetSessionAsync(sessionId, ctx.RequestAborted);
            if (session is null || !string.Equals(session.BackendId, id, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Session not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            return Results.Json(new IntegrationBackendSessionResponse { Session = session }, CoreJsonContext.Default.IntegrationBackendSessionResponse);
        });

        group.MapGet("/{id}/sessions/{sessionId}/events", async (HttpContext ctx, string id, string sessionId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            var session = await coordinator.GetSessionAsync(sessionId, ctx.RequestAborted);
            if (session is null || !string.Equals(session.BackendId, id, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Session not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            var afterSequence = GetQueryLong(ctx, "afterSequence", 0);
            var limit = GetQueryInt(ctx, "limit", 100);
            var items = await coordinator.ListEventsAsync(sessionId, afterSequence, limit, ctx.RequestAborted);
            return Results.Json(
                new IntegrationBackendEventsResponse
                {
                    SessionId = sessionId,
                    NextSequence = items.Count == 0 ? afterSequence : items.Max(static item => item.Sequence),
                    Items = items
                },
                CoreJsonContext.Default.IntegrationBackendEventsResponse);
        });

        group.MapGet("/{id}/sessions/{sessionId}/events/stream", async (HttpContext ctx, string id, string sessionId) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.read", requireCsrf: false);
            if (failure is not null)
                return failure;

            var session = await coordinator.GetSessionAsync(sessionId, ctx.RequestAborted);
            if (session is null || !string.Equals(session.BackendId, id, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Session not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            var afterSequence = GetQueryLong(ctx, "afterSequence", 0);
            var limit = GetQueryInt(ctx, "limit", 100);
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            using var subscription = liveEvents.Subscribe();
            var currentItems = await coordinator.ListEventsAsync(sessionId, afterSequence, limit, ctx.RequestAborted);
            await StreamSessionEventsAsync(ctx, session, currentItems, subscription.Reader, afterSequence);

            return Results.Empty;
        });
    }

    internal static async Task StreamSessionEventsAsync(
        HttpContext ctx,
        BackendSessionRecord session,
        IReadOnlyList<BackendEvent> currentItems,
        ChannelReader<BackendEvent> reader,
        long afterSequence)
    {
        var lastSequence = afterSequence;

        try
        {
            await WriteSseCommentAsync(ctx, "stream-open");

            foreach (var item in currentItems)
            {
                await WriteSseEventAsync(ctx, item);
                lastSequence = item.Sequence;
                if (item is BackendSessionCompletedEvent)
                    return;
            }

            if (IsTerminalState(session.State) && lastSequence >= session.LastEventSequence)
                return;

            await foreach (var item in reader.ReadAllAsync(ctx.RequestAborted))
            {
                if (!string.Equals(item.SessionId, session.SessionId, StringComparison.Ordinal))
                    continue;
                if (item.Sequence <= lastSequence)
                    continue;

                await WriteSseEventAsync(ctx, item);
                lastSequence = item.Sequence;
                if (item is BackendSessionCompletedEvent)
                    break;
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static async Task<T?> TryReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (ctx.Request.ContentLength is null or 0)
            return default;

        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted);
        }
        catch
        {
            return default;
        }
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

        if (!EndpointHelpers.IsRoleAllowed(auth.Role, endpointScope, out var requiredRole))
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Endpoint '{endpointScope}' requires role '{requiredRole}'." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, runtime.Operations, auth, endpointScope, out var blockedByPolicyId))
        {
            return Results.Json(
                new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return null;
    }

    private static int GetQueryInt(HttpContext ctx, string key, int fallback)
        => int.TryParse(ctx.Request.Query[key], out var parsed) ? parsed : fallback;

    private static long GetQueryLong(HttpContext ctx, string key, long fallback)
        => long.TryParse(ctx.Request.Query[key], out var parsed) ? parsed : fallback;

    private static async Task WriteSseEventAsync(HttpContext ctx, BackendEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, CoreJsonContext.Default.BackendEvent);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static async Task WriteSseCommentAsync(HttpContext ctx, string comment)
    {
        await ctx.Response.WriteAsync($": {comment}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static bool IsTerminalState(string? state)
        => string.Equals(state, BackendSessionState.Completed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(state, BackendSessionState.Failed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(state, BackendSessionState.Cancelled, StringComparison.OrdinalIgnoreCase);
}
