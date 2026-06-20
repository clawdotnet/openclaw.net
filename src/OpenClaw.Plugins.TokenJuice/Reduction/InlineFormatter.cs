namespace OpenClaw.Plugins.TokenJuice.Reduction;

public static class InlineFormatter
{
    public static string Format(string summary, Dictionary<string, int> facts, int exitCode, int? maxInlineChars = null)
    {
        var parts = new List<string>();

        if (exitCode != 0)
            parts.Add($"exit {exitCode}");

        var nonZeroFacts = facts
            .Where(kv => kv.Value != 0)
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        if (nonZeroFacts.Count > 0)
            parts.Add(string.Join("; ", nonZeroFacts));

        var trimmedSummary = summary.Trim();
        if (trimmedSummary.Length > 0)
            parts.Add(trimmedSummary);

        var result = string.Join("\n", parts).Trim();

        if (maxInlineChars is > 0 && result.Length > maxInlineChars)
        {
            var half = Math.Max(1, (maxInlineChars.Value - 32) / 2);
            result = $"{result[..half]}\n... omitted chars ...\n{result[^half..]}";
        }

        return result;
    }
}
