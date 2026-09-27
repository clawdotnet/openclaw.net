namespace OpenClaw.LayaService;

public sealed record CommandInvocation(
    string Command,
    CommandOptions Options,
    IReadOnlyList<string> Arguments);

public sealed class CommandOptions
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _values;

    internal CommandOptions(Dictionary<string, List<string>> values)
    {
        _values = values.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    public bool Contains(string name) => _values.ContainsKey(name);

    public string? Get(string name)
        => _values.TryGetValue(name, out var values) ? values[0] : null;

    public IReadOnlyList<string> GetMany(string name)
        => _values.TryGetValue(name, out var values) ? values : Array.Empty<string>();
}

public static class CommandLine
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> OptionsByCommand =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["serve"] = ["manifest", "calibration", "port", "device", "checkpoint", "threads"],
            ["download"] = ["destination", "revision", "checkpoint"],
            ["evaluate"] = ["endpoint", "output"],
            ["calibrate"] = ["fit", "validate", "output"],
            ["report"] = ["labels", "output", "plot"]
        };

    public static CommandInvocation Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || !OptionsByCommand.TryGetValue(args[0], out var allowedOptions))
        {
            throw new ArgumentException("Invalid command line.", nameof(args));
        }

        var command = args[0];
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var positionalArguments = new List<string>();

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (command is not ("evaluate" or "report") || positionalArguments.Count != 0)
                {
                    throw new ArgumentException("Invalid command line.", nameof(args));
                }

                positionalArguments.Add(argument);
                continue;
            }

            var name = argument[2..];
            if (name.Length == 0 || !allowedOptions.Contains(name))
            {
                throw new ArgumentException("Invalid command line.", nameof(args));
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid command line.", nameof(args));
            }

            if (!values.TryGetValue(name, out var optionValues))
            {
                optionValues = [];
                values.Add(name, optionValues);
            }
            else if (name != "checkpoint")
            {
                throw new ArgumentException("Invalid command line.", nameof(args));
            }

            optionValues.Add(args[++index]);
        }

        return new CommandInvocation(command, new CommandOptions(values), positionalArguments.ToArray());
    }
}