namespace OpenClaw.Core.Skills;

/// <summary>
/// Shared inspection helpers for CLI and admin tooling around local skill packages.
/// </summary>
public static class SkillInspector
{
    public static SkillInspectionResult InspectPath(string candidatePath, SkillSource source)
    {
        if (!TryLocateSkillRoot(candidatePath, out var skillRootPath, out var error))
            return SkillInspectionResult.Failure(error ?? "No SKILL.md file was found.");

        var skillFilePath = Path.Combine(skillRootPath!, "SKILL.md");
        var definition = SkillLoader.ParseSkillFile(skillFilePath, skillRootPath!, source);
        if (definition is null)
            return SkillInspectionResult.Failure($"Failed to parse skill frontmatter at {skillFilePath}.");

        return new SkillInspectionResult
        {
            Success = true,
            SkillRootPath = skillRootPath!,
            SkillFilePath = skillFilePath,
            Definition = definition
        };
    }

    public static IReadOnlyList<SkillInspectionResult> InspectInstalledRoot(string skillsRootPath, SkillSource source)
    {
        var results = new List<SkillInspectionResult>();
        if (!Directory.Exists(skillsRootPath))
            return results;

        var rootSkillFile = Path.Combine(skillsRootPath, "SKILL.md");
        if (File.Exists(rootSkillFile))
            results.Add(InspectPath(skillsRootPath, source));

        foreach (var dir in Directory.GetDirectories(skillsRootPath))
        {
            var skillFilePath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFilePath))
                continue;

            results.Add(InspectPath(dir, source));
        }

        return results;
    }

    public static bool TryLocateSkillRoot(string candidatePath, out string? skillRootPath, out string? error)
    {
        skillRootPath = null;
        error = null;

        if (!Directory.Exists(candidatePath))
        {
            error = $"Skill path not found: {candidatePath}";
            return false;
        }

        if (File.Exists(Path.Combine(candidatePath, "SKILL.md")))
        {
            skillRootPath = Path.GetFullPath(candidatePath);
            return true;
        }

        var matches = Directory
            .EnumerateFiles(
                candidatePath,
                "SKILL.md",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        if (matches.Length == 0)
        {
            error = $"No SKILL.md file was found under {candidatePath}.";
            return false;
        }

        if (matches.Length > 1)
        {
            error = $"Multiple SKILL.md files were found under {candidatePath}. Point the command at a single skill directory.";
            return false;
        }

        skillRootPath = Path.GetFullPath(matches[0]);
        return true;
    }
}

public sealed class SkillInspectionResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SkillRootPath { get; init; }
    public string? SkillFilePath { get; init; }
    public SkillDefinition? Definition { get; init; }

    public static SkillInspectionResult Failure(string errorMessage)
        => new() { Success = false, ErrorMessage = errorMessage };
}
