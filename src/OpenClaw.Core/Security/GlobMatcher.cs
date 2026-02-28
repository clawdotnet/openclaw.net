namespace OpenClaw.Core.Security;

public static class GlobMatcher
{
    /// <summary>
    /// Matches a value against a simple glob pattern supporting '*' as "match any sequence".
    /// Case-sensitive by default.
    /// </summary>
    public static bool IsMatch(string pattern, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        if (pattern == "*")
            return true;

        if (pattern.Length == 0)
            return value.Length == 0;

        // Fast path: no wildcard
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(pattern, value, comparison);

        // Split on '*' and ensure all segments occur in order.
        var parts = pattern.Split('*');
        var index = 0;

        // Leading segment must match prefix when pattern doesn't start with '*'
        if (!pattern.StartsWith('*'))
        {
            var first = parts[0];
            if (!value.StartsWith(first, comparison))
                return false;
            index = first.Length;
        }

        // Middle segments must appear in order
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
                continue;

            var next = value.IndexOf(part, index, comparison);
            if (next < 0)
                return false;
            index = next + part.Length;
        }

        // Trailing segment must match suffix when pattern doesn't end with '*'
        if (!pattern.EndsWith('*'))
        {
            var last = parts[^1];
            return value.AsSpan(index).EndsWith(last, comparison);
        }

        return true;
    }

    /// <summary>
    /// Allow/deny evaluator where deny wins. Empty allow list means "deny all".
    /// </summary>
    public static bool IsAllowed(string[] allowGlobs, string[] denyGlobs, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        foreach (var deny in denyGlobs)
        {
            if (!string.IsNullOrWhiteSpace(deny) && IsMatch(deny.Trim(), value, comparison))
                return false;
        }

        if (allowGlobs.Length == 0)
            return false;

        foreach (var allow in allowGlobs)
        {
            if (!string.IsNullOrWhiteSpace(allow) && IsMatch(allow.Trim(), value, comparison))
                return true;
        }

        return false;
    }
}

