using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapConfigurationEndpoints(WebApplication app, AdminEndpointServices services)
    {
        app.MapGet("/admin/configuration", (HttpContext ctx) =>
        {
            var auth = AuthorizeOperator(ctx, services.Startup, services.BrowserSessions, services.Operations,
                requireCsrf: false, endpointScope: "admin.settings.mutate");
            return auth.Failure ?? Results.Json(services.AdminSettings.DescribeConfiguration(), ConfigurationJsonContext.Default.ConfigurationState);
        });
        foreach (var operation in new[] { "preview", "apply" })
        {
            var apply = operation == "apply";
            app.MapPost($"/admin/configuration/{operation}", async (HttpContext ctx) =>
            {
                var auth = AuthorizeOperator(ctx, services.Startup, services.BrowserSessions, services.Operations,
                    requireCsrf: true, endpointScope: "admin.settings.mutate");
                if (auth.Failure is not null) return auth.Failure;
                if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, services.Operations, auth.Authorization!, "admin.control", out _)) return Results.StatusCode(429);
                ConfigurationRequest? request;
                try
                {
                    var body = await ReadJsonBodyAsync(ctx, ConfigurationJsonContext.Default.ConfigurationRequest, 32 * 1024);
                    if (body.Failure is not null) return body.Failure;
                    request = body.Value;
                }
                catch (JsonException) { return Results.BadRequest(); }
                if (request is null || request.Changes is null) return Results.BadRequest();
                if (!apply && !string.IsNullOrWhiteSpace(request.Instruction))
                {
                    if (request.Instruction.Length > 4000) return Results.BadRequest();
                    var state = services.AdminSettings.DescribeConfiguration();
                    if (request.Revision != state.Revision) return Results.Json(new ConfigurationState { Message = "Settings changed. Reload and try again.", Errors = ["Stale revision."] }, ConfigurationJsonContext.Default.ConfigurationState, statusCode: 409);
                    if (services.ModelProfiles is not ConfiguredModelProfileRegistry registry || registry.DefaultProfileId is not { } profileId
                        || !registry.TryGetRegistration(profileId, out var registration) || registration is null || registration.Client is null)
                    {
                        state.Success = false;
                        state.Message = "No model is available. Choose a setting below or complete local setup first.";
                        return Results.Json(state, ConfigurationJsonContext.Default.ConfigurationState);
                    }
                    // Send names and types only. Existing values and credentials never enter the interpretation prompt.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        request.Changes = await ConfigurationPlanner.ProposeAsync(registration.Client,
                            registration.Profile.ModelId, request.Instruction, state, timeout.Token);
                        if (request.Changes.Count == 0)
                        {
                            state.Success = false;
                            state.Message = "I couldn't map that to a setting. Please rephrase or choose a setting below.";
                            return Results.Json(state, ConfigurationJsonContext.Default.ConfigurationState);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
                    {
                        state.Success = false;
                        state.Message = "Could not interpret this request. Retry or edit settings below.";
                        return Results.Json(state, ConfigurationJsonContext.Default.ConfigurationState);
                    }
                }
                var result = services.AdminSettings.ChangeConfiguration(request, apply);
                if (apply)
                    RecordOperatorAudit(ctx, services.Operations, auth.Authorization!, "configuration_apply", "gateway-settings",
                        result.Success ? "Applied reviewed configuration changes: " + string.Join(", ", result.Changes.Keys.Order(StringComparer.Ordinal)) : "Configuration change rejected.", result.Success, before: null, after: null);
                return Results.Json(result, ConfigurationJsonContext.Default.ConfigurationState,
                    statusCode: result.Success ? 200 : request.Revision != result.Revision ? 409 : 400);
            });
        }
    }
}
