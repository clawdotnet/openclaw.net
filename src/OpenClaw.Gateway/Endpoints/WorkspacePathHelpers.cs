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
    /// (Linux) and case-insensitive comparison on case-insensitive file systems
    /// (Windows/macOS) so that symlink-resolved paths are compared correctly on every OS.
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

    /// <summary>
    /// Walks each component of <paramref name="path"/> and resolves any symlink or
    /// junction targets so that containment checks operate on the real filesystem path.
    /// Non-existent components are kept as-is (they cannot be links).
    /// Link resolution is best-effort: on failure (permissions, broken reparse point,
    /// unsupported FS) the logical <paramref name="path"/> component is kept.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var remaining = path[root.Length..];
        var parts = remaining.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var resolved = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var part in parts)
        {
            var candidate = resolved + Path.DirectorySeparatorChar + part;

            if (Directory.Exists(candidate))
            {
                try
                {
                    var target = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                    resolved = target?.FullName ?? candidate;
                }
                catch
                {
                    resolved = candidate;
                }
            }
            else if (File.Exists(candidate))
            {
                try
                {
                    var target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                    resolved = target?.FullName ?? candidate;
                }
                catch
                {
                    resolved = candidate;
                }
            }
            else
            {
                // Component does not exist yet — keep the logical path.
                resolved = candidate;
            }
        }

        return resolved;
    }
}
