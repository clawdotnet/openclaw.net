using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
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

        return (fullRoot, null);
    }

    private static string? ResolveCodebaseMapAllowedRoot(GatewayStartupContext startup)
    {
        var candidates = new[]
        {
            startup.WorkspacePath,
            ResolveConfiguredWorkspaceRoot(startup.Config.Tooling.WorkspaceRoot),
            Directory.GetCurrentDirectory()
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                return Path.GetFullPath(candidate);
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

        return value;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        if (string.Equals(fullPath, fullRoot, StringComparison.Ordinal))
            return true;

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }
}
