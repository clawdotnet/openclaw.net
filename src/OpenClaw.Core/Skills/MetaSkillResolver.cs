namespace OpenClaw.Core.Skills;

/// <summary>
/// Resolves a meta skill candidate from user text based on trigger match and meta priority.
/// </summary>
public static class MetaSkillResolver
{
    /// <summary>
    /// Try to match a meta skill for the given user message.
    /// </summary>
    public static bool TryResolve(
        IReadOnlyList<SkillDefinition> skills,
        string? userMessage,
        out SkillDefinition? matched)
    {
        matched = null;

        if (skills.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return false;

        var message = userMessage.Trim();

        SkillDefinition? bestSkill = null;
        int bestPriority = int.MinValue;
        int bestTriggerLength = -1;

        foreach (var skill in skills)
        {
            if (skill.Kind != SkillKind.Meta || skill.DisableModelInvocation)
                continue;

            if (skill.Triggers.Count == 0)
                continue;

            foreach (var trigger in skill.Triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger))
                    continue;

                if (!IsTriggerMatch(message, trigger))
                    continue;

                var priority = skill.MetaPriority ?? 0;
                var triggerLength = trigger.Length;

                if (priority > bestPriority || (priority == bestPriority && triggerLength > bestTriggerLength))
                {
                    bestSkill = skill;
                    bestPriority = priority;
                    bestTriggerLength = triggerLength;
                }
            }
        }

        matched = bestSkill;
        return matched is not null;
    }

    private static bool IsTriggerMatch(string userMessage, string trigger)
    {
        var normalizedTrigger = trigger.Trim();
        if (normalizedTrigger.Length == 0)
            return false;

        return userMessage.Contains(normalizedTrigger, StringComparison.OrdinalIgnoreCase);
    }
}
