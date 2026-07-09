using System.IO.Compression;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class WorkspaceFileEndpoints
{
    private const long MaxUploadBytes = 100 * 1024 * 1024; // 100 MB

    public static void MapOpenClawWorkspaceFileEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var browserSessions = app.Services.GetRequiredService<BrowserSessionAuthService>();
        var operations = runtime.Operations;

        // POST /admin/workspace/upload?dir=<relative-subdir>
        // Upload one or more files into workspace/<dir>/.
        // Special case: a single .zip file is extracted into workspace/<dir>/ instead.
        app.MapPost("/admin/workspace/upload", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            if (!EndpointHelpers.TryConsumeOperatorRateLimit(ctx, operations, auth, "admin.control", out var blockedByPolicyId))
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = $"Rate limit exceeded by policy '{blockedByPolicyId}'." },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status429TooManyRequests);

            var workspacePath = startup.WorkspacePath
                ?? SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status501NotImplemented);

            var workspaceRoot = Path.GetFullPath(workspacePath);

            var dirParam = ctx.Request.Query["dir"].FirstOrDefault() ?? "";
            var targetDir = ResolveAndValidatePath(workspaceRoot, dirParam, out var pathError);
            if (targetDir is null)
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = pathError },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            EndpointHelpers.TrySetMaxRequestBodySize(ctx, MaxUploadBytes);

            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = "No files uploaded. Send multipart/form-data with one or more files." },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            var files = ctx.Request.Form.Files;

            // Single ZIP file → extract into target directory.
            if (files.Count == 1 && IsZipFile(files[0]))
            {
                var upload = files[0];
                if (upload.Length > MaxUploadBytes)
                    return Results.Json(
                        new WorkspaceUploadResponse { Success = false, Error = $"File too large (max {MaxUploadBytes / 1024 / 1024} MB)." },
                        CoreJsonContext.Default.WorkspaceUploadResponse,
                        statusCode: StatusCodes.Status400BadRequest);

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
                        new WorkspaceUploadResponse { Success = false, Error = $"Failed to read file: {ex.Message}" },
                        CoreJsonContext.Default.WorkspaceUploadResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var extracted = new List<string>();
                long totalUncompressed = 0;
                try
                {
                    Directory.CreateDirectory(targetDir);
                    using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries)
                    {
                        // Skip pure-directory entries (no filename component).
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        // ZIP-slip guard (step 1): lexical containment check.
                        var entryFull = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                        var fullDestDirPath = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
                        if (!entryFull.StartsWith(fullDestDirPath, StringComparison.Ordinal))
                            continue; // silently skip traversal attempts

                        // Enforce quota on expanded (uncompressed) contents.
                        totalUncompressed += entry.Length;
                        if (totalUncompressed > MaxUploadBytes)
                            return Results.Json(
                                new WorkspaceUploadResponse { Success = false, Error = $"Expanded archive exceeds upload limit ({MaxUploadBytes / 1024 / 1024} MB)." },
                                CoreJsonContext.Default.WorkspaceUploadResponse,
                                statusCode: StatusCodes.Status400BadRequest);

                        // ZIP-slip guard (step 2): create the parent directory first so
                        // that ResolveRealPath can walk any existing symlinks/junctions,
                        // then re-validate containment on the real filesystem path.
                        Directory.CreateDirectory(Path.GetDirectoryName(entryFull)!);
                        var realEntryFull = ResolveRealPath(entryFull);
                        if (!IsInsideDirectory(realEntryFull, workspaceRoot))
                            continue; // symlink/junction escape — skip

                        await using var outStream = new FileStream(realEntryFull, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
                        await using var entryStream = entry.Open();
                        await entryStream.CopyToAsync(outStream, ctx.RequestAborted);
                        extracted.Add(Path.GetRelativePath(workspaceRoot, realEntryFull).Replace('\\', '/'));
                    }
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new WorkspaceUploadResponse { Success = false, Error = $"Extraction failed: {ex.Message}" },
                        CoreJsonContext.Default.WorkspaceUploadResponse,
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                AppendAudit(ctx, operations, auth, "workspace_upload_zip",
                    dirParam.Length > 0 ? dirParam : "/",
                    $"Extracted '{upload.FileName}': {extracted.Count} file(s) to '{(dirParam.Length > 0 ? dirParam : "/")}'.", true);

                return Results.Json(
                    new WorkspaceUploadResponse { Success = true, Files = extracted, FileCount = extracted.Count },
                    CoreJsonContext.Default.WorkspaceUploadResponse);
            }

            // One or more regular files → save directly to target directory.
            var saved = new List<string>();
            try
            {
                Directory.CreateDirectory(targetDir);
                foreach (var file in files)
                {
                    if (file.Length == 0)
                        continue;

                    if (file.Length > MaxUploadBytes)
                        return Results.Json(
                            new WorkspaceUploadResponse { Success = false, Error = $"File '{file.FileName}' too large (max {MaxUploadBytes / 1024 / 1024} MB)." },
                            CoreJsonContext.Default.WorkspaceUploadResponse,
                            statusCode: StatusCodes.Status400BadRequest);

                    var safeName = SanitizeFileName(Path.GetFileName(file.FileName ?? "upload.bin"));
                    if (string.IsNullOrWhiteSpace(safeName))
                        safeName = "upload.bin";

                    var destPath = Path.GetFullPath(Path.Combine(targetDir, safeName));
                    if (!IsInsideDirectory(destPath, targetDir))
                        return Results.Json(
                            new WorkspaceUploadResponse { Success = false, Error = $"Invalid filename: '{file.FileName}'." },
                            CoreJsonContext.Default.WorkspaceUploadResponse,
                            statusCode: StatusCodes.Status400BadRequest);

                    await using var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
                    await file.CopyToAsync(outStream, ctx.RequestAborted);
                    saved.Add(Path.GetRelativePath(workspaceRoot, destPath).Replace('\\', '/'));
                }
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = $"Upload failed: {ex.Message}" },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            AppendAudit(ctx, operations, auth, "workspace_upload_files",
                dirParam.Length > 0 ? dirParam : "/",
                $"Uploaded {saved.Count} file(s) to '{(dirParam.Length > 0 ? dirParam : "/")}'.", true);

            return Results.Json(
                new WorkspaceUploadResponse { Success = true, Files = saved, FileCount = saved.Count },
                CoreJsonContext.Default.WorkspaceUploadResponse);
        });

        // GET /admin/workspace/tree?path=<relative-path>&depth=<max-depth>
        // Returns the recursive directory/file tree of the given path (default: workspace root).
        // depth defaults to 6; use 0 for unlimited (use with caution on large workspaces).
        app.MapGet("/admin/workspace/tree", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            var workspacePath = startup.WorkspacePath
                ?? SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(
                    new WorkspaceTreeResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." },
                    CoreJsonContext.Default.WorkspaceTreeResponse,
                    statusCode: StatusCodes.Status501NotImplemented);

            var workspaceRoot = Path.GetFullPath(workspacePath);

            var pathParam  = ctx.Request.Query["path"].FirstOrDefault() ?? "";
            var depthParam = ctx.Request.Query["depth"].FirstOrDefault();
            int maxDepth   = int.TryParse(depthParam, out var d) && d >= 0 ? d : 6;

            var targetPath = ResolveAndValidatePath(workspaceRoot, pathParam, out var pathError);
            if (targetPath is null)
                return Results.Json(
                    new WorkspaceTreeResponse { Success = false, Error = pathError },
                    CoreJsonContext.Default.WorkspaceTreeResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
                return Results.Json(
                    new WorkspaceTreeResponse { Success = false, Error = "Path does not exist." },
                    CoreJsonContext.Default.WorkspaceTreeResponse,
                    statusCode: StatusCodes.Status404NotFound);

            var rootRelative = Path.GetRelativePath(workspaceRoot, targetPath).Replace('\\', '/');
            if (rootRelative == ".") rootRelative = "";

            static WorkspaceTreeEntry BuildEntry(string fullPath, string wsRoot, int depth, int maxDepth)
            {
                var relPath = Path.GetRelativePath(wsRoot, fullPath).Replace('\\', '/');
                var name    = Path.GetFileName(fullPath);

                if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    return new WorkspaceTreeEntry { Name = name, Path = relPath, IsDir = false, Size = info.Length };
                }

                if (maxDepth == 0 || depth < maxDepth)
                {
                    var children = new List<WorkspaceTreeEntry>();
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(fullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            children.Add(BuildEntry(sub, wsRoot, depth + 1, maxDepth));
                        foreach (var file in Directory.GetFiles(fullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            children.Add(BuildEntry(file, wsRoot, depth + 1, maxDepth));
                    }
                    catch { /* ignore permission errors on individual dirs */ }

                    return new WorkspaceTreeEntry { Name = name, Path = relPath, IsDir = true, Children = children };
                }

                return new WorkspaceTreeEntry { Name = name, Path = relPath, IsDir = true };
            }

            List<WorkspaceTreeEntry> entries;
            if (File.Exists(targetPath))
            {
                var fi = new FileInfo(targetPath);
                entries = [new WorkspaceTreeEntry { Name = fi.Name, Path = rootRelative, IsDir = false, Size = fi.Length }];
            }
            else
            {
                entries = [];
                try
                {
                    foreach (var sub in Directory.GetDirectories(targetPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        entries.Add(BuildEntry(sub, workspaceRoot, 1, maxDepth));
                    foreach (var file in Directory.GetFiles(targetPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        entries.Add(BuildEntry(file, workspaceRoot, 1, maxDepth));
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new WorkspaceTreeResponse { Success = false, Error = $"Failed to enumerate directory: {ex.Message}" },
                        CoreJsonContext.Default.WorkspaceTreeResponse,
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            await ValueTask.CompletedTask;
            return Results.Json(
                new WorkspaceTreeResponse { Success = true, Root = rootRelative, Entries = entries },
                CoreJsonContext.Default.WorkspaceTreeResponse);
        });

        // GET /admin/workspace/browse?path=<relative-path>
        // Returns a flat (one-level) directory listing with { files: [{ name, path, isDirectory, size }] }.
        app.MapGet("/admin/workspace/browse", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            var workspacePath = startup.WorkspacePath
                ?? SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(
                    new WorkspaceBrowseResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." },
                    CoreJsonContext.Default.WorkspaceBrowseResponse,
                    statusCode: StatusCodes.Status501NotImplemented);

            var workspaceRoot = Path.GetFullPath(workspacePath);

            var pathParam = ctx.Request.Query["path"].FirstOrDefault() ?? "";
            var targetPath = ResolveAndValidatePath(workspaceRoot, pathParam, out var pathError);
            if (targetPath is null)
                return Results.Json(
                    new WorkspaceBrowseResponse { Success = false, Error = pathError },
                    CoreJsonContext.Default.WorkspaceBrowseResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            if (!Directory.Exists(targetPath))
                return Results.Json(
                    new WorkspaceBrowseResponse { Success = false, Error = "Path does not exist or is not a directory." },
                    CoreJsonContext.Default.WorkspaceBrowseResponse,
                    statusCode: StatusCodes.Status404NotFound);

            var entries = new List<WorkspaceBrowseEntry>();
            try
            {
                foreach (var dir in Directory.GetDirectories(targetPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    entries.Add(new WorkspaceBrowseEntry
                    {
                        Name = Path.GetFileName(dir),
                        Path = Path.GetRelativePath(workspaceRoot, dir).Replace('\\', '/'),
                        IsDirectory = true
                    });
                foreach (var file in Directory.GetFiles(targetPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    entries.Add(new WorkspaceBrowseEntry
                    {
                        Name = Path.GetFileName(file),
                        Path = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/'),
                        IsDirectory = false,
                        Size = new FileInfo(file).Length
                    });
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new WorkspaceBrowseResponse { Success = false, Error = $"Failed to list directory: {ex.Message}" },
                    CoreJsonContext.Default.WorkspaceBrowseResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await ValueTask.CompletedTask;
            return Results.Json(
                new WorkspaceBrowseResponse { Success = true, Files = entries },
                CoreJsonContext.Default.WorkspaceBrowseResponse);
        });

        // GET /admin/workspace/download?path=<relative-path>
        // - File  → streamed directly with appropriate Content-Type.
        // - Directory → packed as ZIP and streamed.
        app.MapGet("/admin/workspace/download", async (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            var workspacePath = startup.WorkspacePath
                ?? SecretResolver.Resolve(startup.Config.Tooling.WorkspaceRoot);
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = "Workspace path is not configured (OPENCLAW_WORKSPACE not set)." },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status501NotImplemented);

            var workspaceRoot = Path.GetFullPath(workspacePath);

            var pathParam = ctx.Request.Query["path"].FirstOrDefault() ?? "";
            var targetPath = ResolveAndValidatePath(workspaceRoot, pathParam, out var pathError);
            if (targetPath is null)
                return Results.Json(
                    new WorkspaceUploadResponse { Success = false, Error = pathError },
                    CoreJsonContext.Default.WorkspaceUploadResponse,
                    statusCode: StatusCodes.Status400BadRequest);

            // Directory → stream ZIP directly to response (avoids buffering full archive in memory).
            if (Directory.Exists(targetPath))
            {
                var dirName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(dirName))
                    dirName = "workspace";
                var zipName = $"{dirName}.zip";

                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";
                ctx.Response.StatusCode = StatusCodes.Status200OK;

                using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var filePath in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
                    {
                        ctx.RequestAborted.ThrowIfCancellationRequested();
                        var entryName = Path.GetRelativePath(targetPath, filePath).Replace('\\', '/');
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        await using var entryStream = entry.Open();
                        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
                        await fileStream.CopyToAsync(entryStream, ctx.RequestAborted);
                    }
                }

                return Results.Empty;
            }

            // Single file → stream directly.
            if (File.Exists(targetPath))
            {
                var fileName = Path.GetFileName(targetPath);
                var contentType = GetContentType(fileName);
                return Results.File(targetPath, contentType, fileName, enableRangeProcessing: false);
            }

            return Results.Json(
                new WorkspaceUploadResponse { Success = false, Error = $"Path not found: '{pathParam}'." },
                CoreJsonContext.Default.WorkspaceUploadResponse,
                statusCode: StatusCodes.Status404NotFound);
        });

    }

    /// <summary>
    /// Resolves a workspace-relative path and validates it stays inside workspaceRoot.
    /// Returns null (and sets <paramref name="error"/>) on failure.
    /// </summary>
    private static string? ResolveAndValidatePath(string workspaceRoot, string relative, out string? error)
    {
        error = null;

        // Empty or bare dot → workspace root itself.
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return workspaceRoot;

        // Normalise separators and strip leading slashes.
        var cleaned = relative.Replace('\\', '/').Trim('/');

        // Reject obvious traversal before Path.GetFullPath expansion.
        if (cleaned.Contains(".."))
        {
            error = "Path must not contain '..'.";
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(workspaceRoot, cleaned));

        // Resolve symlink / junction targets so containment checks use
        // the real filesystem path, not the logical reparse-point path.
        var realFull = ResolveRealPath(full);

        // Final containment check after symlink/alias expansion.
        if (!IsInsideDirectory(realFull, workspaceRoot))
        {
            error = "Path escapes the workspace root.";
            return null;
        }

        return realFull;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is the directory itself or a descendant of it.
    /// </summary>
    private static bool IsInsideDirectory(string path, string directory)
        => WorkspacePathHelpers.IsInsideDirectory(path, directory);

    /// <summary>
    /// Walks each component of <paramref name="path"/> and resolves any symlink or junction
    /// targets so that containment checks operate on the real filesystem path.
    /// Non-existent components are kept as-is (they cannot be links).
    /// </summary>
    private static string ResolveRealPath(string path)
        => WorkspacePathHelpers.ResolveRealPath(path);

    private static bool IsZipFile(IFormFile file)
        => string.Equals(Path.GetExtension(file.FileName ?? ""), ".zip", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars).Trim('.', ' ');
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".md" => "text/markdown; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

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
