namespace OpenClaw.Core.Models;

/// <summary>Operator-controlled restrictions, intersected with every runtime tool preset.</summary>
public sealed class AudienceConfig
{
    public bool Enabled { get; set; }
    public string DefaultAudience { get; set; } = "public";
    public Dictionary<string, string> ChannelBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Exact session IDs allow distinct rooms on the same transport. No sender heuristics.
    public Dictionary<string, string> SessionBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AudienceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public"] = new(),
        ["team"] = new() { AllowedTools = ["web_search", "web_fetch"] },
        ["personal"] = new() { AllowAllTools = true, IncludePrivateContext = true, AllowAttachments = true }
    };
}

public sealed class AudienceProfile
{
    public bool AllowAllTools { get; set; }
    public string[] AllowedTools { get; set; } = [];
    public bool IncludePrivateContext { get; set; }
    public bool AllowAttachments { get; set; }
}

public static class AudiencePolicy
{
    public static AudienceProfile? Resolve(AudienceConfig config, Session session)
    {
        if (!config.Enabled) return null;
        var name = Value(config.SessionBindings, session.Id)
            ?? Value(config.ChannelBindings, session.ChannelId)
            ?? config.DefaultAudience;
        // Unknown/misspelled profiles deny everything instead of widening access.
        return Value(config.Profiles, name) ?? new AudienceProfile();
    }

    private static TValue? Value<TValue>(Dictionary<string, TValue> values, string key) where TValue : class
        => values.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public static bool AllowsTool(AudienceProfile? profile, string name)
        => profile is null || profile.AllowAllTools || profile.AllowedTools.Contains(name, StringComparer.OrdinalIgnoreCase);
}
