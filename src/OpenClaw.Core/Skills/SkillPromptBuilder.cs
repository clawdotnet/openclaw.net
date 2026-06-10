using System.Text;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Formats eligible skills into system prompt fragments.
/// Compatible with OpenClaw's XML skill list format (<c>formatSkillsForPrompt</c>).
/// </summary>
public static class SkillPromptBuilder
{
    /// <summary>
    /// Build the full skills section for the system prompt.
    /// Returns empty string if no skills are eligible for model invocation.
    /// </summary>
    /// <remarks>
    /// Eagerly emits both the index (metadata) and every skill's full instructions body.
    /// For progressive disclosure, prefer <see cref="BuildIndex"/> in the system prompt and
    /// inject <see cref="BuildSkillBody"/> on demand via the <c>load_skill</c> tool.
    /// </remarks>
    public static string Build(IReadOnlyList<SkillDefinition> skills)
    {
        // Filter to skills that aren't excluded from the model prompt
        var modelSkills = skills
            .Where(s => !s.DisableModelInvocation)
            .ToList();

        if (modelSkills.Count == 0)
            return "";

        var sb = new StringBuilder();

        // Compact XML list (matches OpenClaw's formatSkillsForPrompt output)
        sb.AppendLine();
        sb.AppendLine("<available-skills>");
        sb.AppendLine("The following skills are available to help you complete tasks. Use them when relevant.");
        sb.AppendLine();

        foreach (var skill in modelSkills)
        {
            AppendSkillEntry(sb, skill);
        }

        sb.AppendLine("</available-skills>");

        // Append full instructions for each skill
        sb.AppendLine();
        sb.AppendLine("<skill-instructions>");

        foreach (var skill in modelSkills)
        {
            if (string.IsNullOrWhiteSpace(skill.Instructions))
                continue;

            sb.AppendLine();
            sb.Append("## Skill: ");
            sb.AppendLine(skill.Name);
            sb.AppendLine(skill.Instructions);
        }

        sb.AppendLine("</skill-instructions>");

        return sb.ToString();
    }

    /// <summary>
    /// Built-in template for <see cref="BuildIndex"/>. Contains the <c>{skills}</c>,
    /// <c>{load_instruction}</c> and <c>{resource_instruction}</c> placeholders.
    /// </summary>
    /// <remarks>
    /// Mirrors the spirit of MAF's <c>AgentSkillsProvider.DefaultSkillsInstructionPrompt</c>
    /// while staying compatible with OpenClaw's existing XML envelope.
    /// </remarks>
    public const string DefaultIndexTemplate =
        """
        <available-skills>
        The following skills are available. Only metadata and a resource manifest are shown here.
        {load_instruction}{resource_instruction}Only load what is needed, when it is needed.

        {skills}
        </available-skills>
        """;

    private const string LoadInstructionFragment =
        "Call the `load_skill` tool with a skill name to fetch its full instructions on demand.\n";

    private const string ResourceInstructionFragment =
        "Call the `read_skill_resource` tool with a skill name and resource path to fetch a single reference or script body. "
        + "Only paths listed inside that skill's <resources> manifest are valid; if a skill has no <resources> node, do not call this tool for it. "
        + "Note: `SKILL.md` is the skill body itself — use `load_skill` to fetch it, never `read_skill_resource`.\n";

    /// <summary>The required placeholder in any custom <see cref="BuildIndex"/> template.</summary>
    public const string SkillsPlaceholder = "{skills}";

