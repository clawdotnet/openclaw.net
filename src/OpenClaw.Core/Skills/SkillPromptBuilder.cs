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
            sb.AppendLine("<skill>");
            sb.Append("  <name>");
            sb.Append(XmlEscape(skill.Name));
            sb.AppendLine("</name>");
            sb.Append("  <description>");
            sb.Append(XmlEscape(skill.Description));
            sb.AppendLine("</description>");
            sb.Append("  <location>");
            sb.Append(XmlEscape(skill.Location));
            sb.AppendLine("</location>");
            sb.AppendLine("</skill>");
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

            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            sb.AppendLine($"  - {skill.Name}: {skill.Description}{flagStr} ({skill.Source})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Estimate the character cost of adding skills to the system prompt.
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

    private static string XmlEscape(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
