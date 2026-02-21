namespace OpenClaw.Core.Security;

/// <summary>
/// Input sanitization helpers shared across tools.
/// Defence-in-depth layer to prevent injection attacks.
/// </summary>
public static class InputSanitizer
{
    /// <summary>
    /// Characters that can be used for shell metacharacter injection.
    /// </summary>
    private static readonly char[] ShellMetaChars = [';', '|', '&', '`', '$', '(', ')', '{', '}', '<', '>', '\n', '\r'];

    /// <summary>
    /// Validate that a string is free of shell metacharacters.
    /// Returns <c>null</c> if clean, or an error message describing what was found.
    /// </summary>
    public static string? CheckShellMetaChars(string value, string parameterName)
    {
        var idx = value.AsSpan().IndexOfAny(ShellMetaChars);
        if (idx >= 0)
            return $"Error: '{parameterName}' contains disallowed character '{value[idx]}'. " +
                   "Shell metacharacters (;|&`$(){{}}\\n\\r<>) are not permitted for security reasons.";
        return null;
    }

    /// <summary>
    /// Strip CRLF (carriage return / line feed) from IMAP/SMTP protocol inputs
    /// to prevent command injection via line-breaking.
    /// </summary>
    public static string StripCrlf(string value)
        => value.Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal);

    /// <summary>
    /// Validate that a memory note key does not contain path traversal sequences.
    /// Returns <c>null</c> if clean, or an error message.
    /// </summary>
    public static string? CheckMemoryKey(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal) ||
            key.Contains('/', StringComparison.Ordinal) ||
            key.Contains('\\', StringComparison.Ordinal) ||
            key.Contains('\0'))
        {
            return "Error: Key contains disallowed characters (path separators, '..' or null bytes).";
        }
        return null;
    }

    /// <summary>
    /// Validate that an IMAP folder name contains only safe characters.
    /// IMAP folder names should not contain control characters.
    /// </summary>
    public static string? CheckImapFolderName(string folder)
    {
        foreach (var c in folder)
        {
            if (char.IsControl(c))
                return $"Error: Folder name contains control character (0x{(int)c:X2}). " +
                       "Only printable characters are allowed in folder names.";
        }
        return null;
    }
}