    /// <summary>
    /// Build only the skill index — L1 metadata plus the L3 resource manifest of
    /// progressive disclosure. The L2 SKILL.md body is *not* included; the model is expected to
    /// invoke the <c>load_skill</c> tool to fetch a specific skill's instructions on demand,
    /// and the <c>read_skill_resource</c> tool to pull a single L3 resource body.
    /// </summary>
    /// <param name="skills">The set of skills to advertise.</param>
    /// <param name="template">
    /// Optional template overriding <see cref="DefaultIndexTemplate"/>. Must contain the
    /// <c>{skills}</c> placeholder; <c>{load_instruction}</c> and <c>{resource_instruction}</c>
    /// are optional. When null or whitespace, the default template is used.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="template"/> is non-empty but does not contain
    /// the required <c>{skills}</c> placeholder.
    /// </exception>
    public static string BuildIndex(IReadOnlyList<SkillDefinition> skills, string? template = null)
    {
        var modelSkills = skills
            .Where(s => !s.DisableModelInvocation)
            .ToList();

        if (modelSkills.Count == 0)
            return "";

        var hasResources = modelSkills.Any(s => s.Resources.Count > 0);

        var entries = new StringBuilder();
        foreach (var skill in modelSkills)
        {
            AppendSkillEntry(entries, skill);
        }
        // Trim trailing newline produced by AppendSkillEntry so the placeholder
        // substitution does not introduce a stray blank line before the closing tag.
        var skillsBlock = entries.ToString().TrimEnd('\r', '\n');

        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? DefaultIndexTemplate : template;

        if (!effectiveTemplate.Contains(SkillsPlaceholder, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Skills instruction prompt template must contain the {SkillsPlaceholder} placeholder.",
                nameof(template));

        var rendered = effectiveTemplate
            .Replace("{load_instruction}", LoadInstructionFragment, StringComparison.Ordinal)
            .Replace("{resource_instruction}", hasResources ? ResourceInstructionFragment : "", StringComparison.Ordinal)
            .Replace(SkillsPlaceholder, skillsBlock, StringComparison.Ordinal);

        // Preserve the original behaviour of leading/trailing newlines around the section
        // so callers can append it directly to the base prompt with a single separator.
        return "\n" + rendered + "\n";
    }

    /// <summary>
    /// Build the <c>&lt;skill-instructions&gt;</c> fragment for a single skill — the level-2 body
    /// injected on demand. Returns an empty string if the skill is excluded from model invocation
    /// or has no instructions body.
    /// </summary>
    public static string BuildSkillBody(SkillDefinition skill)
    {
        if (skill.DisableModelInvocation || string.IsNullOrWhiteSpace(skill.Instructions))
            return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<skill-instructions>");
        sb.AppendLine();
        sb.Append("## Skill: ");
        sb.AppendLine(skill.Name);
        sb.AppendLine(skill.Instructions);
        sb.AppendLine("</skill-instructions>");
        return sb.ToString();
    }

    private static void AppendSkillEntry(StringBuilder sb, SkillDefinition skill)
    {
        sb.AppendLine("<skill>");
        sb.Append("  <name>");
        sb.Append(XmlEscape(skill.Name));
        sb.AppendLine("</name>");
        sb.Append("  <kind>");
        sb.Append(XmlEscape(skill.Kind.ToString().ToLowerInvariant()));
        sb.AppendLine("</kind>");
        sb.Append("  <description>");
        sb.Append(XmlEscape(skill.Description));
        sb.AppendLine("</description>");
        sb.Append("  <location>");
        sb.Append(XmlEscape(skill.Location));
        sb.AppendLine("</location>");

        if (skill.MetaPriority is not null)
        {
            sb.Append("  <meta-priority>");
            sb.Append(skill.MetaPriority.Value);
            sb.AppendLine("</meta-priority>");
        }

        if (skill.Triggers.Count > 0)
        {
            sb.AppendLine("  <triggers>");
            foreach (var trigger in skill.Triggers)
            {
                sb.Append("    <trigger>");
                sb.Append(XmlEscape(trigger));
                sb.AppendLine("</trigger>");
            }
            sb.AppendLine("  </triggers>");
        }

        if (skill.Resources.Count > 0)
        {
            sb.AppendLine("  <resources>");
            foreach (var resource in skill.Resources)
            {
                sb.Append("    <resource kind=\"");
                sb.Append(resource.Kind == SkillResourceKind.Reference ? "reference" : "script");
                sb.Append("\" path=\"");
                sb.Append(XmlEscape(resource.RelativePath));
                sb.AppendLine("\" />");
            }
            sb.AppendLine("  </resources>");
        }

        sb.AppendLine("</skill>");
    }

    /// <summary>
    /// Build a concise summary of available skills for token-cost estimation or debugging.
    /// </summary>
    public static string BuildSummary(IReadOnlyList<SkillDefinition> skills)
    {
        if (skills.Count == 0)
            return "No skills loaded.";

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded skills ({skills.Count}):");

        foreach (var skill in skills)
        {
            var flags = new List<string>(4);
            if (skill.DisableModelInvocation) flags.Add("no-model");
            if (!skill.UserInvocable) flags.Add("no-slash");
            if (skill.Metadata.Always) flags.Add("always");
            if (skill.CommandDispatch is not null) flags.Add($"dispatch:{skill.CommandDispatch}");
            if (skill.Kind == SkillKind.Meta) flags.Add("kind:meta");
            if (skill.MetaPriority is not null) flags.Add($"meta-priority:{skill.MetaPriority.Value}");

            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            sb.AppendLine($"  - {skill.Name}: {skill.Description}{flagStr} ({skill.Source})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Estimate the character cost of adding skills to the system prompt
    /// using the eager <see cref="Build"/> output (index + every SKILL.md body).
    /// </summary>
    public static int EstimateCharacterCost(IReadOnlyList<SkillDefinition> skills)
    {
        var modelSkills = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (modelSkills.Count == 0)
            return 0;

        // Base overhead (XML wrapper + header text)
        var cost = 195;

        foreach (var skill in modelSkills)
        {
            // Per-skill overhead (XML tags + indentation) + actual content
            cost += 97
                + XmlEscape(skill.Name).Length
                + XmlEscape(skill.Description).Length
                + XmlEscape(skill.Location).Length
                + skill.Instructions.Length;
        }

        return cost;
    }

    /// <summary>
    /// Estimate the character cost of the progressive-disclosure index produced by
    /// <see cref="BuildIndex"/> — i.e. L1 metadata + the L3 resource manifest, without
    /// any SKILL.md body. Useful for comparing the savings versus <see cref="EstimateCharacterCost"/>.
    /// </summary>
    public static int EstimateIndexCharacterCost(IReadOnlyList<SkillDefinition> skills, string? template = null)
    {
        var modelSkills = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (modelSkills.Count == 0)
            return 0;

        var hasResources = modelSkills.Any(s => s.Resources.Count > 0);
        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? DefaultIndexTemplate : template;

        // Template overhead, with placeholders resolved the same way BuildIndex does.
        var templateOverhead = effectiveTemplate.Length
            - SkillsPlaceholder.Length
            - "{load_instruction}".Length
            - (effectiveTemplate.Contains("{resource_instruction}", StringComparison.Ordinal) ? "{resource_instruction}".Length : 0)
            + LoadInstructionFragment.Length
            + (hasResources ? ResourceInstructionFragment.Length : 0)
            + 2; // BuildIndex prepends and appends "\n"

        var cost = templateOverhead;

        foreach (var skill in modelSkills)
        {
            // Per-skill XML overhead (<skill>, <name>, <description>, <location>, closing tags + newlines)
            cost += 97
                + XmlEscape(skill.Name).Length
                + XmlEscape(skill.Description).Length
                + XmlEscape(skill.Location).Length;

            if (skill.Resources.Count > 0)
            {
                // <resources> + </resources> wrapper (incl. indentation + newlines)
                cost += 30;
                foreach (var resource in skill.Resources)
                {
                    // <resource kind="..." path="..." />
                    cost += 33
                        + (resource.Kind == SkillResourceKind.Reference ? "reference".Length : "script".Length)
                        + XmlEscape(resource.RelativePath).Length;
                }
            }
        }

        return cost;
    }

    /// <summary>
    /// Estimate the per-skill character contribution to the eager <see cref="Build"/> output —
    /// i.e. the index entry plus the full SKILL.md body. Returns 0 for skills with
    /// <see cref="SkillDefinition.DisableModelInvocation"/> set.
    /// </summary>
    public static int EstimateSkillEagerCost(SkillDefinition skill)
    {
        if (skill.DisableModelInvocation)
            return 0;

        return 97
            + XmlEscape(skill.Name).Length
            + XmlEscape(skill.Description).Length
            + XmlEscape(skill.Location).Length
            + skill.Instructions.Length;
    }

    /// <summary>
    /// Estimate the per-skill character contribution to <see cref="BuildIndex"/> —
    /// i.e. the L1 entry plus the L3 resource manifest, without the SKILL.md body.
    /// Returns 0 for skills with <see cref="SkillDefinition.DisableModelInvocation"/> set.
    /// </summary>
    public static int EstimateSkillIndexCost(SkillDefinition skill)
    {
        if (skill.DisableModelInvocation)
            return 0;

        var cost = 97
            + XmlEscape(skill.Name).Length
            + XmlEscape(skill.Description).Length
            + XmlEscape(skill.Location).Length;

        if (skill.Resources.Count > 0)
        {
            cost += 30; // <resources>...</resources> wrapper
            foreach (var resource in skill.Resources)
            {
                cost += 33
                    + (resource.Kind == SkillResourceKind.Reference ? "reference".Length : "script".Length)
                    + XmlEscape(resource.RelativePath).Length;
            }
        }

        return cost;
    }

    private static string XmlEscape(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
