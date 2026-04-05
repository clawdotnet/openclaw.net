using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

/// <summary>
/// Shared path resolution utility for configuration paths.
/// Used by both <see cref="ConfigValidator"/> and <see cref="DoctorCheck"/>.
/// </summary>
internal static class ConfigPathResolver
{
    /// <summary>
    /// Resolves a configured path string: applies secret substitution,
    /// expands <c>~/</c> to the user profile directory, and trims whitespace.
    /// Returns empty string if the path is null/empty or resolves to nothing.
    /// </summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var resolved = SecretResolver.Resolve(path);
        if (string.IsNullOrWhiteSpace(resolved))
            return "";

        if (resolved.StartsWith("~/", StringComparison.Ordinal) || string.Equals(resolved, "~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            resolved = resolved.Length == 1 ? home : Path.Combine(home, resolved[2..]);
        }

        return resolved.Trim();
    }
}
