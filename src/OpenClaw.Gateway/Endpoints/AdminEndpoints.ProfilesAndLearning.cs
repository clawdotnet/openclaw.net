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
    private static void MapProfilesAndLearningEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var profileStore = services.ProfileStore;
        var proposalStore = services.ProposalStore;
        var learningService = services.LearningService;
        var operations = services.Operations;

        app.MapGet("/admin/profiles", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
            return Results.Json(
                new IntegrationProfilesResponse { Items = profiles },
                CoreJsonContext.Default.IntegrationProfilesResponse);
        });

        app.MapGet("/admin/profiles/export", async (HttpContext ctx, string? actorId = null, bool includeProposals = true) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedActorId = string.IsNullOrWhiteSpace(actorId) ? null : actorId.Trim();
            var profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(normalizedActorId))
            {
                profiles = profiles
                    .Where(profile => string.Equals(profile.ActorId, normalizedActorId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            IReadOnlyList<LearningProposal> proposals = [];
            if (includeProposals)
            {
                proposals = await proposalStore.ListProposalsAsync(status: null, kind: null, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    proposals = proposals
                        .Where(item => ProposalMatchesActor(item, normalizedActorId))
                        .ToArray();
                }
            }

            return Results.Json(
                new ProfileExportBundle
                {
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    Profiles = profiles,
                    Proposals = proposals
                },
                CoreJsonContext.Default.ProfileExportBundle);
        });

        app.MapPost("/admin/profiles/import", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.profiles.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.ProfileExportBundle);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var bundle = requestPayload.Value;
            if (bundle is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Profile import payload is required."
                });
            }

            var importedProfiles = 0;
            var importedProposals = 0;

            foreach (var profile in bundle.Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.ActorId)))
            {
                await profileStore.SaveProfileAsync(NormalizeProfile(profile), ctx.RequestAborted);
                importedProfiles++;
            }

            foreach (var proposal in bundle.Proposals.Where(static proposal => !string.IsNullOrWhiteSpace(proposal.Id)))
            {
                await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                importedProposals++;
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "profiles",
                Action = "imported",
                Severity = "info",
                Summary = $"Imported {importedProfiles} profiles and {importedProposals} learning proposals."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "profiles_import",
                string.IsNullOrWhiteSpace(bundle.Profiles.FirstOrDefault()?.ActorId) ? "bulk" : bundle.Profiles.First().ActorId,
                $"Imported {importedProfiles} profiles and {importedProposals} learning proposals.",
                success: true,
                before: null,
                after: new { importedProfiles, importedProposals });

            return Results.Json(
                new ProfileImportResponse
                {
                    Success = true,
                    ProfilesImported = importedProfiles,
                    ProposalsImported = importedProposals,
                    Message = "Profiles imported."
                },
                CoreJsonContext.Default.ProfileImportResponse);
        });

        app.MapGet("/admin/profiles/{actorId}", async (HttpContext ctx, string actorId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.profiles");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var profile = await profileStore.GetProfileAsync(actorId, ctx.RequestAborted);
            if (profile is null)
            {
                return Results.NotFound(new OperationStatusResponse
                {
                    Success = false,
                    Error = "Profile not found."
                });
            }

            return Results.Json(
                new IntegrationProfileResponse { Profile = profile },
                CoreJsonContext.Default.IntegrationProfileResponse);
        });

        app.MapGet("/admin/learning/proposals", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.learning");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var status = ctx.Request.Query.TryGetValue("status", out var statusValues) ? statusValues.ToString() : null;
            var kind = ctx.Request.Query.TryGetValue("kind", out var kindValues) ? kindValues.ToString() : null;
            var items = await learningService.ListAsync(status, kind, ctx.RequestAborted);
            return Results.Json(
                new LearningProposalListResponse { Items = items },
                CoreJsonContext.Default.LearningProposalListResponse);
        });

        app.MapGet("/admin/learning/proposals/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.learning");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var detail = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            if (detail is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            return Results.Json(detail, CoreJsonContext.Default.LearningProposalDetailResponse);
        });

        app.MapPost("/admin/learning/proposals/{id}/approve", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);

            var approved = await learningService.ApproveAsync(id, runtime.AgentRuntime, ctx.RequestAborted);
            if (approved is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            var approvalSucceeded = string.Equals(approved.Status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase);
            var action = approvalSucceeded ? "approved" : "rejected";
            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = approved.ProfileUpdate?.ChannelId ?? approved.AutomationDraft?.DeliveryChannelId,
                SenderId = approved.ProfileUpdate?.SenderId ?? approved.AutomationDraft?.DeliveryRecipientId,
                Component = "learning",
                Action = action,
                Severity = approvalSucceeded ? "info" : "warning",
                Summary = approvalSucceeded
                    ? $"Learning proposal '{approved.Id}' approved."
                    : $"Learning proposal '{approved.Id}' rejected during approval validation."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                approvalSucceeded ? "learning_approve" : "learning_reject",
                approved.Id,
                approvalSucceeded
                    ? $"Approved learning proposal '{approved.Id}'."
                    : $"Rejected learning proposal '{approved.Id}' during approval validation.",
                success: approvalSucceeded,
                before,
                after: approved);

            return Results.Json(approved, CoreJsonContext.Default.LearningProposal);
        });

        app.MapPost("/admin/learning/proposals/{id}/reject", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.LearningProposalReviewRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            var rejected = await learningService.RejectAsync(id, requestPayload.Value?.Reason, ctx.RequestAborted);
            if (rejected is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = rejected.ProfileUpdate?.ChannelId ?? rejected.AutomationDraft?.DeliveryChannelId,
                SenderId = rejected.ProfileUpdate?.SenderId ?? rejected.AutomationDraft?.DeliveryRecipientId,
                Component = "learning",
                Action = "rejected",
                Severity = "info",
                Summary = $"Learning proposal '{rejected.Id}' rejected."
            });
            RecordOperatorAudit(ctx, operations, auth, "learning_reject", rejected.Id, $"Rejected learning proposal '{rejected.Id}'.", success: true, before, after: rejected);

            return Results.Json(rejected, CoreJsonContext.Default.LearningProposal);
        });

        app.MapPost("/admin/learning/proposals/{id}/rollback", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.learning.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.LearningProposalReviewRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var before = await learningService.GetDetailAsync(id, ctx.RequestAborted);
            if (before?.Proposal is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });

            if (!before.CanRollback)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Proposal is not in a rollbackable state."
                });
            }

            var rolledBack = await learningService.RollbackAsync(id, requestPayload.Value?.Reason, runtime.AgentRuntime, ctx.RequestAborted);
            if (rolledBack is null)
                return Results.NotFound(new MutationResponse { Success = false, Error = "Proposal not found." });
            if (!string.Equals(rolledBack.Status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase) ||
                !rolledBack.RolledBack)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Rollback could not be completed. Managed learning metadata was missing, did not match this proposal, or the target artifact no longer exists."
                });
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ChannelId = rolledBack.ProfileUpdate?.ChannelId ?? rolledBack.AutomationDraft?.DeliveryChannelId,
                SenderId = rolledBack.ProfileUpdate?.SenderId ?? rolledBack.AutomationDraft?.DeliveryRecipientId,
                Component = "learning",
                Action = "rolled_back",
                Severity = "warning",
                Summary = $"Learning proposal '{rolledBack.Id}' rolled back."
            });
            RecordOperatorAudit(ctx, operations, auth, "learning_rollback", rolledBack.Id, $"Rolled back learning proposal '{rolledBack.Id}'.", success: true, before, after: rolledBack);

            return Results.Json(rolledBack, CoreJsonContext.Default.LearningProposal);
        });

    }
}
