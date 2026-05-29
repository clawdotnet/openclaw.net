using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillRunPlanner
{
    public SkillRunPlan Plan(SkillPackage package, IReadOnlyList<string> inputPaths)
    {
        var issues = new List<SkillValidationIssue>();
        foreach (var inputPath in inputPaths)
        {
            issues.Add(File.Exists(inputPath)
                ? new SkillValidationIssue { Severity = SkillValidationSeverity.Pass, Area = "Inputs", Message = $"{inputPath} exists.", FileName = inputPath }
                : new SkillValidationIssue { Severity = SkillValidationSeverity.Error, Area = "Inputs", Message = $"{inputPath} is missing.", FileName = inputPath });
        }

        return new SkillRunPlan
        {
            Manifest = package.Manifest,
            Inputs = inputPaths.ToArray(),
            InputIssues = issues
        };
    }
}
