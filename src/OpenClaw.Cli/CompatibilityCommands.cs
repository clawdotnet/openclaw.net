using System.Text.Json;
using OpenClaw.Core.Compatibility;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class CompatibilityCommands
{
    public static int Run(string[] args)
        => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp(output);
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "catalog" or "list" => ListCatalog(rest, output),
            _ => UnknownSubcommand(subcommand, output, error)
        };
    }

    private static int ListCatalog(string[] args, TextWriter output)
    {
        var compatibilityStatus = GetOptionValue(args, "--status");
        var kind = GetOptionValue(args, "--kind");
        var category = GetOptionValue(args, "--category");
        var asJson = args.Contains("--json");

        var catalog = PublicCompatibilityCatalog.GetCatalog(compatibilityStatus, kind, category);
        if (asJson)
        {
            output.WriteLine(JsonSerializer.Serialize(catalog, CoreJsonContext.Default.CompatibilityCatalogResponse));
            return 0;
        }

        output.WriteLine($"Compatibility catalog v{catalog.Version} ({catalog.Items.Count} scenarios)");
        if (catalog.Items.Count == 0)
        {
            output.WriteLine("No compatibility scenarios matched the requested filters.");
            return 0;
        }

        foreach (var item in catalog.Items)
        {
            output.WriteLine($"- {item.Id} [{item.Kind}] {item.CompatibilityStatus}");
            output.WriteLine($"  Subject: {item.Subject}");
            output.WriteLine($"  Summary: {item.Summary}");
            output.WriteLine($"  Install: {item.InstallCommand}");
            if (!string.IsNullOrWhiteSpace(item.ConfigJsonExample))
                output.WriteLine($"  Config: {item.ConfigJsonExample}");
            if (item.InstallExtraPackages.Length > 0)
                output.WriteLine($"  Extras: {string.Join(", ", item.InstallExtraPackages)}");
            if (item.ExpectedToolNames.Length > 0)
                output.WriteLine($"  Tools: {string.Join(", ", item.ExpectedToolNames)}");
            if (item.ExpectedSkillNames.Length > 0)
                output.WriteLine($"  Skills: {string.Join(", ", item.ExpectedSkillNames)}");
            if (item.ExpectedDiagnosticCodes.Length > 0)
                output.WriteLine($"  Diagnostics: {string.Join(", ", item.ExpectedDiagnosticCodes)}");
            foreach (var guidance in item.Guidance)
                output.WriteLine($"  Note: {guidance}");
        }

        return 0;
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static int UnknownSubcommand(string subcommand, TextWriter output, TextWriter error)
    {
        error.WriteLine($"Unknown compatibility subcommand: {subcommand}");
        PrintHelp(output);
        return 2;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("""
            openclaw compatibility

            Usage:
              openclaw compatibility catalog [--status <compatible|incompatible>] [--kind <kind>] [--category <category>] [--json]
              openclaw compat catalog [--status <compatible|incompatible>] [--kind <kind>] [--category <category>] [--json]

            Notes:
              - The catalog is backed by the pinned public compatibility smoke manifest shipped with OpenClaw.NET.
              - Use --json when you want machine-readable output.
            """);
    }
}
