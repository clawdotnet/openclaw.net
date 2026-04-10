using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Backends;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class AdminBackendEndpoints
{
    public static void MapOpenClawAdminBackendEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var accounts = app.Services.GetRequiredService<ConnectedAccountService>();
        var coordinator = app.Services.GetRequiredService<BackendSessionCoordinator>();
        var resolver = app.Services.GetRequiredService<IBackendCredentialResolver>();

        app.MapPost("/admin/accounts/test-resolution", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "admin.backends", requireCsrf: true);
            if (failure is not null)
                return failure;

            BackendCredentialResolutionRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.BackendCredentialResolutionRequest, ctx.RequestAborted);
            }
            catch
            {
                request = null;
            }

            if (request is null)
                return Results.Json(new BackendCredentialResolutionResponse { Success = false, Error = "Invalid JSON request body." }, CoreJsonContext.Default.BackendCredentialResolutionResponse, statusCode: StatusCodes.Status400BadRequest);

            var provider = request.Provider;
            if (string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(request.BackendId))
                provider = coordinator.GetBackend(request.BackendId)?.Provider;
            if (string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(request.CredentialSource?.ConnectedAccountId))
                provider = (await accounts.GetAsync(request.CredentialSource.ConnectedAccountId, ctx.RequestAborted))?.Provider;
            if (string.IsNullOrWhiteSpace(provider))
                return Results.Json(new BackendCredentialResolutionResponse { Success = false, Error = "provider or backendId is required." }, CoreJsonContext.Default.BackendCredentialResolutionResponse, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var credential = await resolver.ResolveAsync(provider, request.CredentialSource, ctx.RequestAborted);
                return Results.Json(
                    new BackendCredentialResolutionResponse
                    {
                        Success = credential is not null,
                        Error = credential is null ? "No credential source resolved." : null,
                        Credential = credential
                    },
                    CoreJsonContext.Default.BackendCredentialResolutionResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new BackendCredentialResolutionResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.BackendCredentialResolutionResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/admin/backends", (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "admin.backends", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(new IntegrationBackendsResponse { Items = coordinator.ListBackends() }, CoreJsonContext.Default.IntegrationBackendsResponse);
        });

        app.MapGet("/admin/backends/{id}", (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "admin.backends", requireCsrf: false);
            if (failure is not null)
                return failure;

            var backend = coordinator.GetBackend(id);
            if (backend is null)
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Backend not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            return Results.Json(new IntegrationBackendResponse { Backend = backend }, CoreJsonContext.Default.IntegrationBackendResponse);
        });

        app.MapPost("/admin/backends/{id}/probe", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "admin.backends", requireCsrf: true);
            if (failure is not null)
                return failure;

            BackendProbeRequest request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.BackendProbeRequest, ctx.RequestAborted)
                    ?? new BackendProbeRequest();
            }
            catch
            {
                request = new BackendProbeRequest();
            }

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
                new OperationStatusResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return null;
    }
}
