using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class DeterministicSkillCritiqueProvider : ISkillCritiqueProvider
{
    public Task<SkillCritiqueResult> CritiqueAsync(SkillPackage package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var findings = new List<string>();
        var manifest = package.Manifest;

        AddIf(findings, IsVague(manifest.Intent.Outcome), "Outcome is vague; add concrete success criteria and expected downstream use.");
        AddIf(findings, manifest.HumanApproval.RequiredFor.Count == 0, "No human approval points are defined.");
        AddIf(findings, manifest.Tools.Forbidden.Count == 0, "No forbidden tools are defined.");
        AddIf(findings, manifest.Validation.Checks.Count == 0, "No validation checks are defined.");
        AddIf(findings, !manifest.Workflow.Steps.Any(static step => step.Type == SkillWorkflowStepType.Validation), "Workflow has no validation step.");
        AddIf(findings, !package.Files.TryGetValue("intent.md", out var intent) || !ContainsSectionText(intent, "Failure Scenarios"), "No failure scenarios are documented.");
        AddIf(findings, !package.Files.TryGetValue("guardrails.md", out var guardrails) || !guardrails.Contains("Missing Information", StringComparison.OrdinalIgnoreCase), "Missing-information behavior is not documented.");
        AddIf(findings, !package.Files.TryGetValue("guardrails.md", out guardrails) || !guardrails.Contains("Grounding", StringComparison.OrdinalIgnoreCase), "Grounding or attribution rules are not documented.");
        AddIf(findings, !package.Files.TryGetValue("examples.md", out var examples) || !examples.Contains("Expected Output", StringComparison.OrdinalIgnoreCase), "Examples do not include an expected output outline.");

        if (findings.Count == 0)
            findings.Add("No deterministic critique findings.");

        var builder = new StringBuilder();
        builder.AppendLine("# Skill Critique");
        builder.AppendLine();
        builder.AppendLine($"Skill: {manifest.Name}");
        builder.AppendLine($"GeneratedAtUtc: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in findings)
            builder.AppendLine($"- {finding}");
        builder.AppendLine();
        builder.AppendLine("## Notes");
        builder.AppendLine();
        builder.AppendLine("- This critique is deterministic and does not call an LLM.");
        builder.AppendLine("- Future critique providers can implement `ISkillCritiqueProvider`.");

        return Task.FromResult(new SkillCritiqueResult
        {
            Markdown = builder.ToString(),
            Findings = findings
        });
    }

    private static bool IsVague(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim();
        return normalized.Length < 40 ||
            normalized.Contains("help", StringComparison.OrdinalIgnoreCase) && normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 10;
    }

    private static bool ContainsSectionText(string text, string section)
    {
        var index = text.IndexOf(section, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        var after = text[(index + section.Length)..];
        return after.Split('\n').Any(static line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
    }

    private static void AddIf(List<string> findings, bool condition, string finding)
    {
        if (condition)
            findings.Add(finding);
    }
}
