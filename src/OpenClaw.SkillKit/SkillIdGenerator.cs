using System.Globalization;
using System.Text;

namespace OpenClaw.SkillKit;

public static class SkillIdGenerator
{
    public static string Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "general.untitled_skill";

        var tokens = Tokenize(name);
        if (tokens.Count == 0)
            return "general.untitled_skill";

        if (tokens.Count >= 2 && tokens[0] == "asp" && tokens[1] == "net")
        {
            tokens[0] = "aspnet";
            tokens.RemoveAt(1);
        }

        if (tokens.Count > 2 && tokens[^1] == "extractor")
            tokens.RemoveAt(tokens.Count - 1);

        var prefix = tokens[0];
        var suffixTokens = tokens.Count > 1 ? tokens.Skip(1).ToArray() : ["skill"];
        return $"{prefix}.{string.Join('_', suffixTokens)}";
    }

    private static List<string> Tokenize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSeparator = true;

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
