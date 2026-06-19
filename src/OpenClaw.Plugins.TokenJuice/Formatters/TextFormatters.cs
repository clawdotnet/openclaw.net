using System.Text.RegularExpressions;

namespace OpenClaw.Plugins.TokenJuice.Formatters;

public static class TextFormatters
{
    private static readonly Regex AnsiPattern = new(
        "\\u001b(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~]|\\][^\\u0007]*(?:\\u0007|\\u001b\\\\))",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    public static string StripAnsi(string text) => AnsiPattern.Replace(text, "");

    public static List<string> TrimEmptyEdges(List<string> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && string.IsNullOrWhiteSpace(lines[start]))
            start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
            end--;
        return lines.GetRange(start, end - start);
    }

    public static List<string> DedupeAdjacent(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        string? last = null;
        foreach (var line in lines)
        {
            if (line != last)
                result.Add(line);
            last = line;
        }
        return result;
    }

    public static List<string> HeadTail(List<string> lines, int head, int tail)
    {
        if (lines.Count <= head + tail) return lines;
        var omitted = lines.Count - head - tail;
        return [.. lines.Take(head),
                $"... omitted {omitted} lines ...",
                .. lines.Skip(lines.Count - tail)];
    }

    public static int CountPattern(List<string> lines, Regex pattern) =>
        lines.Count(line => pattern.IsMatch(line));

    public static Regex CompilePattern(string pattern, string? flags = null)
    {
        var options = RegexOptions.Compiled;
        if (flags is not null)
        {
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m')) options |= RegexOptions.Multiline;
        }
        return new Regex(pattern, options, TimeSpan.FromMilliseconds(500));
    }
}
