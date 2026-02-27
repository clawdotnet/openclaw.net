using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Integrations;

internal static class HomeAssistantRuleEngine
{
    internal sealed record EventInfo(
        string EventType,
        string EntityId,
        string FromState,
        string ToState,
        string FriendlyName);

    public static HomeAssistantEventRule? SelectRule(HomeAssistantEventsConfig cfg, EventInfo info, DateTime localNow)
    {
        foreach (var rule in cfg.Rules)
        {
            if (!IsRuleMatch(rule, info))
                continue;
            if (!IsRuleInLocalWindow(rule, localNow))
                continue;
            if (!IsRuleAllowedDay(rule, localNow))
                continue;

            return rule;
        }

        return null;
    }

    public static string Render(HomeAssistantEventsConfig cfg, HomeAssistantEventRule? rule, EventInfo info)
    {
        var template = rule is not null && !string.IsNullOrWhiteSpace(rule.PromptTemplate)
            ? rule.PromptTemplate
            : cfg.PromptTemplate;

        return template
            .Replace("{event_type}", info.EventType, StringComparison.Ordinal)
            .Replace("{entity_id}", info.EntityId, StringComparison.Ordinal)
            .Replace("{from_state}", info.FromState, StringComparison.Ordinal)
            .Replace("{to_state}", info.ToState, StringComparison.Ordinal)
            .Replace("{friendly_name}", info.FriendlyName, StringComparison.Ordinal);
    }

    private static bool IsRuleMatch(HomeAssistantEventRule rule, EventInfo info)
    {
        if (rule.EntityIdGlobs.Length > 0 && !rule.EntityIdGlobs.Any(g => GlobMatcher.IsMatch(g, info.EntityId)))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.FromState) && !string.Equals(rule.FromState, info.FromState, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.ToState) && !string.Equals(rule.ToState, info.ToState, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsRuleInLocalWindow(HomeAssistantEventRule rule, DateTime localNow)
    {
        if (string.IsNullOrWhiteSpace(rule.BetweenLocalStart) || string.IsNullOrWhiteSpace(rule.BetweenLocalEnd))
            return true;

        if (!TimeOnly.TryParseExact(rule.BetweenLocalStart, "HH:mm", out var start))
            return true;
        if (!TimeOnly.TryParseExact(rule.BetweenLocalEnd, "HH:mm", out var end))
            return true;

        var now = TimeOnly.FromDateTime(localNow);

        if (start <= end)
            return now >= start && now <= end;

        // Overnight window (e.g., 22:00–06:00)
        return now >= start || now <= end;
    }

    private static bool IsRuleAllowedDay(HomeAssistantEventRule rule, DateTime localNow)
    {
        if (rule.DaysOfWeek.Length == 0)
            return true;

        var abbrev = localNow.DayOfWeek switch
        {
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tue",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thu",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            DayOfWeek.Sunday => "Sun",
            _ => ""
        };

        return rule.DaysOfWeek.Any(d => string.Equals(d, abbrev, StringComparison.OrdinalIgnoreCase));
    }
}

