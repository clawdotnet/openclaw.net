using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillValidator
{
    public async Task<SkillValidationResult> ValidateAsync(string skillRef, string skillsRoot, CancellationToken cancellationToken = default)
    {
        var root = SkillPackageReader.ResolveSkillPath(skillRef, skillsRoot);
        var issues = new List<SkillValidationIssue>();
        var manifestPath = SkillPackageReader.ResolvePackageFilePath(root, "skill.yaml");

        if (!File.Exists(manifestPath))
        {
            issues.Add(Error("Files", "skill.yaml is missing.", "skill.yaml"));
            return new SkillValidationResult { SkillId = Path.GetFileName(root), Issues = issues };
        }

        SkillManifest manifest;
        try
        {
            manifest = await SkillManifestSerializer.ReadAsync(manifestPath, cancellationToken);
            issues.Add(Pass("Files", "skill.yaml exists.", "skill.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            issues.Add(Error("Files", $"skill.yaml could not be read: {ex.Message}", "skill.yaml"));
            return new SkillValidationResult { SkillId = Path.GetFileName(root), Issues = issues };
        }

        foreach (var file in SkillTemplateRenderer.RequiredFiles.Where(static file => file != "skill.yaml"))
        {
            issues.Add(File.Exists(SkillPackageReader.ResolvePackageFilePath(root, file))
                ? Pass("Files", $"{file} exists.", file)
                : Error("Files", $"{file} is missing.", file));
        }

        var folderName = Path.GetFileName(root);
        if (!string.Equals(folderName, manifest.Id, StringComparison.OrdinalIgnoreCase) &&
            !manifest.Aliases.Contains(folderName, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(Error("Manifest", $"Manifest id '{manifest.Id}' does not match folder '{folderName}' or a declared alias.", "skill.yaml"));
        }
        else
        {
            issues.Add(Pass("Manifest", "Manifest id matches folder or alias.", "skill.yaml"));
        }

        RequireText(issues, manifest.Name, "Manifest", "Name is present.", "Name is missing.", "skill.yaml");
        RequireText(issues, manifest.Version, "Manifest", "Version is present.", "Version is missing.", "skill.yaml");
        RequireText(issues, manifest.Category, "Manifest", "Category is present.", "Category is missing.", "skill.yaml");
        RequireText(issues, manifest.Intent.Outcome, "Intent", "Intent outcome is present.", "Intent outcome is missing.", "skill.yaml");
        RequireList(issues, manifest.Inputs.Required, "Policy", "Required inputs defined.", "At least one required input is required.", "skill.yaml", SkillValidationSeverity.Error);
        RequireList(issues, manifest.Outputs.Required, "Policy", "Required outputs defined.", "At least one required output is required.", "skill.yaml", SkillValidationSeverity.Error);
        RequireList(issues, manifest.Validation.Checks, "Policy", "Validation checks defined.", "Validation checks are empty.", "skill.yaml", SkillValidationSeverity.Warning);
        RequireList(issues, manifest.Guardrails.MustNot, "Policy", "Guardrails defined.", "Guardrails are empty.", "skill.yaml", SkillValidationSeverity.Error);
        RequireList(issues, manifest.HumanApproval.RequiredFor, "Policy", "Human approval policy defined.", "Human approval policy is empty.", "skill.yaml", SkillValidationSeverity.Warning);

        if (manifest.Workflow.Steps.Count == 0)
            issues.Add(Error("Workflow", "Workflow must contain at least one step.", "workflow.yaml"));
        else
            issues.Add(Pass("Workflow", "Workflow has at least one step.", "workflow.yaml"));

        var allowed = manifest.Tools.Allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbidden = manifest.Tools.Forbidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approvalRequired = manifest.Tools.ApprovalRequired.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedForbiddenOverlap = allowed.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray();
        var approvalForbiddenOverlap = approvalRequired.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray();

        if (allowedForbiddenOverlap.Length > 0)
            issues.Add(Error("Policy", $"Allowed and forbidden tools overlap: {string.Join(", ", allowedForbiddenOverlap)}.", "tools.yaml"));
        else if (approvalForbiddenOverlap.Length > 0)
            issues.Add(Error("Policy", $"Approval-required tools cannot be forbidden: {string.Join(", ", approvalForbiddenOverlap)}.", "tools.yaml"));
        else
            issues.Add(Pass("Policy", "Tool policy has no conflicts.", "tools.yaml"));

        return new SkillValidationResult { SkillId = manifest.Id, Issues = issues };
    }

    private static void RequireText(List<SkillValidationIssue> issues, string value, string area, string pass, string error, string fileName)
    {
        issues.Add(string.IsNullOrWhiteSpace(value)
            ? Error(area, error, fileName)
            : Pass(area, pass, fileName));
    }

    private static void RequireList(List<SkillValidationIssue> issues, IReadOnlyList<string> values, string area, string pass, string failure, string fileName, SkillValidationSeverity emptySeverity)
    {
        if (values.Count > 0)
        {
            issues.Add(Pass(area, pass, fileName));
            return;
        }

        issues.Add(emptySeverity == SkillValidationSeverity.Warning
            ? Warning(area, failure, fileName)
            : Error(area, failure, fileName));
    }

    private static SkillValidationIssue Pass(string area, string message, string? fileName = null) => new()
    {
        Severity = SkillValidationSeverity.Pass,
        Area = area,
        Message = message,
        FileName = fileName
    };

    private static SkillValidationIssue Warning(string area, string message, string? fileName = null) => new()
    {
        Severity = SkillValidationSeverity.Warning,
        Area = area,
        Message = message,
        FileName = fileName
    };

    private static SkillValidationIssue Error(string area, string message, string? fileName = null) => new()
    {
        Severity = SkillValidationSeverity.Error,
        Area = area,
        Message = message,
        FileName = fileName
    };
}
