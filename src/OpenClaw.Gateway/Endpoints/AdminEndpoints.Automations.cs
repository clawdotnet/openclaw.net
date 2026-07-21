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
    private static void MapAutomationEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var automationService = services.AutomationService;
        var facade = services.Facade;
        var operations = services.Operations;

        app.MapGet("/admin/automations", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new IntegrationAutomationsResponse
                {
                    Items = await automationService.ListAsync(ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationsResponse);
        });

        app.MapGet("/admin/automations/templates", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                facade.ListAutomationTemplates(),
                CoreJsonContext.Default.AutomationTemplateListResponse);
        });

        app.MapPost("/admin/automations/migrate", async (HttpContext ctx, bool apply = false) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.migrate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var migrated = await automationService.MigrateLegacyAsync(apply, ctx.RequestAborted);
            return Results.Json(
                new IntegrationAutomationsResponse { Items = migrated },
                CoreJsonContext.Default.IntegrationAutomationsResponse);
        });

        app.MapPost("/admin/automations/preview", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.preview");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationDefinition);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var automation = requestPayload.Value;
            if (automation is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Automation payload is required." });

            return Results.Json(
                automationService.BuildPreview(automation),
                CoreJsonContext.Default.AutomationPreview);
        });

        app.MapGet("/admin/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations.detail");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var automation = await automationService.GetAsync(id, ctx.RequestAborted);
            if (automation is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Automation not found." });

            return Results.Json(
                new IntegrationAutomationDetailResponse
                {
                    Automation = automation,
                    RunState = await automationService.GetRunStateAsync(id, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationDetailResponse);
        });

        app.MapGet("/admin/automations/{id}/runs", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations.detail");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var automation = await automationService.GetAsync(id, ctx.RequestAborted);
            if (automation is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Automation not found." });

            return Results.Json(
                new IntegrationAutomationRunsResponse
                {
                    AutomationId = id,
                    RunState = await automationService.GetRunStateAsync(id, ctx.RequestAborted),
                    Items = await automationService.ListRunRecordsAsync(id, 50, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationRunsResponse);
        });

        app.MapGet("/admin/automations/{id}/runs/{runId}", async (HttpContext ctx, string id, string runId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.automations.detail");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var automation = await automationService.GetAsync(id, ctx.RequestAborted);
            if (automation is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Automation not found." });

            var run = await automationService.GetRunRecordAsync(id, runId, ctx.RequestAborted);
            if (run is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Automation run not found." });

            return Results.Json(
                new IntegrationAutomationRunDetailResponse
                {
                    AutomationId = id,
                    Automation = automation,
                    RunState = await automationService.GetRunStateAsync(id, ctx.RequestAborted),
                    Run = run
                },
                CoreJsonContext.Default.IntegrationAutomationRunDetailResponse);
        });

        app.MapPut("/admin/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationDefinition);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var automation = requestPayload.Value;
            if (automation is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Automation payload is required." });

            var saved = await automationService.SaveAsync(new AutomationDefinition
            {
                Id = id,
                Name = automation.Name,
                Enabled = automation.Enabled,
                Schedule = automation.Schedule,
                Timezone = automation.Timezone,
                Prompt = automation.Prompt,
                ModelId = automation.ModelId,
                RunOnStartup = automation.RunOnStartup,
                SessionId = automation.SessionId,
                DeliveryChannelId = automation.DeliveryChannelId,
                DeliveryRecipientId = automation.DeliveryRecipientId,
                DeliverySubject = automation.DeliverySubject,
                Tags = automation.Tags,
                IsDraft = automation.IsDraft,
                Source = automation.Source,
                TemplateKey = automation.TemplateKey,
                Verification = automation.Verification,
                RetryPolicy = automation.RetryPolicy,
                CreatedAtUtc = automation.CreatedAtUtc,
                UpdatedAtUtc = automation.UpdatedAtUtc
            }, ctx.RequestAborted);

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                SessionId = saved.SessionId,
                ChannelId = saved.DeliveryChannelId,
                SenderId = saved.DeliveryRecipientId,
                Component = "automations",
                Action = "saved",
                Severity = "info",
                Summary = $"Automation '{saved.Id}' saved."
            });

            return Results.Json(
                new IntegrationAutomationDetailResponse
                {
                    Automation = saved,
                    RunState = await automationService.GetRunStateAsync(saved.Id, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationDetailResponse);
        });

        app.MapPost("/admin/automations/{id}/run", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.run");
            if (authResult.Failure is not null)
                return authResult.Failure;

            AutomationRunRequest? request = null;
            if (ctx.Request.ContentLength is > 0)
            {
                var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationRunRequest);
                if (requestPayload.Failure is not null)
                    return requestPayload.Failure;
                request = requestPayload.Value;
            }

            var result = await facade.RunAutomationAsync(id, request?.DryRun ?? false, ctx.RequestAborted);
            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status202Accepted : StatusCodes.Status400BadRequest);
        });

        app.MapPost("/admin/automations/{id}/runs/{runId}/replay", async (HttpContext ctx, string id, string runId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.run");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var result = await facade.ReplayAutomationRunAsync(id, runId, ctx.RequestAborted);
            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status202Accepted : StatusCodes.Status400BadRequest);
        });

        app.MapPost("/admin/automations/{id}/quarantine/clear", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var result = await facade.ClearAutomationQuarantineAsync(id, ctx.RequestAborted);
            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });

        app.MapDelete("/admin/automations/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = await automationService.GetAsync(id, ctx.RequestAborted);
            var result = await facade.DeleteAutomationAsync(id, ctx.RequestAborted);
            if (result.Success)
            {
                RecordOperatorAudit(ctx, operations, auth, "automation_delete", id, $"Deleted automation '{id}'.", success: true, before, after: null);
            }
            else
            {
                RecordOperatorAudit(ctx, operations, auth, "automation_delete", id, result.Error ?? "Automation delete failed.", success: false, before, after: null);
            }

            return Results.Json(
                result,
                CoreJsonContext.Default.MutationResponse,
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });

        app.MapPost("/admin/automations", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.automations.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new MutationResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.MutationResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AutomationDefinition);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var automation = requestPayload.Value;
            if (automation is null || string.IsNullOrWhiteSpace(automation.Name))
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Automation name is required." });

            var newId = Guid.NewGuid().ToString("N")[..16];
            var saved = await automationService.SaveAsync(new AutomationDefinition
            {
                Id = newId,
                Name = automation.Name,
                Enabled = automation.Enabled,
                Schedule = automation.Schedule,
                Timezone = automation.Timezone,
                Prompt = automation.Prompt,
                ModelId = automation.ModelId,
                RunOnStartup = automation.RunOnStartup,
                SessionId = automation.SessionId,
                DeliveryChannelId = automation.DeliveryChannelId,
                DeliveryRecipientId = automation.DeliveryRecipientId,
                DeliverySubject = automation.DeliverySubject,
                Tags = automation.Tags,
                IsDraft = automation.IsDraft,
                Source = automation.Source,
                TemplateKey = automation.TemplateKey,
                Verification = automation.Verification,
                RetryPolicy = automation.RetryPolicy,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, ctx.RequestAborted);

            RecordOperatorAudit(ctx, operations, auth, "automation_create", saved.Id, $"Created automation '{saved.Name}' ({saved.Id}).", true, before: null, after: null);
            return Results.Json(
                new IntegrationAutomationDetailResponse
                {
                    Automation = saved,
                    RunState = await automationService.GetRunStateAsync(saved.Id, ctx.RequestAborted)
                },
                CoreJsonContext.Default.IntegrationAutomationDetailResponse,
                statusCode: StatusCodes.Status201Created);
        });
    }
}
