namespace OpenClaw.Cli;

internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];
    public List<string> Files { get; } = [];
    public bool ShowHelp { get; private set; }

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

            if (a is "--no-stream")
            {
                parsed._flags.Add(a);
                continue;
            }

            if (a is "--url" or "--token" or "--model" or "--system" or "--temperature" or "--max-tokens" or "--file")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {a}");

                var value = args[++i];
                if (a == "--file")
                    parsed.Files.Add(value);
                else
                    parsed._options[a] = value;
                continue;
            }

            throw new ArgumentException($"Unknown option: {a}");
        }

        return parsed;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? GetOption(string name)
        => _options.TryGetValue(name, out var value) ? value : null;
}

