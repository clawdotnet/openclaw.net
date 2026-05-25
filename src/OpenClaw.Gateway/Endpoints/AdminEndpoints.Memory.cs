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
    private static void MapMemoryEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var adminSettings = services.AdminSettings;
        var memoryStore = services.MemoryStore;
        var memorySearch = services.MemorySearch;
        var memoryCatalog = services.MemoryCatalog;
        var structuredMemoryProvider = services.StructuredMemoryProvider;
        var profileStore = services.ProfileStore;
        var proposalStore = services.ProposalStore;
        var automationService = services.AutomationService;
        var operations = services.Operations;

        app.MapGet("/admin/memory/fractal/status", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = structuredMemoryProvider is null
                ? BuildUnavailableFractalStatus(startup.Config, "Structured memory provider is not registered in this runtime.")
                : await structuredMemoryProvider.GetStatusAsync(ctx.RequestAborted);
            return Results.Json(response, CoreJsonContext.Default.StructuredMemoryStatusResponse);
        });

        app.MapGet("/admin/memory/fractal/search", async (HttpContext ctx, string? query, int limit = 10, string? scope = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedQuery = NormalizeOptionalValue(query);
            var normalizedScope = NormalizeOptionalValue(scope);
            var normalizedLimit = Math.Clamp(limit, 1, 50);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
                return Results.Json(new StructuredMemorySearchResult { Success = false, Query = "", Scope = normalizedScope, Error = "query is required." }, CoreJsonContext.Default.StructuredMemorySearchResult, statusCode: StatusCodes.Status400BadRequest);

            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemorySearchResult { Success = false, Query = normalizedQuery, Scope = normalizedScope, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemorySearchResult);

            var result = await structuredMemoryProvider.SearchAsync(normalizedQuery, normalizedLimit, normalizedScope, ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemorySearchResult);
        });

        app.MapGet("/admin/memory/fractal/open", async (HttpContext ctx, string? path, int? depth = null, string? view = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedPath = NormalizeOptionalValue(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
                return Results.Json(new StructuredMemoryOpenResult { Success = false, Path = "", Error = "path is required." }, CoreJsonContext.Default.StructuredMemoryOpenResult, statusCode: StatusCodes.Status400BadRequest);

            var fractal = startup.Config.Memory.Fractal;
            var normalizedDepth = Math.Clamp(depth ?? fractal.DefaultDepth, 0, 3);
            var normalizedView = NormalizeFractalMemoryView(view, fractal.DefaultView);
            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryOpenResult { Success = false, Path = normalizedPath, Depth = normalizedDepth, View = normalizedView, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryOpenResult);

            var result = await structuredMemoryProvider.OpenAsync(normalizedPath, normalizedDepth, normalizedView, ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryOpenResult);
        });

        app.MapGet("/admin/memory/fractal/export", async (HttpContext ctx, string path, string? mode = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryExportResult { Success = false, Path = path ?? "", Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryExportResult);

            var result = await structuredMemoryProvider.ExportAsync(path, mode ?? startup.Config.Memory.Fractal.DefaultExportMode, ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryExportResult);
        });

        app.MapGet("/admin/memory/fractal/recent", async (HttpContext ctx, int days = 30, int limit = 10, string? scope = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedDays = Math.Clamp(days, 1, 3650);
            var normalizedLimit = Math.Clamp(limit, 1, 100);
            var normalizedScope = NormalizeOptionalValue(scope);
            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryRecentResult { Success = false, Days = normalizedDays, Scope = normalizedScope, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryRecentResult);

            var result = await structuredMemoryProvider.RecentAsync(normalizedDays, normalizedLimit, normalizedScope, ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryRecentResult);
        });

        app.MapPost("/admin/memory/fractal/validate", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryValidationResult { Success = false, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryValidationResult);

            var result = await structuredMemoryProvider.ValidateAsync(ctx.RequestAborted);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryValidationResult);
        });

        app.MapPost("/admin/memory/fractal/index/refresh", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (!startup.Config.Memory.Fractal.AllowWrites)
                return Results.Json(new StructuredMemoryValidationResult { Success = false, Error = "Fractal Memory writes are disabled by configuration." }, CoreJsonContext.Default.StructuredMemoryValidationResult, statusCode: StatusCodes.Status403Forbidden);
            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryValidationResult { Success = false, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryValidationResult);

            var result = await structuredMemoryProvider.RefreshIndexAsync(ctx.RequestAborted);
            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "fractal_memory",
                Action = "index_refresh",
                Severity = result.Success ? "info" : "warning",
                Summary = result.Success ? "Fractal Memory index refresh requested." : $"Fractal Memory index refresh failed: {result.Error}"
            });
            RecordOperatorAudit(ctx, operations, auth, "fractal_memory_index_refresh", "fractal_memory", "Requested Fractal Memory index refresh.", result.Success, before: null, after: result);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryValidationResult);
        });

        app.MapPost("/admin/memory/fractal/handoff", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (!startup.Config.Memory.Fractal.AllowWrites)
                return Results.Json(new StructuredMemoryHandoffResult { Success = false, Error = "Fractal Memory writes are disabled by configuration." }, CoreJsonContext.Default.StructuredMemoryHandoffResult, statusCode: StatusCodes.Status403Forbidden);
            if (structuredMemoryProvider is null)
                return Results.Json(new StructuredMemoryHandoffResult { Success = false, Error = "Structured memory provider is not registered in this runtime." }, CoreJsonContext.Default.StructuredMemoryHandoffResult);

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.StructuredMemoryPathRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            var path = requestPayload.Value?.Path;
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new MutationResponse { Success = false, Error = "path is required." });

            var result = await structuredMemoryProvider.CreateHandoffAsync(path, ctx.RequestAborted);
            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "fractal_memory",
                Action = "handoff_create",
                Severity = result.Success ? "info" : "warning",
                Summary = result.Success ? $"Fractal Memory handoff created for '{path}'." : $"Fractal Memory handoff failed for '{path}': {result.Error}"
            });
            RecordOperatorAudit(ctx, operations, auth, "fractal_memory_handoff_create", path, $"Requested Fractal Memory handoff for '{path}'.", result.Success, before: null, after: result);
            return Results.Json(result, CoreJsonContext.Default.StructuredMemoryHandoffResult);
        });

        app.MapGet("/admin/memory/notes", async (HttpContext ctx, string? prefix = null, string? memoryClass = null, string? projectId = null, int limit = 100) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (memoryCatalog is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Memory catalog is not available in this runtime." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            var items = await ListMemoryNotesAsync(memoryCatalog, prefix, memoryClass, projectId, limit, ctx.RequestAborted);
            return Results.Json(
                new MemoryNoteListResponse
                {
                    Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(),
                    MemoryClass = string.IsNullOrWhiteSpace(memoryClass) ? null : memoryClass.Trim(),
                    ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
                    Items = items
                },
                CoreJsonContext.Default.MemoryNoteListResponse);
        });

        app.MapGet("/admin/memory/search", async (HttpContext ctx, string query, string? memoryClass = null, string? projectId = null, int limit = 20) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            if (memorySearch is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Memory search is not available in this runtime." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "query is required."
                });
            }

            var normalizedClass = NormalizeMemoryClass(memoryClass);
            if (normalizedClass is null && !string.IsNullOrWhiteSpace(memoryClass))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unknown memoryClass '{memoryClass}'."
                });
            }

            var normalizedProjectId = NormalizeOptionalValue(projectId);
            var prefixFilter = BuildMemoryPrefix(normalizedClass, normalizedProjectId, prefixSuffix: null);
            var hits = await memorySearch.SearchNotesAsync(query.Trim(), prefixFilter, Math.Clamp(limit, 1, 50), ctx.RequestAborted);
            var items = hits
                .Select(static hit => MapMemoryNoteItem(hit.Key, hit.Content, hit.UpdatedAt))
                .Where(item => MatchesMemoryNoteFilter(item, normalizedClass, normalizedProjectId))
                .ToArray();

            return Results.Json(
                new MemoryNoteListResponse
                {
                    Query = query.Trim(),
                    MemoryClass = normalizedClass,
                    ProjectId = normalizedProjectId,
                    Items = items
                },
                CoreJsonContext.Default.MemoryNoteListResponse);
        });

        app.MapGet("/admin/memory/notes/{key}", async (HttpContext ctx, string key) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var keyError = InputSanitizer.CheckMemoryKey(key);
            if (keyError is not null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var content = await memoryStore.LoadNoteAsync(key, ctx.RequestAborted);
            if (content is null)
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note not found."
                });
            }

            var updatedAt = DateTimeOffset.UtcNow;
            if (memoryCatalog is not null)
                updatedAt = (await memoryCatalog.GetNoteEntryAsync(key, ctx.RequestAborted))?.UpdatedAt ?? updatedAt;

            return Results.Json(
                new MemoryNoteDetailResponse
                {
                    Note = MapMemoryNoteItem(key, content, updatedAt, includeContent: true)
                },
                CoreJsonContext.Default.MemoryNoteDetailResponse);
        });

        app.MapPost("/admin/memory/notes", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.MemoryNoteUpsertRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            if (request is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note payload is required."
                });
            }

            var normalizedClass = NormalizeMemoryClass(request.MemoryClass);
            if (normalizedClass is null && !string.IsNullOrWhiteSpace(request.MemoryClass))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unknown memoryClass '{request.MemoryClass}'."
                });
            }

            var resolvedKey = BuildMemoryNoteKey(request.Key, normalizedClass, request.ProjectId, out var keyError);
            if (!string.IsNullOrWhiteSpace(keyError))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var previousContent = await memoryStore.LoadNoteAsync(resolvedKey!, ctx.RequestAborted);
            await memoryStore.SaveNoteAsync(resolvedKey!, request.Content ?? string.Empty, ctx.RequestAborted);
            var savedEntry = memoryCatalog is null
                ? MapMemoryNoteItem(resolvedKey!, request.Content ?? string.Empty, DateTimeOffset.UtcNow, includeContent: true)
                : MapMemoryNoteItem(
                    resolvedKey!,
                    request.Content ?? string.Empty,
                    (await memoryCatalog.GetNoteEntryAsync(resolvedKey!, ctx.RequestAborted))?.UpdatedAt ?? DateTimeOffset.UtcNow,
                    includeContent: true);

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "note_saved",
                Severity = "info",
                Summary = $"Saved memory note '{resolvedKey}'."
            });
            RecordOperatorAudit(ctx, operations, auth, "memory_note_save", resolvedKey!, $"Saved memory note '{resolvedKey}'.", success: true, before: previousContent, after: savedEntry);

            return Results.Json(
                new MemoryNoteDetailResponse { Note = savedEntry },
                CoreJsonContext.Default.MemoryNoteDetailResponse);
        });

        app.MapDelete("/admin/memory/notes/{key}", async (HttpContext ctx, string key) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var keyError = InputSanitizer.CheckMemoryKey(key);
            if (keyError is not null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = keyError
                });
            }

            var previousContent = await memoryStore.LoadNoteAsync(key, ctx.RequestAborted);
            if (previousContent is null)
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "Memory note not found."
                });
            }

            await memoryStore.DeleteNoteAsync(key, ctx.RequestAborted);
            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "note_deleted",
                Severity = "warning",
                Summary = $"Deleted memory note '{key}'."
            });
            RecordOperatorAudit(ctx, operations, auth, "memory_note_delete", key, $"Deleted memory note '{key}'.", success: true, before: previousContent, after: null);

            return Results.Json(
                new MutationResponse
                {
                    Success = true,
                    Message = "Memory note deleted."
                },
                CoreJsonContext.Default.MutationResponse);
        });

        app.MapGet("/admin/memory/export", async (HttpContext ctx, string? actorId = null, string? projectId = null, bool includeProfiles = true, bool includeProposals = true, bool includeAutomations = true, bool includeNotes = true) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.memory.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedActorId = NormalizeOptionalValue(actorId);
            var normalizedProjectId = NormalizeOptionalValue(projectId);

            IReadOnlyList<UserProfile> profiles = [];
            if (includeProfiles)
            {
                profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    profiles = profiles
                        .Where(profile => string.Equals(profile.ActorId, normalizedActorId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
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

            IReadOnlyList<AutomationDefinition> automations = [];
            if (includeAutomations)
            {
                automations = await automationService.ListAsync(ctx.RequestAborted);
            }

            IReadOnlyList<MemoryNoteItem> notes = [];
            if (includeNotes && memoryCatalog is not null)
            {
                var prefixFilter = BuildMemoryPrefix(memoryClass: null, normalizedProjectId, prefixSuffix: null);
                var entries = await memoryCatalog.ListNotesAsync(prefixFilter, 500, ctx.RequestAborted);
                notes = await MaterializeMemoryNoteItemsAsync(memoryStore, entries, includeContent: true, ctx.RequestAborted);
            }

            return Results.Json(
                new MemoryConsoleExportBundle
                {
                    ExportedAtUtc = DateTimeOffset.UtcNow,
                    Notes = notes,
                    Profiles = profiles,
                    Proposals = proposals,
                    Automations = automations
                },
                CoreJsonContext.Default.MemoryConsoleExportBundle);
        });

        app.MapPost("/admin/memory/import", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.memory.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.MemoryConsoleExportBundle);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var bundle = requestPayload.Value;
            if (bundle is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Memory import payload is required."
                });
            }

            var notesImported = 0;
            var profilesImported = 0;
            var proposalsImported = 0;
            var automationsImported = 0;

            var invalidNoteKeys = bundle.Notes
                .Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null)
                .Select(static note => new { note.Key, Error = InputSanitizer.CheckMemoryKey(note.Key) })
                .Where(static item => item.Error is not null)
                .ToArray();
            if (invalidNoteKeys.Length > 0)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Memory import contains invalid note keys: {string.Join(", ", invalidNoteKeys.Select(static item => item.Key))}."
                });
            }

            foreach (var note in bundle.Notes.Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null))
            {
                await memoryStore.SaveNoteAsync(note.Key, note.Content!, ctx.RequestAborted);
                notesImported++;
            }

            foreach (var profile in bundle.Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.ActorId)))
            {
                await profileStore.SaveProfileAsync(NormalizeProfile(profile), ctx.RequestAborted);
                profilesImported++;
            }

            foreach (var proposal in bundle.Proposals.Where(static proposal => !string.IsNullOrWhiteSpace(proposal.Id)))
            {
                await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                proposalsImported++;
            }

            foreach (var automation in bundle.Automations.Where(static automation => !string.IsNullOrWhiteSpace(automation.Id)))
            {
                await automationService.SaveAsync(automation, ctx.RequestAborted);
                automationsImported++;
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "memory",
                Action = "imported",
                Severity = "info",
                Summary = $"Imported {notesImported} memory notes, {profilesImported} profiles, {proposalsImported} proposals, and {automationsImported} automations."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "memory_import",
                "memory-bundle",
                $"Imported {notesImported} notes, {profilesImported} profiles, {proposalsImported} proposals, and {automationsImported} automations.",
                success: true,
                before: null,
                after: new { notesImported, profilesImported, proposalsImported, automationsImported });

            return Results.Json(
                new MemoryConsoleImportResponse
                {
                    Success = true,
                    NotesImported = notesImported,
                    ProfilesImported = profilesImported,
                    ProposalsImported = proposalsImported,
                    AutomationsImported = automationsImported,
                    Message = "Memory bundle imported."
                },
                CoreJsonContext.Default.MemoryConsoleImportResponse);
        });

        app.MapGet("/admin/agent-bundle/export", async (HttpContext ctx, string? actorId = null, string? projectId = null, bool includeSettings = true, bool includeNotes = true, bool includeProfiles = true, bool includeProposals = true, bool includeAutomations = true, bool includePolicies = true, bool includeManagedSkills = true) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.agent-bundle.export");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var normalizedActorId = NormalizeOptionalValue(actorId);
            var normalizedProjectId = NormalizeOptionalValue(projectId);

            IReadOnlyList<UserProfile> profiles = [];
            if (includeProfiles)
            {
                profiles = await profileStore.ListProfilesAsync(ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(normalizedActorId))
                {
                    profiles = profiles
                        .Where(profile => string.Equals(profile.ActorId, normalizedActorId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
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

            IReadOnlyList<AutomationDefinition> automations = [];
            if (includeAutomations)
                automations = await automationService.ListAsync(ctx.RequestAborted);

            IReadOnlyList<MemoryNoteItem> notes = [];
            if (includeNotes && memoryCatalog is not null)
            {
                var prefixFilter = BuildMemoryPrefix(memoryClass: null, normalizedProjectId, prefixSuffix: null);
                var entries = await memoryCatalog.ListNotesAsync(prefixFilter, 1_000, ctx.RequestAborted);
                notes = await MaterializeMemoryNoteItemsAsync(memoryStore, entries, includeContent: true, ctx.RequestAborted);
            }

            var bundle = new AgentBundleExportBundle
            {
                ExportedAtUtc = DateTimeOffset.UtcNow,
                Settings = includeSettings ? adminSettings.GetSnapshot() : null,
                Notes = notes,
                Profiles = profiles,
                Proposals = proposals,
                Automations = automations,
                ProviderPolicies = includePolicies ? operations.ProviderPolicies.List() : [],
                ManagedSkills = includeManagedSkills
                    ? await ListManagedSkillBundleItemsAsync(startup.Config, ctx.RequestAborted)
                    : []
            };

            return Results.Json(bundle, CoreJsonContext.Default.AgentBundleExportBundle);
        });

        app.MapPost("/admin/agent-bundle/import", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.agent-bundle.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AgentBundleExportBundle, MaxAgentBundleJsonBodyBytes);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var bundle = requestPayload.Value;
            if (bundle is null)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Agent bundle payload is required."
                });
            }

            if (!string.Equals(bundle.Format, "openclaw-agent-bundle", StringComparison.Ordinal))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unsupported agent bundle format '{bundle.Format}'."
                });
            }

            if (bundle.Version != 1)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Unsupported agent bundle version '{bundle.Version}'."
                });
            }

            var invalidNoteKeys = bundle.Notes
                .Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null)
                .Select(static note => new { note.Key, Error = InputSanitizer.CheckMemoryKey(note.Key) })
                .Where(static item => item.Error is not null)
                .ToArray();
            if (invalidNoteKeys.Length > 0)
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = $"Agent bundle contains invalid note keys: {string.Join(", ", invalidNoteKeys.Select(static item => item.Key))}."
                });
            }

            if (bundle.ManagedSkills.Any(static skill => string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Content)))
            {
                return Results.BadRequest(new MutationResponse
                {
                    Success = false,
                    Error = "Agent bundle contains managed skills with missing name or content."
                });
            }

            var settingsImported = false;
            if (bundle.Settings is not null)
            {
                var settingsResult = adminSettings.Update(bundle.Settings);
                if (!settingsResult.Success)
                {
                    return Results.Json(
                        new AgentBundleImportResponse
                        {
                            Success = false,
                            Version = bundle.Version,
                            Message = settingsResult.Errors.Count > 0
                                ? string.Join("; ", settingsResult.Errors)
                                : "Agent bundle settings validation failed."
                        },
                        CoreJsonContext.Default.AgentBundleImportResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                settingsImported = true;
            }

            var notesImported = 0;
            var profilesImported = 0;
            var proposalsImported = 0;
            var automationsImported = 0;
            var providerPoliciesImported = 0;
            var managedSkillsImported = 0;

            foreach (var note in bundle.Notes.Where(static note => !string.IsNullOrWhiteSpace(note.Key) && note.Content is not null))
            {
                await memoryStore.SaveNoteAsync(note.Key, note.Content!, ctx.RequestAborted);
                notesImported++;
            }

            foreach (var profile in bundle.Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.ActorId)))
            {
                await profileStore.SaveProfileAsync(NormalizeProfile(profile), ctx.RequestAborted);
                profilesImported++;
            }

            foreach (var proposal in bundle.Proposals.Where(static proposal => !string.IsNullOrWhiteSpace(proposal.Id)))
            {
                await proposalStore.SaveProposalAsync(proposal, ctx.RequestAborted);
                proposalsImported++;
            }

            foreach (var automation in bundle.Automations.Where(static automation => !string.IsNullOrWhiteSpace(automation.Id)))
            {
                await automationService.SaveAsync(automation, ctx.RequestAborted);
                automationsImported++;
            }

            foreach (var policy in bundle.ProviderPolicies.Where(static policy => !string.IsNullOrWhiteSpace(policy.ProviderId) && !string.IsNullOrWhiteSpace(policy.ModelId)))
            {
                operations.ProviderPolicies.AddOrUpdate(policy);
                providerPoliciesImported++;
            }

            var shouldReloadSkills = false;
            foreach (var managedSkill in bundle.ManagedSkills)
            {
                await SaveManagedSkillBundleItemAsync(startup.Config, managedSkill, ctx.RequestAborted);
                managedSkillsImported++;
                shouldReloadSkills = true;
            }

            if (shouldReloadSkills)
            {
                await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
                runtime.LoadedSkills = LoadCurrentSkillDefinitions(startup, runtime);
            }

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                Component = "agent-bundle",
                Action = "imported",
                Severity = "info",
                Summary = $"Imported agent bundle v{bundle.Version} with {notesImported} notes, {profilesImported} profiles, {proposalsImported} proposals, {automationsImported} automations, {providerPoliciesImported} provider policies, and {managedSkillsImported} managed skills."
            });
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "agent_bundle_import",
                "agent-bundle",
                $"Imported agent bundle v{bundle.Version}.",
                success: true,
                before: null,
                after: new
                {
                    bundle.Version,
                    settingsImported,
                    notesImported,
                    profilesImported,
                    proposalsImported,
                    automationsImported,
                    providerPoliciesImported,
                    managedSkillsImported,
                    skillsReloaded = shouldReloadSkills
                });

            return Results.Json(
                new AgentBundleImportResponse
                {
                    Success = true,
                    Version = bundle.Version,
                    SettingsImported = settingsImported,
                    NotesImported = notesImported,
                    ProfilesImported = profilesImported,
                    ProposalsImported = proposalsImported,
                    AutomationsImported = automationsImported,
                    ProviderPoliciesImported = providerPoliciesImported,
                    ManagedSkillsImported = managedSkillsImported,
                    SkillsReloaded = shouldReloadSkills,
                    Message = "Agent bundle imported."
                },
                CoreJsonContext.Default.AgentBundleImportResponse);
        });
    }

    private static StructuredMemoryStatusResponse BuildUnavailableFractalStatus(GatewayConfig config, string error)
        => new()
        {
            Enabled = config.Memory.Fractal.Enabled,
            Mode = string.IsNullOrWhiteSpace(config.Memory.Fractal.Mode) ? "mcp" : config.Memory.Fractal.Mode.Trim(),
            RepositoryRoot = config.Memory.Fractal.RepositoryRoot,
            McpCommand = config.Memory.Fractal.McpCommand,
            AutoContextMode = config.Memory.Fractal.AutoContextMode,
            AllowWrites = config.Memory.Fractal.AllowWrites,
            WriteToolsAvailable = false,
            Available = false,
            Status = config.Memory.Fractal.Enabled ? "unavailable" : "disabled",
            Error = error
        };

    private static string NormalizeFractalMemoryView(string? value, string? fallback)
        => (NormalizeOptionalValue(value) ?? NormalizeOptionalValue(fallback) ?? "index").ToLowerInvariant() switch
        {
            "state" => "state",
            "timeline" => "timeline",
            "decisions" => "decisions",
            "children" => "children",
            _ => "index"
        };
}
