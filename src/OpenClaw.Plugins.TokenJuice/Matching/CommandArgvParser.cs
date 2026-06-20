namespace OpenClaw.Plugins.TokenJuice.Matching;

public static class CommandArgvParser
{
    public static List<string> Parse(string? command, List<string>? argv = null)
    {
        if (argv is { Count: > 0 }) return argv;
        if (string.IsNullOrWhiteSpace(command)) return [];

        var tokens = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
