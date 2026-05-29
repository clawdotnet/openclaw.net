namespace OpenClaw.SkillKit.Abstractions;

public sealed class SkillManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "0.1.0";
    public string Category { get; init; } = "general";
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public SkillIntent Intent { get; init; } = new();
    public SkillInputs Inputs { get; init; } = new();
    public SkillOutputs Outputs { get; init; } = new();
    public SkillToolPolicy Tools { get; init; } = new();
    public SkillGuardrails Guardrails { get; init; } = new();
    public SkillHumanApprovalPolicy HumanApproval { get; init; } = new();
    public SkillValidationPolicy Validation { get; init; } = new();
    public SkillWorkflow Workflow { get; init; } = new();
}

public sealed class SkillIntent
{
    public string Outcome { get; init; } = string.Empty;
}

public sealed class SkillInputs
{
    public IReadOnlyList<string> Required { get; init; } = [];
    public IReadOnlyList<string> Optional { get; init; } = [];
}

public sealed class SkillOutputs
{
    public IReadOnlyList<string> Required { get; init; } = [];
    public IReadOnlyList<string> Optional { get; init; } = [];
}

public sealed class SkillToolPolicy
{
    public IReadOnlyList<string> Allowed { get; init; } = [];
    public IReadOnlyList<string> Forbidden { get; init; } = [];
    public IReadOnlyList<string> ApprovalRequired { get; init; } = [];
}

public sealed class SkillGuardrails
{
    public IReadOnlyList<string> MustNot { get; init; } = [];
}

public sealed class SkillHumanApprovalPolicy
{
    public IReadOnlyList<string> RequiredFor { get; init; } = [];
}

public sealed class SkillValidationPolicy
{
    public IReadOnlyList<string> Checks { get; init; } = [];
}

public sealed class SkillWorkflow
{
    public IReadOnlyList<SkillWorkflowStep> Steps { get; init; } = [];
}

public sealed class SkillWorkflowStep
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public SkillWorkflowStepType Type { get; init; } = SkillWorkflowStepType.Reasoning;
    public string Description { get; init; } = string.Empty;
}

public sealed class SkillPackage
{
    public string RootPath { get; init; } = string.Empty;
    public SkillManifest Manifest { get; init; } = new();
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillPackageOptions
{
    public string SkillsRoot { get; init; } = string.Empty;
    public string PackagesRoot { get; init; } = string.Empty;
    public string Template { get; init; } = "generic";
    public bool Force { get; init; }
}

public sealed class SkillValidationResult
{
    public string SkillId { get; init; } = string.Empty;
    public bool Passed => Issues.All(static issue => issue.Severity != SkillValidationSeverity.Error);
    public IReadOnlyList<SkillValidationIssue> Issues { get; init; } = [];
}

public sealed class SkillValidationIssue
{
    public SkillValidationSeverity Severity { get; init; }
    public string Area { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? FileName { get; init; }
}

public sealed class SkillRunPlan
{
    public SkillManifest Manifest { get; init; } = new();
    public IReadOnlyList<string> Inputs { get; init; } = [];
    public IReadOnlyList<SkillValidationIssue> InputIssues { get; init; } = [];
}

public sealed class SkillCritiqueResult
{
    public string Markdown { get; init; } = string.Empty;
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public interface ISkillCritiqueProvider
{
    Task<SkillCritiqueResult> CritiqueAsync(SkillPackage package, CancellationToken cancellationToken);
}

public enum SkillValidationSeverity
{
    Pass,
    Warning,
    Error
}

public enum SkillWorkflowStepType
{
    Input,
    Reasoning,
    Generation,
    Validation,
    Approval,
    Output
}
