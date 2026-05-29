using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public sealed class SkillTemplateRenderer
{
    public static readonly string[] RequiredFiles =
    [
        "skill.yaml",
        "intent.md",
        "expectations.md",
        "workflow.yaml",
        "tools.yaml",
        "guardrails.md",
        "validation.md",
        "examples.md",
        "trace.md"
    ];

    public SkillManifest CreateManifest(string name, string category, string template)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();
        var normalizedTemplate = string.IsNullOrWhiteSpace(template) ? normalizedCategory : template.Trim().ToLowerInvariant();
        var id = SkillIdGenerator.Generate(name);
        var profile = TemplateProfile.For(normalizedTemplate, normalizedCategory);

        return new SkillManifest
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled Skill" : name.Trim(),
            Version = "0.1.0",
            Category = normalizedCategory,
            Description = profile.Description,
            Intent = new SkillIntent { Outcome = profile.Outcome },
            Inputs = new SkillInputs
            {
                Required = profile.RequiredInputs,
                Optional = profile.OptionalInputs
            },
            Outputs = new SkillOutputs
            {
                Required = profile.RequiredOutputs,
                Optional = profile.OptionalOutputs
            },
            Tools = new SkillToolPolicy
            {
                Allowed = profile.AllowedTools,
                Forbidden = profile.ForbiddenTools,
                ApprovalRequired = profile.ApprovalRequiredTools
            },
            Guardrails = new SkillGuardrails { MustNot = profile.MustNot },
            HumanApproval = new SkillHumanApprovalPolicy { RequiredFor = profile.HumanApprovalRequiredFor },
            Validation = new SkillValidationPolicy { Checks = profile.ValidationChecks },
            Workflow = CreateDefaultWorkflow()
        };
    }

    public IReadOnlyDictionary<string, string> RenderFiles(SkillManifest manifest)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["skill.yaml"] = SkillManifestSerializer.Serialize(manifest),
            ["intent.md"] = RenderIntent(manifest),
            ["expectations.md"] = RenderExpectations(manifest),
            ["workflow.yaml"] = SkillManifestSerializer.SerializeWorkflow(manifest.Workflow),
            ["tools.yaml"] = SkillManifestSerializer.SerializeTools(manifest.Tools),
            ["guardrails.md"] = RenderGuardrails(manifest),
            ["validation.md"] = RenderValidation(manifest),
            ["examples.md"] = RenderExamples(manifest),
            ["trace.md"] = RenderTrace(manifest, "created")
        };
    }

    public string RenderTrace(SkillManifest manifest, string status)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Trace");
        builder.AppendLine();
        builder.AppendLine("## Skill");
        builder.AppendLine();
        builder.AppendLine($"- ID: {manifest.Id}");
        builder.AppendLine($"- Name: {manifest.Name}");
        builder.AppendLine($"- Version: {manifest.Version}");
        builder.AppendLine();
        builder.AppendLine("## Generated Files");
        builder.AppendLine();
        foreach (var file in RequiredFiles)
            builder.AppendLine($"- {file}");
        builder.AppendLine();
        builder.AppendLine("## Open Decisions");
        builder.AppendLine();
        builder.AppendLine("- Review whether the default tool policy is sufficient for the intended environment.");
        builder.AppendLine();
        builder.AppendLine("## Validation Status");
        builder.AppendLine();
        builder.AppendLine($"- {status}");
        builder.AppendLine();
        builder.AppendLine("## Run History");
        builder.AppendLine();
        builder.AppendLine("- No runs recorded.");
        return builder.ToString();
    }

    private static SkillWorkflow CreateDefaultWorkflow() => new()
    {
        Steps =
        [
            new SkillWorkflowStep { Id = "collect_inputs", Name = "Collect Required Inputs", Type = SkillWorkflowStepType.Input, Description = "Ensure all required inputs are present." },
            new SkillWorkflowStep { Id = "analyze_source", Name = "Analyze Source Material", Type = SkillWorkflowStepType.Reasoning, Description = "Extract grounded themes and evidence." },
            new SkillWorkflowStep { Id = "draft_output", Name = "Draft Output", Type = SkillWorkflowStepType.Generation, Description = "Produce the requested structured output." },
            new SkillWorkflowStep { Id = "validate_output", Name = "Validate Output", Type = SkillWorkflowStepType.Validation, Description = "Check output against validation rules." },
            new SkillWorkflowStep { Id = "request_human_review", Name = "Request Human Review", Type = SkillWorkflowStepType.Approval, Description = "Ask for human review before final use if required." }
        ]
    };

    private static string RenderIntent(SkillManifest manifest) =>
        $"""
        # Intent

        ## Outcome

        {manifest.Intent.Outcome}

        ## Users

        - People who need repeatable, reviewable agent assistance for {manifest.Category} work.

        ## Required Inputs

        {FormatList(manifest.Inputs.Required)}

        ## Expected Outputs

        {FormatList(manifest.Outputs.Required)}

        ## Constraints

        - Follow the tool policy in `tools.yaml`.
        - Follow the guardrails in `guardrails.md`.
        - Ask for human review when required by the manifest.

        ## Success Scenarios

        - The output is complete, grounded in supplied inputs, and ready for human review.

        ## Failure Scenarios

        - Required inputs are missing.
        - The output includes unsupported claims.
        - The workflow reaches a human approval point and no reviewer is available.
        """;

    private static string RenderExpectations(SkillManifest manifest) =>
        $"""
        # Expectations

        ## Done Means

        - All required outputs are present.
        - Validation checks pass or unresolved issues are explicitly listed.

        ## Must-Have Behaviors

        - Ground conclusions in the provided inputs.
        - Separate known facts from inferred or uncertain points.
        - Preserve missing information as a visible section instead of filling gaps.

        ## Must-Not-Happen Behaviors

        {FormatList(manifest.Guardrails.MustNot)}

        ## Quality Bar

        - Clear, structured, and usable by the intended reviewer.
        - No external action is taken without approval when approval is required.

        ## Human Review Required When

        {FormatList(manifest.HumanApproval.RequiredFor)}
        """;

    private static string RenderGuardrails(SkillManifest manifest) =>
        $"""
        # Guardrails

        ## Must Not

        {FormatList(manifest.Guardrails.MustNot)}

        ## Requires Human Approval

        {FormatList(manifest.HumanApproval.RequiredFor)}

        ## Missing Information Behavior

        - List missing inputs or uncertain areas separately.
        - Do not invent facts, quotes, evidence, or approvals.

        ## Attribution and Grounding Rules

        - Attribute claims only when the source material supports attribution.
        - Distinguish direct evidence from interpretation.
        """;

    private static string RenderValidation(SkillManifest manifest) =>
        $"""
        # Validation

        ## Required Checks

        {FormatList(manifest.Validation.Checks)}

        ## Output Completeness

        {FormatList(manifest.Outputs.Required.Select(static output => $"Output includes `{output}`.").ToArray())}

        ## Grounding Checks

        - Every important claim is traceable to supplied inputs or clearly marked as inference.

        ## Risk Checks

        - Risks, cautions, and missing information are visible.

        ## Approval Checks

        - Human approval points are respected before final use or external action.
        """;

    private static string RenderExamples(SkillManifest manifest) =>
        $"""
        # Examples

        ## Example Input

        ```text
        Provide {string.Join(", ", manifest.Inputs.Required)} for {manifest.Name}.
        ```

        ## Expected Output Outline

        {FormatList(manifest.Outputs.Required)}
        """;

    private static string FormatList(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "- None specified.";

        return string.Join(Environment.NewLine, values.Select(static value => $"- {value}"));
    }

    private sealed record TemplateProfile(
        string Description,
        string Outcome,
        IReadOnlyList<string> RequiredInputs,
        IReadOnlyList<string> OptionalInputs,
        IReadOnlyList<string> RequiredOutputs,
        IReadOnlyList<string> OptionalOutputs,
        IReadOnlyList<string> AllowedTools,
        IReadOnlyList<string> ForbiddenTools,
        IReadOnlyList<string> ApprovalRequiredTools,
        IReadOnlyList<string> MustNot,
        IReadOnlyList<string> HumanApprovalRequiredFor,
        IReadOnlyList<string> ValidationChecks)
    {
        public static TemplateProfile For(string template, string category) => template switch
        {
            "research" => Research,
            "proposal" => Proposal,
            "compliance" => Compliance,
            "operations" => Operations,
            "software" => Software,
            _ => Generic(category)
        };

        private static TemplateProfile Generic(string category) => new(
            $"Reusable {category} skill with explicit inputs, guardrails, tool policy, and validation checks.",
            "Produce a structured, grounded output that can be reviewed before use.",
            ["source_material"],
            ["project_context", "target_audience"],
            ["summary", "recommendations", "risks", "next_steps"],
            [],
            ["file.read", "file.search", "memory.retrieve"],
            ["email.send", "external_submit", "data_delete"],
            ["external_publication", "final_recommendations"],
            ["Invent facts or evidence.", "Take external action without approval.", "Hide uncertainty or missing information."],
            ["final_recommendations", "external_publication"],
            ["Required outputs are present.", "Important claims are grounded in supplied inputs.", "Missing information is listed separately."]);

        private static readonly TemplateProfile Research = new(
            "Extracts pain points, stakeholder needs, risks, and practical technology opportunities from community-engaged research discussions.",
            "Produce a structured insight brief that helps researchers understand key issues and identify practical support opportunities without replacing community judgment.",
            ["transcript_or_notes"],
            ["project_context", "target_audience", "prior_discussions"],
            ["executive_summary", "key_pain_points", "stakeholder_needs", "opportunity_map", "risks_and_cautions", "follow_up_questions"],
            [],
            ["file.read", "file.search", "web.search", "memory.retrieve"],
            ["email.send", "external_submit", "data_delete"],
            ["final_recommendations", "external_publication", "named_attribution"],
            ["Invent participant quotes.", "Attribute views to named people unless present in the source.", "Recommend replacing community engagement with automation.", "Treat AI-generated themes as final truth without human review."],
            ["final_recommendations", "external_publication", "named_attribution"],
            ["Every key claim is grounded in the transcript or provided context.", "Recommendations are framed as support tools, not replacements for relationships.", "Missing information is listed separately.", "Risks are included alongside opportunities."]);

        private static readonly TemplateProfile Proposal = new(
            "Drafts a concept note or proposal from grounded project inputs.",
            "Produce a concise proposal draft with clear goals, beneficiaries, activities, risks, and review points.",
            ["project_brief"],
            ["funder_guidance", "budget_notes", "organization_context"],
            ["concept_summary", "need_statement", "proposed_activities", "outcomes", "risks", "review_questions"],
            [],
            ["file.read", "file.search", "memory.retrieve"],
            ["external_submit", "email.send", "data_delete"],
            ["budget_commitments", "external_submission", "final_recommendations"],
            ["Invent funder requirements.", "Commit funds or organizational promises without approval.", "Submit externally without human review."],
            ["budget_commitments", "external_submission", "final_recommendations"],
            ["Proposal claims are grounded in supplied material.", "Open assumptions are listed.", "Submission requires human approval."]);

        private static readonly TemplateProfile Compliance = new(
            "Reviews compliance materials against provided requirements and produces an issue list.",
            "Produce a grounded compliance review that separates findings, evidence, risks, and open questions.",
            ["document_or_policy", "requirements"],
            ["jurisdiction", "prior_findings"],
            ["findings", "evidence_table", "risk_summary", "open_questions", "recommended_reviews"],
            [],
            ["file.read", "file.search"],
            ["external_submit", "data_delete", "policy_change"],
            ["legal_conclusions", "external_publication", "policy_change"],
            ["Present legal advice as final.", "Invent requirements.", "Change policy without approval."],
            ["legal_conclusions", "external_publication", "policy_change"],
            ["Each finding cites supplied evidence.", "Uncertain interpretations are marked for review.", "Risks are included."]);

        private static readonly TemplateProfile Operations = new(
            "Plans operational work with explicit checks, risks, and approval points.",
            "Produce an operational task plan that is clear, bounded, and safe to review before action.",
            ["task_request"],
            ["constraints", "systems_context", "deadline"],
            ["task_plan", "dependencies", "risks", "approval_points", "verification_steps"],
            [],
            ["file.read", "file.search", "memory.retrieve"],
            ["external_submit", "data_delete", "system_modify"],
            ["system_change", "external_communication", "final_execution"],
            ["Modify production systems without approval.", "Hide operational risk.", "Skip verification steps."],
            ["system_change", "external_communication", "final_execution"],
            ["Dependencies are listed.", "Risks are visible.", "Verification steps are defined."]);

        private static readonly TemplateProfile Software = new(
            "Creates a development task plan or implementation prompt from repository context.",
            "Produce an actionable software work plan with constraints, tests, and acceptance criteria.",
            ["task_request"],
            ["repo_context", "existing_findings", "target_files"],
            ["implementation_plan", "test_plan", "risks", "acceptance_criteria"],
            [],
            ["file.read", "file.search", "git.diff"],
            ["git.push", "external_submit", "data_delete"],
            ["file_write", "shell_execution", "merge_or_release"],
            ["Invent APIs without checking code.", "Change files or run mutating commands without approval.", "Skip validation for risky changes."],
            ["file_write", "shell_execution", "merge_or_release"],
            ["Plan references concrete files or seams.", "Tests are listed.", "Known risks and unknowns are explicit."]);
    }
}
