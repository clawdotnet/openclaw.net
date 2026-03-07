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

        // Span-based matching — avoids the string[] allocation from Split('*').
        var remaining = pattern.AsSpan();
        var valueIndex = 0;
        var isFirst = true;

        while (remaining.Length > 0)
        {
            var starPos = remaining.IndexOf('*');
            if (starPos < 0)
            {
                // No more wildcards — the rest of the pattern must match the suffix of value
                return value.AsSpan(valueIndex).EndsWith(remaining, comparison);
            }

            var segment = remaining[..starPos];
            remaining = remaining[(starPos + 1)..];

            if (segment.Length == 0)
            {
                isFirst = false;
                continue;
            }

            if (isFirst)
            {
                // First segment (pattern doesn't start with '*') must match prefix
                if (!value.AsSpan(valueIndex).StartsWith(segment, comparison))
                    return false;
                valueIndex += segment.Length;
                isFirst = false;
            }
            else
            {
                // Middle segment — find next occurrence in value
                var found = value.AsSpan(valueIndex).IndexOf(segment, comparison);
                if (found < 0)
                    return false;
                valueIndex += found + segment.Length;
            }
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

