using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapCodebaseMapEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var browserSessions = services.BrowserSessions;
        var operations = services.Operations;
        var codebaseMap = services.CodebaseMap;

        app.MapGet("/admin/harness/codebase-map", async (
            HttpContext ctx,
            string? root = null,
            bool includeHashes = false,
            int recentDays = 30,
            int maxFiles = 5000,
            string? category = null) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.harness");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var rootResolution = ResolveCodebaseMapRoot(startup, root);
            if (rootResolution.Error is not null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = rootResolution.Error },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var map = await codebaseMap.GenerateAsync(
                rootResolution.Root,
                new CodebaseMapOptions
                {
                    IncludeHashes = includeHashes,
                    RecentDays = recentDays,
                    MaxFiles = maxFiles,
                    Category = CodebaseHarnessMapService.NormalizeCategory(category)
                },
                ctx.RequestAborted);

            return Results.Json(map, CoreJsonContext.Default.CodebaseHarnessMap);
        });
    }

    private static (string Root, string? Error) ResolveCodebaseMapRoot(GatewayStartupContext startup, string? requestedRoot)
    {
        var allowedRoot = ResolveCodebaseMapAllowedRoot(startup);
        if (allowedRoot is null)
            return ("", "Codebase map requires a configured workspace root.");

        var root = string.IsNullOrWhiteSpace(requestedRoot)
            ? allowedRoot
            : requestedRoot.Trim();
        if (!Path.IsPathRooted(root))
            root = Path.Join(allowedRoot, root);

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ("", $"Invalid codebase map root: {ex.Message}");
        }

        if (!IsUnderRoot(fullRoot, allowedRoot))
            return ("", $"Codebase map root must be under the configured workspace root '{allowedRoot}'.");
        if (HasReparsePointInPath(fullRoot, allowedRoot))
            return ("", "Codebase map root must not include symlink or reparse-point directories.");

        return (fullRoot, null);
    }

    private static string? ResolveCodebaseMapAllowedRoot(GatewayStartupContext startup)
    {
        var candidates = new[]
        {
            startup.WorkspacePath,
            ResolveConfiguredWorkspaceRoot(startup.Config.Tooling.WorkspaceRoot)
        };

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate!);
                if (HasReparsePointInPath(fullPath, fullPath))
                    continue;

                return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
        }

        return null;
    }

    private static string? ResolveConfiguredWorkspaceRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(value["env:".Length..]);

        return SecretResolver.Resolve(value) ?? value;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullPath, fullRoot, comparison))
            return true;

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, comparison);
    }

    private static bool HasReparsePointInPath(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(fullRoot))
            return true;

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            return true;

        if (IsDirectoryReparsePoint(fullRoot))
            return true;

        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return false;

        var current = fullRoot;
        foreach (var segment in relative
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(static segment => !string.IsNullOrWhiteSpace(segment) && segment != "."))
        {
            current = Path.Join(current, segment);
            if (Directory.Exists(current) && IsDirectoryReparsePoint(current))
                return true;
        }

        return false;
    }

    private static bool IsDirectoryReparsePoint(string path)
    {
        try
        {
            return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
