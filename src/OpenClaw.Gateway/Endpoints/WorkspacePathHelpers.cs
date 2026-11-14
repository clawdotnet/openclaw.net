using System.Runtime.InteropServices;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// Shared path-safety helpers used by workspace and digital-employee endpoints.
/// </summary>
internal static class WorkspacePathHelpers
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is the directory itself or a
    /// descendant of it.  Uses case-sensitive comparison on case-sensitive file systems
    /// (Unix) and case-insensitive comparison on case-insensitive file systems
    /// (Windows) so that symlink-resolved paths are compared correctly on every OS.
    /// </summary>
    internal static bool IsInsideDirectory(string path, string directory)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var dir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                  + Path.DirectorySeparatorChar;
        return path.StartsWith(dir, comparison)
               || string.Equals(path,
                   directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   comparison);
    }

    // The configured root is trusted. Refuse links below it, including dangling links,
    // because opening them for creation can write outside the workspace.
    internal static bool IsSafeWorkspacePath(string path, string root)
    {
        if (!IsInsideDirectory(path, root))
            return false;

        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
            return true;

        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Join(current, component);
            if (new FileInfo(current).LinkTarget is not null)
                return false;
        }

        return true;
    }
}
