using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

internal static class ToolPathPolicy
{
    public static bool IsReadAllowed(ToolingConfig config, string path) =>
        IsPathAllowed(config.AllowedReadRoots, path);

    public static bool IsWriteAllowed(ToolingConfig config, string path) =>
        IsPathAllowed(config.AllowedWriteRoots, path);

    private static bool IsPathAllowed(string[] roots, string path)
    {
        if (roots.Length == 0)
            return false;

        if (roots.Length == 1 && roots[0] == "*")
            return true;

        var fullPath = ResolveRealPath(path);
        foreach (var root in roots)
        {
            if (root == "*")
                return true;

            var fullRoot = ResolveRealPath(root);
            if (IsUnderRoot(fullPath, fullRoot))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the real filesystem path, following symlinked ancestors and final targets.
    /// For paths that do not exist yet, existing ancestors are still resolved and the
    /// remaining segments are appended.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        var current = root;
        var remaining = full[root.Length..];
        var segments = remaining.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var resolved = TryResolveLinkTarget(current);
            if (resolved is not null)
                current = ResolveRealPath(resolved);
        }

        return Path.GetFullPath(current);
    }

    /// <summary>
    /// Attempts to resolve a symlink target for the given path.
    /// Returns null if the path is not a symlink, does not exist, or cannot be accessed.
    /// </summary>
    private static string? TryResolveLinkTarget(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null) return target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null) return target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static bool IsUnderRoot(string fullPath, string fullRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullPath, fullRoot, comparison))
            return true;

        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        return fullPath.StartsWith(fullRoot, comparison);
    }
}
