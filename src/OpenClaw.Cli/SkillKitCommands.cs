using OpenClaw.SkillKit;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.Cli;

internal static class SkillKitCommands
{
    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp(output);
            return 0;
        }

        var command = args[0];
        var rest = args.Skip(1).ToArray();
        var service = SkillPackageService.CreateDefault();

        try
        {
            return command switch
            {
                "new" => await NewAsync(service, rest, output, error, currentDirectory),
                "list" or "ls" => await ListAsync(service, rest, output, currentDirectory),
                "validate" => await ValidateAsync(service, rest, output, error, currentDirectory),
                "critique" => await CritiqueAsync(service, rest, output, error, currentDirectory),
                "generate" => await GenerateAsync(service, rest, output, error, currentDirectory),
                "package" => await PackageAsync(service, rest, output, error, currentDirectory),
                "run" => await RunSkillAsync(service, rest, output, error, currentDirectory),
                _ => Unknown(command, error)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static async Task<int> NewAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var name = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            await error.WriteLineAsync("Usage: openclaw skill new <name> [--category <category>] [--template <template>] [--output <path>] [--force]");
            return 2;
        }

        var category = GetOptionValue(args, "--category") ?? "general";
        var template = GetOptionValue(args, "--template") ?? category;
        var skillsRoot = ResolveRoot(currentDirectory, GetOptionValue(args, "--output"));
        var force = args.Contains("--force");
        var package = await service.CreateNewAsync(name, category, template, skillsRoot, force);

        await output.WriteLineAsync($"Created skill: {package.Manifest.Id}");
        await output.WriteLineAsync($"Path: {package.RootPath}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Next steps:");
        await output.WriteLineAsync($"- openclaw skill validate {package.Manifest.Id}");
        await output.WriteLineAsync($"- openclaw skill critique {package.Manifest.Id}");
        await output.WriteLineAsync($"- openclaw skill run {package.Manifest.Id} --input <path> --dry-run");
        return 0;
    }

    private static async Task<int> ListAsync(SkillPackageService service, string[] args, TextWriter output, string currentDirectory)
    {
        var skillsRoot = ResolveRoot(currentDirectory, GetOptionValue(args, "--output"));
        var packages = await service.ListAsync(skillsRoot);
        if (packages.Count == 0)
        {
            await output.WriteLineAsync("No SkillKit skills found.");
            return 0;
        }

        await output.WriteLineAsync("ID                           Name                                  Category     Version");
        foreach (var manifest in packages
            .OrderBy(static package => package.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static package => package.Manifest))
        {
            await output.WriteLineAsync($"{Truncate(manifest.Id, 28),-28} {Truncate(manifest.Name, 37),-37} {Truncate(manifest.Category, 12),-12} {manifest.Version}");
        }

        return 0;
    }

    private static async Task<int> ValidateAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var skillRef = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(skillRef))
        {
            await error.WriteLineAsync("Usage: openclaw skill validate <skill-id|path> [--output <path>]");
            return 2;
        }

        var result = await service.ValidateAsync(skillRef, ResolveRoot(currentDirectory, GetOptionValue(args, "--output")));
        PrintValidation(result, output);
        return result.Passed ? 0 : 1;
    }

    private static async Task<int> CritiqueAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var skillRef = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(skillRef))
        {
            await error.WriteLineAsync("Usage: openclaw skill critique <skill-id|path> [--output <path>]");
            return 2;
        }

        var path = await service.CritiqueAsync(skillRef, ResolveRoot(currentDirectory, GetOptionValue(args, "--output")));
        await output.WriteLineAsync($"Critique written: {path}");
        return 0;
    }

    private static async Task<int> GenerateAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var skillRef = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(skillRef))
        {
            await error.WriteLineAsync("Usage: openclaw skill generate <skill-id|path> [--output <path>] [--force]");
            return 2;
        }

        await service.GenerateAsync(skillRef, ResolveRoot(currentDirectory, GetOptionValue(args, "--output")), args.Contains("--force"));
        await output.WriteLineAsync("Generated missing skill files.");
        return 0;
    }

    private static async Task<int> PackageAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var skillRef = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(skillRef))
        {
            await error.WriteLineAsync("Usage: openclaw skill package <skill-id|path> [--output <path>] [--package-output <path>] [--force]");
            return 2;
        }

        var skillsRoot = ResolveRoot(currentDirectory, GetOptionValue(args, "--output"));
        var packagesRoot = ResolvePackagesRoot(currentDirectory, GetOptionValue(args, "--package-output"));
        var zipPath = await service.PackageAsync(skillRef, skillsRoot, packagesRoot, args.Contains("--force"));
        await output.WriteLineAsync($"Package written: {zipPath}");
        return 0;
    }

    private static async Task<int> RunSkillAsync(SkillPackageService service, string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        var skillRef = FirstPositional(args);
        if (string.IsNullOrWhiteSpace(skillRef))
        {
            await error.WriteLineAsync("Usage: openclaw skill run <skill-id|path> --input <path> [--dry-run] [--output <path>]");
            return 2;
        }

        if (!args.Contains("--dry-run"))
        {
            await error.WriteLineAsync("Full SkillKit runtime execution is not implemented yet. Re-run with --dry-run to inspect the execution plan.");
            return 2;
        }

        var inputs = GetOptionValues(args, "--input").Select(path => ResolvePath(currentDirectory, path)).ToArray();
        if (inputs.Length == 0)
        {
            await error.WriteLineAsync("At least one --input <path> is required for skill dry-run planning.");
            return 2;
        }

        var plan = await service.PlanRunAsync(skillRef, ResolveRoot(currentDirectory, GetOptionValue(args, "--output")), inputs);
        PrintRunPlan(plan, output);
        return plan.InputIssues.Any(static issue => issue.Severity == SkillValidationSeverity.Error) ? 1 : 0;
    }

    private static void PrintValidation(SkillValidationResult result, TextWriter output)
    {
        output.WriteLine($"Skill validation: {(result.Passed ? "PASS" : "FAIL")}");
        output.WriteLine();
        foreach (var group in result.Issues.GroupBy(static issue => issue.Area))
        {
            output.WriteLine($"{group.Key}:");
            foreach (var issue in group)
                output.WriteLine($"[{Status(issue.Severity)}] {issue.Message}");
            output.WriteLine();
        }
    }

    private static void PrintRunPlan(SkillRunPlan plan, TextWriter output)
    {
        output.WriteLine($"Skill: {plan.Manifest.Name}");
        output.WriteLine();
        output.WriteLine("Execution Plan:");
        var index = 1;
        foreach (var step in plan.Manifest.Workflow.Steps)
            output.WriteLine($"{index++}. {step.Id}");
        output.WriteLine();
        output.WriteLine("Inputs:");
        foreach (var issue in plan.InputIssues)
            output.WriteLine($"[{Status(issue.Severity)}] {issue.FileName}");
        output.WriteLine();
        output.WriteLine("Tool Policy:");
        output.WriteLine("Allowed:");
        PrintList(plan.Manifest.Tools.Allowed, output);
        output.WriteLine();
        output.WriteLine("Forbidden:");
        PrintList(plan.Manifest.Tools.Forbidden, output);
        output.WriteLine();
        output.WriteLine("Approval Required:");
        PrintList(plan.Manifest.HumanApproval.RequiredFor.Count > 0 ? plan.Manifest.HumanApproval.RequiredFor : plan.Manifest.Tools.ApprovalRequired, output);
        output.WriteLine();
        output.WriteLine("Dry run complete. No model calls or tool calls were executed.");
    }

    private static void PrintList(IReadOnlyList<string> values, TextWriter output)
    {
        if (values.Count == 0)
        {
            output.WriteLine("- None");
            return;
        }

        foreach (var value in values)
            output.WriteLine($"- {value}");
    }

    private static string Status(SkillValidationSeverity severity) => severity switch
    {
        SkillValidationSeverity.Error => "FAIL",
        SkillValidationSeverity.Warning => "WARN",
        _ => "PASS"
    };

    private static string ResolveRoot(string currentDirectory, string? output) =>
        ResolvePath(currentDirectory, output ?? Path.Join(".openclaw", "skills"));

    private static string ResolvePackagesRoot(string currentDirectory, string? output) =>
        ResolvePath(currentDirectory, output ?? Path.Join(".openclaw", "packages"));

    private static string ResolvePath(string currentDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Join(currentDirectory, path));

    private static string? FirstPositional(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-", StringComparison.Ordinal))
            {
                if (RequiresValue(args[i]))
                    i++;
                continue;
            }

            return args[i];
        }

        return null;
    }

    private static string? GetOptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static IReadOnlyList<string> GetOptionValues(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                values.Add(args[i + 1]);
        }

        return values;
    }

    private static bool RequiresValue(string arg) => arg is "--category" or "--output" or "--template" or "--package-output" or "--input";

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width - 3)] + "...";

    private static int Unknown(string command, TextWriter error)
    {
        error.WriteLine($"Unknown skill command: {command}");
        error.WriteLine("Run: openclaw skill --help");
        return 2;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine(
            """
            openclaw skill

            Usage:
              openclaw skill new <name> [--category <category>] [--template <template>] [--output <path>] [--force]
              openclaw skill list [--output <path>]
              openclaw skill validate <skill-id|path> [--output <path>]
              openclaw skill critique <skill-id|path> [--output <path>]
              openclaw skill generate <skill-id|path> [--output <path>] [--force]
              openclaw skill package <skill-id|path> [--output <path>] [--package-output <path>] [--force]
              openclaw skill run <skill-id|path> --input <path> [--dry-run] [--output <path>]

            Defaults:
              --output .openclaw/skills
              --package-output .openclaw/packages
            """);
    }
}
