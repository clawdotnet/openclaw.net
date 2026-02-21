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

        // If the path exists, resolve symlinks based on actual entry type
        if (File.Exists(full))
        {
            var resolved = File.ResolveLinkTarget(full, returnFinalTarget: true);
            return resolved?.FullName ?? full;
        }

        if (Directory.Exists(full))
        {
            var resolved = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            return resolved?.FullName ?? full;
        }

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
            var resolvedDir = Directory.ResolveLinkTarget(dir, returnFinalTarget: true);
            var realDir = resolvedDir?.FullName ?? dir;
            return Path.Combine(realDir, tail);
        }

        return full;
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
