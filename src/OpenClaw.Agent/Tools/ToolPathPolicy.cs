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
    /// Resolves the real filesystem path, following symlinks.
    /// For paths that don't exist yet (e.g. write targets), resolves the deepest
    /// existing ancestor and appends the remaining segments.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);

        // Try resolving as a symlink first, without checking existence.
        // This eliminates the TOCTOU window between File.Exists and ResolveLinkTarget.
        var resolved = TryResolveLinkTarget(full);
        if (resolved is not null)
            return resolved;

        // Not a symlink — if the path exists as-is, return it directly
        if (File.Exists(full) || Directory.Exists(full))
            return full;

        // Path doesn't exist yet — resolve the deepest existing ancestor
        // and append the remaining tail. This prevents writing through a
        // symlinked parent directory.
        var dir = Path.GetDirectoryName(full);
        var tail = Path.GetFileName(full);

        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            tail = Path.Combine(Path.GetFileName(dir), tail);
            dir = Path.GetDirectoryName(dir);
        }

        if (!string.IsNullOrEmpty(dir))
        {
            if (IsPathRoot(dir))
                return Path.Combine(dir, tail);

            var realDir = TryResolveLinkTarget(dir) ?? dir;
            return Path.Combine(realDir, tail);
        }

        return full;
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

    private static bool IsPathRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(root),
            comparison);
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
