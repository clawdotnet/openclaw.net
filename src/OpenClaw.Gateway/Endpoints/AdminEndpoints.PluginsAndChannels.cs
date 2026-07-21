using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using OpenClaw.Gateway.Mcp;
using OpenClaw.Gateway.Models;
using QRCoder;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapPluginAndChannelEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var adminSettings = services.AdminSettings;
        var pluginAdminSettings = services.PluginAdminSettings;
        var facade = services.Facade;
        var operations = services.Operations;
        var mcpConfigStore = app.Services.GetService<McpConfigStore>()
            ?? new McpConfigStore(startup.Config.Memory.StoragePath, NullLogger<McpConfigStore>.Instance);
        var mcpWatcherHolder = app.Services.GetService<McpWatcherHolder>() ?? new McpWatcherHolder();

        app.MapGet("/admin/plugins", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new PluginListResponse
            {
                Items = operations.PluginHealth.ListSnapshots()
            }, CoreJsonContext.Default.PluginListResponse);
        });

        app.MapGet("/admin/skills", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.skills");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var loadedSkills = LoadCurrentSkillDefinitions(startup, runtime);
            runtime.LoadedSkills = loadedSkills;
            return Results.Json(new SkillListResponse
            {
                Items = loadedSkills
                    .Select(MapSkillHealthSnapshot)
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            }, CoreJsonContext.Default.SkillListResponse);
        });

        app.MapGet("/admin/skills/cost-estimate", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.skills.cost-estimate");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var loadedSkills = LoadCurrentSkillDefinitions(startup, runtime);

            var eagerTotal = SkillPromptBuilder.EstimateCharacterCost(loadedSkills);
            var indexTotal = SkillPromptBuilder.EstimateIndexCharacterCost(loadedSkills);
            var saved = Math.Max(0, eagerTotal - indexTotal);
            var ratio = eagerTotal > 0 ? (double)saved / eagerTotal : 0d;

            var items = loadedSkills
                .Select(skill => new SkillCostBreakdown
                {
                    Name = skill.Name,
                    Description = skill.Description,
                    EagerCharacters = SkillPromptBuilder.EstimateSkillEagerCost(skill),
                    IndexCharacters = SkillPromptBuilder.EstimateSkillIndexCost(skill),
                    ResourceCount = skill.Resources.Count,
                    InstructionsLength = skill.Instructions.Length,
                    ExcludedFromModel = skill.DisableModelInvocation
                })
                .OrderByDescending(static b => b.EagerCharacters)
                .ThenBy(static b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var response = new SkillCostEstimateResponse
            {
                TotalSkills = loadedSkills.Count,
                ModelInvocableSkills = loadedSkills.Count(static s => !s.DisableModelInvocation),
                EagerCharacters = eagerTotal,
                IndexCharacters = indexTotal,
                CharactersSaved = saved,
                SavedRatio = ratio,
                // Rough 4-chars-per-token heuristic; UI labels this as an estimate.
                EagerTokensEstimate = (int)Math.Ceiling(eagerTotal / 4d),
                IndexTokensEstimate = (int)Math.Ceiling(indexTotal / 4d),
                Items = items,
                GeneratedAt = DateTimeOffset.UtcNow
            };

            return Results.Json(response, CoreJsonContext.Default.SkillCostEstimateResponse);
        });

        app.MapPost("/admin/skills", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.skills");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.SkillInstallRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            var request = requestPayload.Value;
            if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Content))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "name and content are required." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.Name, @"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "name must be 1-64 alphanumeric/hyphen/underscore characters starting with alphanumeric." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var workspacePath = startup.WorkspacePath ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);

            var skillDir = Path.Combine(workspacePath, "skills", request.Name);
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), request.Content, ctx.RequestAborted);

            var reloaded = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "skill_install", request.Name, $"Installed skill '{request.Name}'. Total: {reloaded.Count}.", true, before: null, after: null);
            return Results.Json(new SkillMutationResponse { Success = true, TotalLoaded = reloaded.Count, LoadedNames = reloaded }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete("/admin/skills/{name}", async (HttpContext ctx, string name) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.skills");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status429TooManyRequests);

            name = name.Trim().Trim('"').Trim('\'');
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid skill name." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var wsPath = startup.WorkspacePath ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(wsPath))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);

            // Only allow deleting workspace-sourced skills.
            // Use the resolved wsPath (not startup.WorkspacePath which may be null when
            // workspace is configured via Tooling.WorkspaceRoot rather than the env var).
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SkillLoader");
            var allSkills = OpenClaw.Core.Skills.SkillLoader.LoadAll(startup.Config.Skills, wsPath, logger);
            var target = allSkills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target is not null && target.Source != OpenClaw.Core.Skills.SkillSource.Workspace)
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Skill '{name}' is a built-in skill and cannot be deleted." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status403Forbidden);

            var skillDir = Path.Combine(wsPath, "skills", name);
            if (!Directory.Exists(skillDir))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"User-installed skill '{name}' not found in workspace." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status404NotFound);

            Directory.Delete(skillDir, recursive: true);
            var reloaded = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "skill_remove", name, $"Removed skill '{name}'. Total: {reloaded.Count}.", true, before: null, after: null);
            return Results.Json(new SkillMutationResponse { Success = true, TotalLoaded = reloaded.Count, LoadedNames = reloaded }, CoreJsonContext.Default.SkillMutationResponse);
        });

        app.MapPost("/admin/skills/upload", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.skills");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;
            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status429TooManyRequests);

            var wsPath = startup.WorkspacePath ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(wsPath))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status501NotImplemented);

            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.Json(new SkillMutationResponse { Success = false, Error = "No file uploaded. Send multipart/form-data with field 'file'." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var upload = ctx.Request.Form.Files[0];
            const long MaxZipBytes = 10 * 1024 * 1024;
            if (upload.Length > MaxZipBytes)
                return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP file too large (max 10 MB)." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            // Phase 1: parse SKILL.md from ZIP to extract skill name
            System.IO.Compression.ZipArchiveEntry? skillMdEntry;
            string skillMdContent;
            try
            {
                using var stream1 = upload.OpenReadStream();
                using var zip1 = new System.IO.Compression.ZipArchive(stream1, System.IO.Compression.ZipArchiveMode.Read);
                skillMdEntry = zip1.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
                if (skillMdEntry is null)
                    return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP must contain a SKILL.md file." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
                using var mdStream = skillMdEntry.Open();
                using var reader = new StreamReader(mdStream);
                skillMdContent = await reader.ReadToEndAsync(ctx.RequestAborted);
            }
            catch (InvalidDataException)
            {
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid or corrupted ZIP file." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            // Extract skill name from SKILL.md frontmatter
            string? skillName = null;
            {
                var inFm = false;
                foreach (var rawLine in skillMdContent.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line == "---") { if (!inFm) { inFm = true; continue; } else break; }
                    if (!inFm) continue;
                    var ci = line.IndexOf(':');
                    if (ci < 0) continue;
                    if (line[..ci].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                    { skillName = line[(ci + 1)..].Trim().Trim('"').Trim('\''); break; }
                }
            }
            if (string.IsNullOrWhiteSpace(skillName))
                return Results.Json(new SkillMutationResponse { Success = false, Error = "SKILL.md is missing a valid 'name:' frontmatter field." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            if (!System.Text.RegularExpressions.Regex.IsMatch(skillName, @"^[a-zA-Z0-9][a-zA-Z0-9_\-.]{0,63}$"))
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Skill name '{skillName}' contains invalid characters." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);

            var skillDir = Path.GetFullPath(Path.Combine(wsPath, "skills", skillName));
            var skillDirPrefix = skillDir + Path.DirectorySeparatorChar;

            // Determine the prefix inside the ZIP to strip (= parent directory of SKILL.md,
            // matching the old ControlEndpoints approach which is more precise than using the
            // first-entry top-level folder).
            var lastSlashIdx = skillMdEntry.FullName.LastIndexOf('/');
            var zipPrefix = lastSlashIdx >= 0 ? skillMdEntry.FullName[..(lastSlashIdx + 1)] : "";

            // Phase 2: ZIP slip validation — reject any entry that would escape skillDir
            try
            {
                using var stream2 = upload.OpenReadStream();
                using var zip2 = new System.IO.Compression.ZipArchive(stream2, System.IO.Compression.ZipArchiveMode.Read);
                foreach (var entry in zip2.Entries)
                {
                    var rel = zipPrefix.Length > 0 && entry.FullName.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullName[zipPrefix.Length..] : entry.FullName;
                    if (string.IsNullOrEmpty(rel)) continue;
                    var destFull = Path.GetFullPath(Path.Combine(skillDir, rel));
                    if (!destFull.StartsWith(skillDirPrefix, StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new SkillMutationResponse { Success = false, Error = "ZIP contains a path traversal entry and was rejected." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
                }
            }
            catch (InvalidDataException)
            {
                return Results.Json(new SkillMutationResponse { Success = false, Error = "Invalid or corrupted ZIP file." }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            // Phase 3: extract to workspace
            if (Directory.Exists(skillDir))
                Directory.Delete(skillDir, recursive: true);
            Directory.CreateDirectory(skillDir);
            try
            {
                using var stream3 = upload.OpenReadStream();
                using var zip3 = new System.IO.Compression.ZipArchive(stream3, System.IO.Compression.ZipArchiveMode.Read);
                foreach (var entry in zip3.Entries)
                {
                    var rel = zipPrefix.Length > 0 && entry.FullName.StartsWith(zipPrefix, StringComparison.OrdinalIgnoreCase)
                        ? entry.FullName[zipPrefix.Length..] : entry.FullName;
                    if (string.IsNullOrEmpty(rel) || rel.EndsWith('/') || rel.EndsWith('\\')) continue;
                    var destPath = Path.GetFullPath(Path.Combine(skillDir, rel));
                    if (!destPath.StartsWith(skillDirPrefix, StringComparison.OrdinalIgnoreCase))
                        continue; // defense-in-depth: skip traversal entries (Phase 2 already validates)
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var entryStream = entry.Open();
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await entryStream.CopyToAsync(fs, ctx.RequestAborted);
                }
            }
            catch (Exception ex)
            {
                if (Directory.Exists(skillDir)) Directory.Delete(skillDir, recursive: true);
                return Results.Json(new SkillMutationResponse { Success = false, Error = $"Extraction failed: {ex.Message}" }, CoreJsonContext.Default.SkillMutationResponse, statusCode: StatusCodes.Status500InternalServerError);
            }

            var reloaded = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "skill_install_zip", skillName, $"Installed skill '{skillName}' via ZIP. Total: {reloaded.Count}.", true, before: null, after: null);
            return Results.Json(new SkillMutationResponse { Success = true, TotalLoaded = reloaded.Count, LoadedNames = reloaded }, CoreJsonContext.Default.SkillMutationResponse);
        });

        app.MapGet("/admin/compatibility/catalog", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.compatibility");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var compatibilityStatus = ctx.Request.Query.TryGetValue("compatibilityStatus", out var statusValue)
                ? statusValue.ToString()
                : null;
            var kind = ctx.Request.Query.TryGetValue("kind", out var kindValue)
                ? kindValue.ToString()
                : null;
            var category = ctx.Request.Query.TryGetValue("category", out var categoryValue)
                ? categoryValue.ToString()
                : null;

            return Results.Json(
                facade.GetCompatibilityCatalog(compatibilityStatus, kind, category),
                CoreJsonContext.Default.IntegrationCompatibilityCatalogResponse);
        });

        app.MapGet("/admin/compatibility/export", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.compatibility");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                facade.GetCompatibilityExport(),
                CoreJsonContext.Default.IntegrationCompatibilityExportResponse);
        });

        app.MapGet("/admin/plugins/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var item = operations.PluginHealth.ListSnapshots().FirstOrDefault(snapshot => string.Equals(snapshot.PluginId, id, StringComparison.Ordinal));
            return item is null
                ? Results.NotFound(new MutationResponse { Success = false, Error = "Plugin not found." })
                : Results.Json(item, CoreJsonContext.Default.PluginHealthSnapshot);
        });

        app.MapPost("/admin/plugins/{id}/disable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_disable", id, $"Disabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin disabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/enable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_enable", id, $"Enabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin enabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/review", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetReviewed(id, reviewed: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_review", id, $"Marked plugin '{id}' as reviewed.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin marked as reviewed.", RestartRequired = false }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/unreview", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetReviewed(id, reviewed: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_unreview", id, $"Removed review mark from plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin review mark cleared.", RestartRequired = false }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_quarantine", id, $"Quarantined plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantined.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/clear-quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_clear_quarantine", id, $"Cleared quarantine for plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantine cleared.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapGet("/admin/workspace/mcp", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.workspace.mcp");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var persistedRaw = await mcpConfigStore.TryLoadRawAsync(ctx.RequestAborted);
            JsonNode? userConfig = null;
            if (!string.IsNullOrWhiteSpace(persistedRaw))
            {
                try
                {
                    userConfig = JsonNode.Parse(persistedRaw);
                }
                catch (JsonException)
                {
                    userConfig = null;
                }
            }

            var payload = new JsonObject
            {
                ["builtin"] = BuildSanitizedBuiltInMcpConfig(startup.Config.Plugins.Mcp),
                ["user"] = userConfig
            };

            return Results.Content(payload.ToJsonString(), "application/json");
        });

        app.MapPut("/admin/workspace/mcp", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.workspace.mcp.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var raw = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(raw))
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = "Request body is required." },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                using var _ = JsonDocument.Parse(raw);
            }
            catch (JsonException ex)
            {
                return Results.Json(
                    new WorkspaceUploadResponse
                    {
                        Success = false,
                        Error = $"Invalid JSON: {ex.Message}"
                    },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await mcpConfigStore.SaveAsync(raw, ctx.RequestAborted);
            mcpWatcherHolder.Watcher?.TriggerReload();
            RecordOperatorAudit(
                ctx,
                operations,
                auth,
                "workspace_mcp_update",
                "mcp.json",
                "Updated workspace MCP configuration.",
                success: true,
                before: null,
                after: null);

            return Results.Json(
                new WorkspaceUploadResponse { Success = true },
                CoreJsonContext.Default.WorkspaceUploadResponse);
        });
        // ── Channel Auth Events ──────────────────────────────────────
        var authEventStore = runtime.ChannelAuthEvents;

        app.MapGet("/admin/channels/auth", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = authEventStore.GetAll().Select(MapChannelAuthStatusItem).ToArray()
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth", (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest(channelId, accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll(channelId).Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No auth event recorded for this channel." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth/stream", async (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, channelId, accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll("whatsapp").Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No WhatsApp auth event recorded." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/whatsapp/auth/stream", async (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, "whatsapp", accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth/qr.svg", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var evt = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId)
                : authEventStore.GetAll("whatsapp").FirstOrDefault(static item =>
                    string.Equals(item.State, "qr_code", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Data));
            if (evt is null || !string.Equals(evt.State, "qr_code", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(evt.Data))
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "No active WhatsApp QR code is available."
                });
            }

            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(evt.Data, QRCodeGenerator.ECCLevel.Q);
            var svg = new SvgQRCode(qrData).GetGraphic(6);
            return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
        });

        app.MapGet("/admin/channels/whatsapp/setup", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp setup loaded.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });

        app.MapPut("/admin/channels/whatsapp/setup", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.WhatsAppSetupRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "WhatsApp setup payload is required." });

            var normalizedRequestResult = NormalizeWhatsAppSetupRequest(request);
            if (normalizedRequestResult.Errors.Count > 0)
            {
                var invalidResponse = BuildWhatsAppSetupResponse(
                    startup,
                    runtime,
                    adminSettings,
                    pluginAdminSettings,
                    message: "WhatsApp setup validation failed.",
                    validationErrors: normalizedRequestResult.Errors);
                return Results.Json(invalidResponse, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var normalizedRequest = normalizedRequestResult.Request;
            var builtInResult = adminSettings.UpdateWhatsAppSettings(normalizedRequest);
            var validationErrors = ValidateWhatsAppPluginConfig(startup, runtime, normalizedRequest, out var pluginId, out var pluginConfig, out var pluginWarning);
            var pluginChanged = false;
            if (builtInResult.Success && validationErrors.Count == 0 && pluginId is not null)
            {
                pluginAdminSettings.Upsert(pluginId, pluginConfig, enabled: true);
                pluginChanged = true;
            }
            var response = BuildWhatsAppSetupResponse(
                startup,
                runtime,
                adminSettings,
                pluginAdminSettings,
                message: builtInResult.Success && validationErrors.Count == 0 ? "WhatsApp setup saved." : "WhatsApp setup validation failed.",
                restartRequired: builtInResult.RestartRequired || pluginChanged,
                validationErrors: [.. builtInResult.Errors, .. validationErrors],
                pluginWarningOverride: pluginWarning);

            if (builtInResult.Success && validationErrors.Count == 0)
            {
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "whatsapp_setup_update",
                    "whatsapp",
                    "Updated WhatsApp setup.",
                    success: true,
                    before: null,
                    after: normalizedRequest);
                return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
            }

            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapPost("/admin/channels/whatsapp/restart", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth.restart");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (!runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter) || adapter is not IRestartableChannelAdapter restartable)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Runtime restart is only available for plugin-backed WhatsApp channels." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            authEventStore.ClearChannel("whatsapp");
            await restartable.RestartAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "whatsapp_restart", "whatsapp", "Restarted WhatsApp channel.", success: true, before: null, after: null);

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp channel restarted.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });
    }

    private static JsonObject BuildSanitizedBuiltInMcpConfig(McpPluginsConfig config)
    {
        var servers = new JsonObject();
        foreach (var (serverId, server) in config.Servers)
        {
            var sanitizedHeaders = new JsonObject();
            foreach (var (headerName, value) in server.Headers)
            {
                sanitizedHeaders[headerName] = new JsonObject
                {
                    ["hasToken"] = !string.IsNullOrWhiteSpace(value)
                };
            }

            servers[serverId] = new JsonObject
            {
                ["enabled"] = server.Enabled,
                ["name"] = server.Name,
                ["transport"] = server.Transport,
                ["command"] = server.Command,
                ["arguments"] = new JsonArray(server.Arguments.Select(arg => JsonValue.Create(arg)).ToArray()),
                ["workingDirectory"] = server.WorkingDirectory,
                ["url"] = server.Url,
                ["toolNamePrefix"] = server.ToolNamePrefix,
                ["startupTimeoutSeconds"] = server.StartupTimeoutSeconds,
                ["requestTimeoutSeconds"] = server.RequestTimeoutSeconds,
                ["headers"] = sanitizedHeaders
            };
        }

        return new JsonObject
        {
            ["enabled"] = config.Enabled,
            ["servers"] = servers
        };
    }
}
