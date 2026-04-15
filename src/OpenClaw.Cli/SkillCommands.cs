using System.Diagnostics;
using OpenClaw.Core.Skills;

namespace OpenClaw.Cli;

internal static class SkillCommands
{
    private const string EnvWorkspace = "OPENCLAW_WORKSPACE";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "inspect" => await InspectAsync(rest),
            "install" => await InstallAsync(rest),
            "list" or "ls" => ListInstalled(rest),
            _ => UnknownSubcommand(subcommand)
        };
    }

    private static Task<int> InspectAsync(string[] args)
    {
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Console.Error.WriteLine("Usage: openclaw skills inspect <path|tarball>");
            return Task.FromResult(2);
        }

        return InspectSourceAsync(sourcePath, printInstallTarget: false);
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Console.Error.WriteLine("Usage: openclaw skills install <path|tarball>");
            return 2;
        }

        var dryRun = args.Contains("--dry-run");
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");

        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: true);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            Console.Error.WriteLine(inspected.ErrorMessage);
            return 1;
        }

        try
        {
            PrintInspection(inspected, ResolveSkillsDirectory(managed, workdir));
            if (dryRun)
                return 0;

            var skillsDirectory = ResolveSkillsDirectory(managed, workdir);
            Directory.CreateDirectory(skillsDirectory);

            var targetDir = Path.Combine(skillsDirectory, inspected.InstallSlug);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            CopyDirectory(inspected.SkillRootPath, targetDir);
            Console.WriteLine($"Installed skill '{inspected.Definition.Name}' to {targetDir}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(resolved.TempDirectory))
            {
                try { Directory.Delete(resolved.TempDirectory, recursive: true); } catch { }
            }
        }
    }

    private static int ListInstalled(string[] args)
    {
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");
        var skillsDirectory = ResolveSkillsDirectory(managed, workdir);

        if (!Directory.Exists(skillsDirectory))
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        var source = managed ? SkillSource.Managed : SkillSource.Workspace;
        var inspections = SkillInspector.InspectInstalledRoot(skillsDirectory, source)
            .Where(static inspection => inspection.Success && inspection.Definition is not null)
            .Select(CreateInspection)
            .OrderBy(static item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (inspections.Length == 0)
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        Console.WriteLine($"Installed skills ({inspections.Length}):");
        foreach (var inspection in inspections)
        {
            Console.WriteLine($"  {inspection.Definition.Name} - {inspection.Definition.Description}");
            Console.WriteLine($"    Trust: {inspection.TrustLevel}");
            Console.WriteLine($"    Source: {inspection.SourceLabel}");
            Console.WriteLine($"    Path: {inspection.SkillRootPath}");
        }

        return 0;
    }

    private static async Task<int> InspectSourceAsync(string sourcePath, bool printInstallTarget)
    {
        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: false);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            Console.Error.WriteLine(inspected.ErrorMessage);
            return 1;
        }

        PrintInspection(inspected, printInstallTarget ? ResolveSkillsDirectory(managed: false, workdir: null) : null);
        return 0;
    }

    private static async Task<(SkillCommandInspection Inspection, string? TempDirectory)> InspectResolvedSourceAsync(string sourcePath, bool retainExtractedDirectory)
    {
        var resolvedSourcePath = Path.GetFullPath(sourcePath);

        if (Directory.Exists(resolvedSourcePath))
        {
            var inspection = SkillInspector.InspectPath(resolvedSourcePath, SkillSource.Extra);
            return inspection.Success
                ? (CreateInspection(inspection), null)
                : (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect {resolvedSourcePath}."), null);
        }

        if (File.Exists(resolvedSourcePath) && resolvedSourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-install-{Guid.NewGuid():N}"[..24]);
            Directory.CreateDirectory(tempDir);

            try
            {
                var extractResult = await RunProcessAsync(
                    "tar",
                    ["--extract", "--gzip", "--file", resolvedSourcePath, "--directory", tempDir],
                    tempDir);
                if (extractResult.ExitCode != 0)
                    return (SkillCommandInspection.Failure($"Failed to extract skill tarball: {extractResult.Stderr}"), retainExtractedDirectory ? tempDir : null);

                var inspection = SkillInspector.InspectPath(tempDir, SkillSource.Extra);
                if (!inspection.Success)
                    return (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect extracted tarball {resolvedSourcePath}."), retainExtractedDirectory ? tempDir : null);

                return (CreateInspection(inspection), retainExtractedDirectory ? tempDir : null);
            }
            finally
            {
                if (!retainExtractedDirectory)
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        return (SkillCommandInspection.Failure($"Skill path not found: {sourcePath}"), null);
    }

    private static SkillCommandInspection CreateInspection(SkillInspectionResult inspection)
    {
        var definition = inspection.Definition!;
        var installSlug = Slugify(definition.Metadata.SkillKey ?? definition.Name);
        var trustLevel = definition.Source == SkillSource.Bundled ? "first-party" : "upstream-compatible";
        var trustReason = definition.Source == SkillSource.Bundled
            ? "Skill ships with OpenClaw.NET."
            : "Skill document parsed successfully and uses the OpenClaw skill format.";

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Description))
            warnings.Add("Skill description is empty.");
        if (definition.Metadata.RequireBins.Length == 0 &&
            definition.Metadata.RequireAnyBins.Length == 0 &&
            definition.Metadata.RequireEnv.Length == 0 &&
            definition.Metadata.RequireConfig.Length == 0)
        {
            warnings.Add("Skill does not declare host requirements.");
        }

        return new SkillCommandInspection
        {
            Success = true,
            Definition = definition,
            SkillRootPath = inspection.SkillRootPath!,
            SkillFilePath = inspection.SkillFilePath!,
            InstallSlug = installSlug,
            SourceLabel = definition.Source.ToString().ToLowerInvariant(),
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            Warnings = warnings
        };
    }

    private static void PrintInspection(SkillCommandInspection inspection, string? installDirectory)
    {
        Console.WriteLine($"Skill: {inspection.Definition.Name}");
        Console.WriteLine($"Description: {inspection.Definition.Description}");
        Console.WriteLine($"Trust: {inspection.TrustLevel}");
        Console.WriteLine($"Trust reason: {inspection.TrustReason}");
        Console.WriteLine($"Source: {inspection.SourceLabel}");
        Console.WriteLine($"Path: {inspection.SkillRootPath}");
        Console.WriteLine($"Install slug: {inspection.InstallSlug}");
        Console.WriteLine($"User invocable: {inspection.Definition.UserInvocable}");
        Console.WriteLine($"Disable model invocation: {inspection.Definition.DisableModelInvocation}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandDispatch))
            Console.WriteLine($"Command dispatch: {inspection.Definition.CommandDispatch}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandTool))
            Console.WriteLine($"Command tool: {inspection.Definition.CommandTool}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandArgMode))
            Console.WriteLine($"Command arg mode: {inspection.Definition.CommandArgMode}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.Homepage))
            Console.WriteLine($"Homepage: {inspection.Definition.Metadata.Homepage}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.PrimaryEnv))
            Console.WriteLine($"Primary env: {inspection.Definition.Metadata.PrimaryEnv}");
        Console.WriteLine($"Requirements: {BuildRequirementsSummary(inspection.Definition)}");
        foreach (var warning in inspection.Warnings)
            Console.WriteLine($"Warning: {warning}");
        if (!string.IsNullOrWhiteSpace(installDirectory))
            Console.WriteLine($"Install target: {Path.Combine(installDirectory, inspection.InstallSlug)}");
    }

    private static string BuildRequirementsSummary(SkillDefinition definition)
    {
        var items = new List<string>();
        if (definition.Metadata.RequireBins.Length > 0)
            items.Add($"bins={string.Join(",", definition.Metadata.RequireBins)}");
        if (definition.Metadata.RequireAnyBins.Length > 0)
            items.Add($"anyBins={string.Join(",", definition.Metadata.RequireAnyBins)}");
        if (definition.Metadata.RequireEnv.Length > 0)
            items.Add($"env={string.Join(",", definition.Metadata.RequireEnv)}");
        if (definition.Metadata.RequireConfig.Length > 0)
            items.Add($"config={string.Join(",", definition.Metadata.RequireConfig)}");
        if (definition.Metadata.Always)
            items.Add("always");

        return items.Count == 0 ? "none" : string.Join(" | ", items);
    }

    private static string ResolveSkillsDirectory(bool managed, string? workdir)
    {
        if (!string.IsNullOrWhiteSpace(workdir))
            return Path.Combine(Path.GetFullPath(workdir), "skills");

        if (managed)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "skills");
        }

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(Path.GetFullPath(workspace), "skills");

        throw new InvalidOperationException($"Missing {EnvWorkspace}. Set {EnvWorkspace} or pass --workdir or --managed.");
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

    private static void CopyDirectory(string source, string destination)
    {
        ThrowIfReparsePoint(new DirectoryInfo(source));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            ThrowIfReparsePoint(new FileInfo(file));
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            ThrowIfReparsePoint(new DirectoryInfo(dir));
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void ThrowIfReparsePoint(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to install skill package containing a symlink or reparse point: {info.FullName}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return (127, "", $"Command not found: {fileName}");
        }
    }

    private static string Slugify(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown skills subcommand: {subcommand}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            openclaw skills — Inspect and install local OpenClaw skill packages

            Usage:
              openclaw skills inspect <path|tarball>
              openclaw skills install <path|tarball> [--dry-run] [--workdir <path> | --managed]
              openclaw skills list [--workdir <path> | --managed]

            Notes:
              - Remote registry installs still go through `openclaw clawhub`.
              - `install --dry-run` prints trust and requirement details without copying files.
            """);
    }

    internal sealed class SkillCommandInspection
    {
        public required bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public required SkillDefinition Definition { get; init; }
        public required string SkillRootPath { get; init; }
        public required string SkillFilePath { get; init; }
        public required string InstallSlug { get; init; }
        public required string SourceLabel { get; init; }
        public required string TrustLevel { get; init; }
        public required string TrustReason { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public static SkillCommandInspection Failure(string errorMessage)
            => new()
            {
                Success = false,
                ErrorMessage = errorMessage,
                Definition = new SkillDefinition
                {
                    Name = string.Empty,
                    Description = string.Empty,
                    Instructions = string.Empty,
                    Location = string.Empty,
                    Source = SkillSource.Extra
                },
                SkillRootPath = string.Empty,
                SkillFilePath = string.Empty,
                InstallSlug = string.Empty,
                SourceLabel = string.Empty,
                TrustLevel = "untrusted",
                TrustReason = string.Empty
            };
    }
}
