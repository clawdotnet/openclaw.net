using OpenClaw.Testing;

namespace OpenClaw.Cli;

internal static class HarnessCommands
{
    public static Task<int> RunAsync(string[] args)
        => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp(output);
            return 0;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        if (subcommand is not ("test" or "regression"))
        {
            error.WriteLine($"Unknown harness subcommand: {subcommand}");
            PrintHelp(output);
            return 2;
        }

        var parsed = CliArgs.Parse(args.Skip(1).ToArray());
        if (parsed.ShowHelp)
        {
            PrintHelp(output);
            return 0;
        }

        return await RunRegressionAsync(parsed, output);
    }

    internal static async Task<int> RunRegressionAliasAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintAliasHelp(output);
            return 0;
        }

        if (!string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"Unknown regression subcommand: {args[0]}");
            PrintAliasHelp(output);
            return 2;
        }

        var forwarded = new[] { "test" }.Concat(args.Skip(1)).ToArray();
        return await RunAsync(forwarded, output, error);
    }

    private static async Task<int> RunRegressionAsync(CliArgs parsed, TextWriter output)
    {
        var options = new HarnessRegressionOptions
        {
            ConfigPath = parsed.GetOption("--config"),
            Category = parsed.GetOption("--category"),
            Offline = true,
            Strict = parsed.HasFlag("--strict"),
            ProposalId = parsed.GetOption("--proposal"),
            OutputPath = parsed.GetOption("--output")
        };

        var report = await new HarnessRegressionRunner().RunAsync(options, CancellationToken.None);
        var renderJson = parsed.HasFlag("--json");
        var rendered = renderJson
            ? HarnessRegressionReportFormatter.ToJson(report)
            : HarnessRegressionReportFormatter.ToText(report);

        var outputPath = parsed.GetOption("--output");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(fullPath, rendered);
        }

        output.Write(rendered);
        if (!renderJson && !string.IsNullOrWhiteSpace(outputPath))
            output.WriteLine($"Report written: {Path.GetFullPath(outputPath)}");

        return HarnessRegressionRunner.GetExitCode(report);
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw harness

            Usage:
              openclaw harness test [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]
              openclaw harness regression [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]

            Categories:
              onboarding, security, approvals, memory, providers, tools, mcp,
              openai_compat, sessions, harness, deployment, docs

            Notes:
              - Runs offline by default and does not require provider keys.
              - Provider/network checks are structural only unless a future online mode is added.
              - Strict mode treats required warnings and skips as failures.
            """);
    }

    private static void PrintAliasHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw regression

            Usage:
              openclaw regression test [--config <path>] [--category <name>] [--json] [--offline] [--strict] [--proposal <id>] [--output <path>]
            """);
    }
}
