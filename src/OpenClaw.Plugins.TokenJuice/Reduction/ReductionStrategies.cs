using System.Text.RegularExpressions;
using OpenClaw.Plugins.TokenJuice.Formatters;
using OpenClaw.Plugins.TokenJuice.Rules;

namespace OpenClaw.Plugins.TokenJuice.Reduction;

public static class ReductionStrategies
{
    public static (string summary, Dictionary<string, int> facts) Reduce(
        TokenJuiceRule rule, string rawText, int exitCode)
    {
        // Step 1: Strip ANSI
        var text = (rule.Transforms?.StripAnsi ?? false)
            ? TextFormatters.StripAnsi(rawText) : rawText;

        // Step 2: OutputMatches
        if (rule.OutputMatches is { Count: > 0 })
        {
            foreach (var om in rule.OutputMatches)
            {
                try
                {
                    if (Regex.IsMatch(text, om.Pattern, RegexOptions.Multiline, TimeSpan.FromMilliseconds(500)))
                        return (om.Message, new Dictionary<string, int>());
                }
                catch { }
            }
        }

        // Step 3: Split into lines
        var lines = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.None))
            lines.Add(line);

        // Step 4: Trim empty edges
        if (rule.Transforms?.TrimEmptyEdges ?? false)
            lines = TextFormatters.TrimEmptyEdges(lines);

        // Step 5: Dedupe adjacent
        if (rule.Transforms?.DedupeAdjacent ?? false)
            lines = TextFormatters.DedupeAdjacent(lines);

        // Step 6: Apply skip/keep filters
        var counterLines = new List<string>(lines);

        if (rule.Filters?.SkipPatterns is { Count: > 0 })
        {
            var compiled = rule.Filters.SkipPatterns
                .Select(p => { try { return new Regex(p, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)); } catch { return null; }})
                .Where(r => r is not null)
                .ToList();

            lines = lines.Where(line => !compiled.Any(r => r!.IsMatch(line))).ToList();
        }

        if (rule.Filters?.KeepPatterns is { Count: > 0 })
        {
            var compiled = rule.Filters.KeepPatterns
                .Select(p => { try { return new Regex(p, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)); } catch { return null; }})
                .Where(r => r is not null)
                .ToList();

            if (compiled.Count > 0)
            {
                var kept = lines.Where(line => compiled.Any(r => r!.IsMatch(line))).ToList();
                if (kept.Count > 0) lines = kept;
            }
        }

        // Step 7: onEmpty
        if (lines.Count == 0 && rule.OnEmpty is not null)
            return (rule.OnEmpty, new Dictionary<string, int>());

        // Step 8: Counters
        var facts = new Dictionary<string, int>();
        if (rule.Counters is { Count: > 0 })
        {
            var source = rule.CounterSource == "preKeep" ? counterLines : lines;
            foreach (var counter in rule.Counters)
            {
                if (string.IsNullOrEmpty(counter.Pattern)) continue;
                try
                {
                    var re = TextFormatters.CompilePattern(counter.Pattern, counter.Flags);
                    facts[counter.Name] = TextFormatters.CountPattern(source, re);
                }
                catch { }
            }
        }

        // Step 9: Head/Tail summarization
        var head = rule.Summarize?.Head ?? 8;
        var tail = rule.Summarize?.Tail ?? 8;
        if (exitCode != 0 && (rule.Failure?.PreserveOnFailure ?? false))
        {
            head = rule.Failure?.Head ?? 12;
            tail = rule.Failure?.Tail ?? 12;
        }

        var compacted = TextFormatters.HeadTail(lines, head, tail);
        return (string.Join("\n", compacted).Trim(), facts);
    }
}
