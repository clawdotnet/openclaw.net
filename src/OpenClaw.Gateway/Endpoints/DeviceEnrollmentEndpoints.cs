using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
namespace OpenClaw.Gateway.Endpoints;

internal static class DeviceEnrollmentEndpoints
{
    public static void MapDeviceEnrollmentEndpoints(this WebApplication app, GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        var service = app.Services.GetRequiredService<DeviceEnrollmentService>();
        var sessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        bool Secure(HttpContext ctx) => ctx.Request.IsHttps || !startup.IsNonLoopbackBind;
        bool TokensEnabled() => app.Services.GetRequiredService<OrganizationPolicyService>().GetSnapshot().AllowedAuthModes.Contains(OrganizationAuthModeNames.AccountToken, StringComparer.OrdinalIgnoreCase);
        app.MapPost("/auth/devices/enroll", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 4096;
            if (!Secure(ctx) || !TokensEnabled()) return Results.StatusCode(403);
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, sessions, requireCsrf: true);
            if (!auth.IsAuthorized) return Results.Unauthorized();
            if (!OperatorRoleNames.CanAccess(auth.Role, OperatorRoleNames.Admin)) return Results.StatusCode(403);
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, runtime.Operations, auth, "admin.operator-accounts.enroll", out _)) return Results.StatusCode(429);
            try
            {
                var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, DeviceEnrollmentJsonContext.Default.DeviceEnrollmentRequest, ctx.RequestAborted);
                if (request is null) return Results.BadRequest();
                var code = service.Create(request);
                runtime.Operations.OperatorAudit.Append(new OperatorAuditEntry
                {
                    Id = "audit_" + Guid.NewGuid().ToString("N"), TimestampUtc = DateTimeOffset.UtcNow,
                    ActorId = auth.AccountId ?? auth.AuthMode, ActorRole = auth.Role, AuthMode = auth.AuthMode,
                    ActionType = "device_enrollment_created", TargetId = request.AccountId,
                    Summary = "Created a five-minute device enrollment code.", Success = true
                });
                return Results.Json(code, DeviceEnrollmentJsonContext.Default.DeviceEnrollmentCode);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException) { return Results.Json(new OperationStatusResponse { Success = false, Error = ex.Message }, CoreJsonContext.Default.OperationStatusResponse, statusCode: 400); }
        });
        app.MapPost("/auth/devices/exchange", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var bodyLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 4096;
            if (!Secure(ctx) || !TokensEnabled()) return Results.StatusCode(403);
            if (ctx.Request.ContentLength is > 1024) return Results.BadRequest();
            if (!runtime.Operations.ActorRateLimits.TryConsumeFixed("ip", EndpointHelpers.GetRemoteIpKey(ctx),
                    "device_enrollment_exchange", 30, TimeSpan.FromMinutes(1))) return Results.StatusCode(429);
            try
            {
                var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, DeviceEnrollmentJsonContext.Default.DeviceEnrollmentExchange, ctx.RequestAborted);
                var result = service.Exchange(request?.Code ?? "");
                if (result is null) return Results.Unauthorized();
                runtime.Operations.OperatorAudit.Append(new OperatorAuditEntry
                {
                    Id = "audit_" + Guid.NewGuid().ToString("N"), TimestampUtc = DateTimeOffset.UtcNow,
                    ActorId = result.Account?.Id ?? "device", ActorRole = result.Account?.Role ?? OperatorRoleNames.Viewer,
                    AuthMode = OrganizationAuthModeNames.AccountToken, ActionType = "device_enrolled",
                    TargetId = result.TokenInfo?.Id ?? "", Summary = "Issued a revocable device token.", Success = true
                });
                return Results.Json(result, CoreJsonContext.Default.OperatorAccountTokenCreateResponse);
            }
            catch (JsonException) { return Results.BadRequest(); }
        });
    }
}
