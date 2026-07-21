using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class DigitalEmployeeEndpoints
{
    private const long MaxUploadBytes = 30 * 1024 * 1024; // 30 MB

    // Config file names extracted from config/ and written directly to the workspace root.
    private static readonly HashSet<string> AllowedConfigFiles = new(StringComparer.OrdinalIgnoreCase)
        { "AGENTS.md", "SOUL.md", "IDENTITY.md", "MEMORY.md" };

    public static void MapOpenClawDigitalEmployeeEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operations = runtime.Operations;

        // POST /admin/digital-employee/upload
        // Accepts multipart/form-data with a single ZIP file containing an NCrew digital-employee
        // package. The ZIP may optionally have a top-level wrapper directory.
        //
        // Package structure expected inside the ZIP:
        //   [prefix/]config/{AGENTS,SOUL,IDENTITY,MEMORY}.md  → written to workspace root
        //   [prefix/]skills/<name>/**                          → written to workspace/skills/<name>/
        //   [prefix/]ontology/*.{md,json,jsonld}              → written to workspace/ontology/
        //   [prefix/]manifest.json                            → read for package name; not written
        //
        // Config files whose names are not in the allowed list are silently skipped.
        // After extraction, all workspace skills are hot-reloaded if any skill files were written.
        // Config files (AGENTS.md etc.) take effect on the next agent restart.
        app.MapPost("/admin/digital-employee/upload", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            var workspacePath = startup.WorkspacePath
                ?? OpenClaw.Core.Security.SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status501NotImplemented);

            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = "No file uploaded. Send multipart/form-data with a zip file." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            var upload = ctx.Request.Form.Files[0];
            if (upload.Length > MaxUploadBytes)
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = $"ZIP file too large (max {MaxUploadBytes / 1024 / 1024} MB)." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            // ── Buffer the ZIP in memory so we can read it multiple times ─────────────────────────
            byte[] zipBytes;
            try
            {
                using var ms = new MemoryStream((int)upload.Length);
                await upload.CopyToAsync(ms, ctx.RequestAborted);
                zipBytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = $"Failed to read uploaded file: {ex.Message}" },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // ── Phase 1: Inspect ZIP — detect wrapper prefix and read manifest.json ──────────────
            string zipPrefix; // e.g. "my-employee/" or ""
            string? packageName = null;
            try
            {
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

                // Detect optional single top-level wrapper directory.
                var topComponents = zip.Entries
                    .Select(e => e.FullName.Split('/')[0])
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct()
                    .ToArray();

                // If all entries share one top-level component AND it is not a known package directory,
                // treat it as a wrapper prefix to strip.
                zipPrefix = topComponents.Length == 1 && !IsKnownPackageDir(topComponents[0])
                    ? topComponents[0] + "/"
                    : "";

                // Read manifest.json for the package name (best-effort).
                var manifestEntry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, zipPrefix + "manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry is not null)
                {
                    using var manifestStream = manifestEntry.Open();
                    using var doc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ctx.RequestAborted);
                    if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        packageName = nameProp.GetString();
                }

                // Require at least one config/ or skills/ entry.
                var hasContent = zip.Entries.Any(e =>
                {
                    var rel = StripPrefix(e.FullName, zipPrefix);
                    return rel.StartsWith("config/", StringComparison.OrdinalIgnoreCase)
                        || rel.StartsWith("skills/", StringComparison.OrdinalIgnoreCase);
                });
                if (!hasContent)
                    return Results.Json(
                        new DigitalEmployeeUploadResponse { Success = false, Error = "ZIP does not appear to be a valid digital employee package (no config/ or skills/ entries found)." },
                        CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                        statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidDataException)
            {
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = "Invalid or corrupted ZIP file." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // ── Phase 2: ZIP-slip validation ──────────────────────────────────────────────────────
            var workspaceRoot = Path.GetFullPath(workspacePath);
            try
            {
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                foreach (var (_, destRel) in GetMappedFileEntries(zip, zipPrefix))
                {
                    var destFull = Path.GetFullPath(Path.Combine(workspaceRoot, destRel));
                    // Resolve any existing symlinks/junctions on the path and validate
                    // containment on the real filesystem path to prevent symlink escapes.
                    var realDestFull = WorkspacePathHelpers.ResolveRealPath(destFull);
                    if (!WorkspacePathHelpers.IsInsideDirectory(realDestFull, workspaceRoot))
                        return Results.Json(
                            new DigitalEmployeeUploadResponse { Success = false, Error = "ZIP contains a path traversal entry and was rejected." },
                            CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                            statusCode: StatusCodes.Status400BadRequest);
                }
            }
            catch (InvalidDataException)
            {
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = "Invalid or corrupted ZIP file." },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // ── Phase 3: Extract ──────────────────────────────────────────────────────────────────
            var installedFiles = new List<string>();
            var skillDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                foreach (var (entry, destRel) in GetMappedFileEntries(zip, zipPrefix))
                {
                    var destFull = Path.GetFullPath(Path.Combine(workspaceRoot, destRel));
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);

                    // Re-resolve after the directory exists so newly visible symlinks
                    // are caught, then re-validate containment before writing.
                    var realDestFull = WorkspacePathHelpers.ResolveRealPath(destFull);
                    if (!WorkspacePathHelpers.IsInsideDirectory(realDestFull, workspaceRoot))
                        continue; // symlink/junction escape — skip silently

                    using var entryStream = entry.Open();
                    using var fs = new FileStream(realDestFull, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await entryStream.CopyToAsync(fs, ctx.RequestAborted);

                    var relNorm = Path.GetRelativePath(workspaceRoot, realDestFull).Replace('\\', '/');
                    installedFiles.Add(relNorm);

                    // Track skill directory names for hot-reload.
                    if (relNorm.StartsWith("skills/", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = relNorm.Split('/');
                        if (parts.Length >= 2) skillDirNames.Add(parts[1]);
                    }
                }
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new DigitalEmployeeUploadResponse { Success = false, Error = $"Extraction failed: {ex.Message}" },
                    CoreJsonContext.Default.DigitalEmployeeUploadResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // ── Hot-reload skills if any skill files were written ─────────────────────────────────
            int totalSkillsLoaded = 0;
            if (skillDirNames.Count > 0)
            {
                var reloaded = await runtime.AgentRuntime.ReloadSkillsAsync(ctx.RequestAborted);
                totalSkillsLoaded = reloaded.Count;
            }

            AppendAudit(ctx, operations, auth, "digital_employee_upload", packageName ?? "unknown",
                $"Installed digital employee package '{packageName ?? "unnamed"}': {installedFiles.Count} file(s), {skillDirNames.Count} skill(s).", true);

            return Results.Json(
                new DigitalEmployeeUploadResponse
                {
                    Success = true,
                    Name = packageName,
                    SkillsInstalled = skillDirNames.Count,
                    InstalledFiles = installedFiles,
                    TotalSkillsLoaded = totalSkillsLoaded,
                },
                CoreJsonContext.Default.DigitalEmployeeUploadResponse);
        });
    }

    /// <summary>
    /// Maps a raw ZIP entry path (after stripping the optional wrapper prefix) to a
    /// workspace-relative path. Returns null if the entry should be skipped.
    /// </summary>
    private static string? MapEntryToWorkspaceRelative(string zipEntryFullName, string zipPrefix)
    {
        var rel = StripPrefix(zipEntryFullName, zipPrefix);

        // config/<name>.md → <name>.md at workspace root (allowed names only).
        if (rel.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = rel["config/".Length..];
            if (fileName.Contains('/') || fileName.Contains('\\')) return null;
            if (!AllowedConfigFiles.Contains(fileName)) return null;
            return fileName;
        }

        // skills/<name>/... → skills/<name>/... (skill directory name must be safe).
        if (rel.StartsWith("skills/", StringComparison.OrdinalIgnoreCase))
        {
            var afterSkills = rel["skills/".Length..];
            var slash = afterSkills.IndexOf('/');
            if (slash <= 0) return null; // must be under a sub-directory
            var skillName = afterSkills[..slash];
            if (!Regex.IsMatch(skillName, @"^[a-zA-Z0-9][a-zA-Z0-9_\-.]{0,63}$")) return null;
            return "skills" + Path.DirectorySeparatorChar + afterSkills.Replace('/', Path.DirectorySeparatorChar);
        }

        // ontology/*.{md,json} → ontology/*.{md,json} (no sub-directories).
        if (rel.StartsWith("ontology/", StringComparison.OrdinalIgnoreCase))
        {
            var afterOntology = rel["ontology/".Length..];
            if (afterOntology.Contains('/') || afterOntology.Contains('\\')) 
                return null;
            if (!afterOntology.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !afterOntology.EndsWith(".json", StringComparison.OrdinalIgnoreCase) 
                && !afterOntology.EndsWith(".jsonld", StringComparison.OrdinalIgnoreCase))
                return null;
            return "ontology" + Path.DirectorySeparatorChar + afterOntology;
        }

        return null; // manifest.json and everything else is intentionally not extracted
    }

    private static IEnumerable<(ZipArchiveEntry Entry, string DestRel)> GetMappedFileEntries(ZipArchive zip, string zipPrefix)
        => zip.Entries
            .Where(static entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\'))
            .Select(entry => (Entry: entry, DestRel: MapEntryToWorkspaceRelative(entry.FullName, zipPrefix)))
            .Where(static item => item.DestRel is not null)
            .Select(static item => (item.Entry, item.DestRel!));

    private static string StripPrefix(string path, string prefix)
        => prefix.Length > 0 && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;

    private static bool IsKnownPackageDir(string component)
        => component.Equals("config", StringComparison.OrdinalIgnoreCase)
        || component.Equals("skills", StringComparison.OrdinalIgnoreCase)
        || component.Equals("ontology", StringComparison.OrdinalIgnoreCase);

    private static void AppendAudit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        EndpointHelpers.OperatorAuthorizationResult auth,
        string actionType,
        string targetId,
        string summary,
        bool success)
    {
        operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = $"audit_{Guid.NewGuid():N}"[..20],
            ActorId = EndpointHelpers.GetOperatorActorId(ctx, auth),
            AuthMode = auth.AuthMode,
            ActionType = actionType,
            TargetId = targetId,
            Summary = summary,
            Success = success
        });
    }
}
