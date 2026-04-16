using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Backends;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class IntegrationAccountEndpoints
{
    public static void MapOpenClawIntegrationAccountEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var accounts = app.Services.GetRequiredService<ConnectedAccountService>();
        var group = app.MapGroup("/api/integration/accounts").WithTags("OpenClaw Integration");

        group.MapGet("", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.accounts", requireCsrf: false);
            if (failure is not null)
                return failure;

            return Results.Json(
                new IntegrationAccountsResponse { Items = (await accounts.ListAsync(ctx.RequestAborted)).Select(RedactAccount).ToArray() },
                CoreJsonContext.Default.IntegrationAccountsResponse);
        });

        group.MapGet("/{id}", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.accounts", requireCsrf: false);
            if (failure is not null)
                return failure;

            var account = await accounts.GetAsync(id, ctx.RequestAborted);
            if (account is null)
                return Results.Json(new OperationStatusResponse { Success = false, Error = "Account not found." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status404NotFound);

            return Results.Json(new IntegrationConnectedAccountResponse { Account = RedactAccount(account) }, CoreJsonContext.Default.IntegrationConnectedAccountResponse);
        });

        group.MapPost("", async (HttpContext ctx) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.accounts", requireCsrf: true);
            if (failure is not null)
                return failure;

            ConnectedAccountCreateRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.ConnectedAccountCreateRequest, ctx.RequestAborted);
            }
            catch
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Provider))
                return Results.Json(new OperationStatusResponse { Success = false, Error = "provider is required." }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var created = await accounts.CreateAsync(request, ctx.RequestAborted);
                return Results.Json(new IntegrationConnectedAccountResponse { Account = RedactAccount(created) }, CoreJsonContext.Default.IntegrationConnectedAccountResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(new OperationStatusResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.OperationStatusResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapDelete("/{id}", async (HttpContext ctx, string id) =>
        {
            var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, "integration.accounts", requireCsrf: true);
            if (failure is not null)
                return failure;

            await accounts.DeleteAsync(id, ctx.RequestAborted);
            return Results.Json(new OperationStatusResponse { Success = true, Message = "Account deleted." }, CoreJsonContext.Default.OperationStatusResponse);
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

    private static ConnectedAccount RedactAccount(ConnectedAccount account)
        => new()
        {
            Id = account.Id,
            Provider = account.Provider,
            DisplayName = account.DisplayName,
            SecretKind = account.SecretKind,
            SecretRef = null,
            EncryptedSecretJson = null,
            TokenFilePath = null,
            Scopes = account.Scopes,
            ExpiresAt = account.ExpiresAt,
            IsActive = account.IsActive,
            Metadata = new Dictionary<string, string>(account.Metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAtUtc = account.CreatedAtUtc,
            UpdatedAtUtc = account.UpdatedAtUtc
        };
}
