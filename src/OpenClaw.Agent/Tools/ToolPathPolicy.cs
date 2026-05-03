using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

internal static class ToolPathPolicy
{
    private const int MaxSymlinkResolutionDepth = 64;

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
        => ResolveRealPath(path, new HashSet<string>(GetPathComparer()), depth: 0);

    private static string ResolveRealPath(string path, HashSet<string> visited, int depth)
    {
        var full = Path.GetFullPath(path);
        if (depth >= MaxSymlinkResolutionDepth || !visited.Add(full))
            return full;

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
            if (Path.IsPathRooted(segment))
                return Path.GetFullPath(current);

            current = Path.Join(current, segment);
            var resolved = TryResolveLinkTarget(current);
            if (resolved is not null)
                current = ResolveRealPath(resolved, visited, depth + 1);
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
        catch (IOException)
        {
            // Expected when probing inaccessible or broken filesystem entries.
        }
        catch (UnauthorizedAccessException)
        {
            // Expected when probing roots the current process cannot inspect.
        }

        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is not null) return target.FullName;
        }
        catch (IOException)
        {
            // Expected when probing inaccessible or broken filesystem entries.
        }
        catch (UnauthorizedAccessException)
        {
            // Expected when probing roots the current process cannot inspect.
        }

        return null;
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
