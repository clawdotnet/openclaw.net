using System.Text.RegularExpressions;
using OpenClaw.Plugins.TokenJuice.Rules;

namespace OpenClaw.Plugins.TokenJuice.Matching;

public static class RuleMatcher
{
    public static TokenJuiceRule? SelectRule(
        IReadOnlyList<TokenJuiceRule> rules,
        string toolName,
        string? command,
        List<string>? argv,
        string content,
        int exitCode)
    {
        foreach (var rule in rules)
        {
            if (RuleMatches(rule, toolName, command, argv, content, exitCode))
                return rule;
        }
        return null;
    }

    private static bool RuleMatches(
        TokenJuiceRule rule,
        string toolName,
        string? command,
        List<string>? argv,
        string content,
        int exitCode)
    {
        var match = rule.Match;
        if (match is null) return true;

        var normalizedTool = command is not null ? "exec" : toolName;
        if (match.ToolNames is { Count: > 0 })
        {
            if (!match.ToolNames.Contains(normalizedTool, StringComparer.Ordinal) &&
                !match.ToolNames.Contains(toolName, StringComparer.Ordinal))
                return false;
        }

        var tokens = CommandArgvParser.Parse(command, argv);

        if (match.Argv0 is { Count: > 0 })
        {
            if (tokens.Count == 0 || !match.Argv0.Contains(tokens[0], StringComparer.Ordinal))
                return false;
        }

        if (match.ArgvIncludes is { Count: > 0 })
        {
            if (!match.ArgvIncludes.Any(entry =>
                entry.All(needle => tokens.Contains(needle, StringComparer.Ordinal))))
                return false;
        }

        if (match.ArgvIncludesAny is { Count: > 0 })
        {
            if (!match.ArgvIncludesAny.Any(entry =>
                entry.Any(needle => tokens.Contains(needle, StringComparer.Ordinal))))
                return false;
        }

        var cmdText = command ?? string.Join(" ", tokens);
        var cmdLower = cmdText.ToLowerInvariant();

        if (match.CommandIncludes is { Count: > 0 })
        {
            if (!match.CommandIncludes.All(needle => cmdLower.Contains(needle.ToLowerInvariant())))
                return false;
        }

        if (match.CommandIncludesAny is { Count: > 0 })
        {
            if (!match.CommandIncludesAny.Any(needle => cmdLower.Contains(needle.ToLowerInvariant())))
                return false;
        }

        if (match.CommandRegex is { Length: > 0 })
        {
            try
            {
                if (!Regex.IsMatch(cmdText, match.CommandRegex, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    return false;
            }
            catch { return false; }
        }

        if (match.ExitCodes is { Count: > 0 })
        {
            if (!match.ExitCodes.Contains(exitCode))
                return false;
        }

        if (match.OutputRegex is { Length: > 0 })
        {
            try
            {
                if (!Regex.IsMatch(content, match.OutputRegex, RegexOptions.Multiline, TimeSpan.FromMilliseconds(500)))
                    return false;
            }
            catch { return false; }
        }

        return true;
    }
}
