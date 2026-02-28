namespace OpenClaw.Core.Security;

public enum AllowlistSemantics : byte
{
    Legacy,
    Strict
}

public static class AllowlistPolicy
{
    public static AllowlistSemantics ParseSemantics(string? value)
    {
        if (string.Equals(value, "strict", StringComparison.OrdinalIgnoreCase))
            return AllowlistSemantics.Strict;
        return AllowlistSemantics.Legacy;
    }

    public static bool IsAllowed(string[] allowlist, string value, AllowlistSemantics semantics, StringComparison comparison = StringComparison.Ordinal)
    {
        if (allowlist.Length == 0)
        {
            // Legacy: empty = allow all (historical Telegram behavior)
            return semantics == AllowlistSemantics.Legacy;
        }

        foreach (var entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var pat = entry.Trim();
            if (pat == "*")
                return true;

            if (GlobMatcher.IsMatch(pat, value, comparison))
                return true;
        }

        return false;
    }
}

