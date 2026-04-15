namespace OpenClaw.Cli;

internal sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];
    public List<string> Files { get; } = [];
    public bool ShowHelp { get; private set; }
    public IReadOnlyDictionary<string, List<string>> Options => _options;

    public static CliArgs Parse(string[] args)
    {
        var parsed = new CliArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help")
            {
                parsed.ShowHelp = true;
                continue;
            }

            if (a == "--")
            {
                parsed.Positionals.AddRange(args[(i + 1)..]);
                break;
            }

            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positionals.Add(a);
                continue;
            }

            if (a is "--no-stream" or "--apply" or "--non-interactive")
            {
                parsed._flags.Add(a);
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (a is "--no-stream" or "--apply" or "--non-interactive")
                {
                    parsed._flags.Add(a);
                    continue;
                }

                throw new ArgumentException($"Missing value for {a}");
            }

            var value = args[++i];
            if (a == "--file")
            {
                parsed.Files.Add(value);
                continue;
            }

            if (!parsed._options.TryGetValue(a, out var values))
            {
                values = [];
                parsed._options[a] = values;
            }

            values.Add(value);
        }

        return parsed;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? GetOption(string name)
        => _options.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;
}
