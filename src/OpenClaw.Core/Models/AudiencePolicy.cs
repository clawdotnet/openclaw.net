namespace OpenClaw.Core.Models;

/// <summary>Operator-controlled restrictions, intersected with every runtime tool preset.</summary>
public sealed class AudienceConfig
{
    public bool Enabled { get; set; }
    public string DefaultAudience { get; set; } = "public";
    public Dictionary<string, string> ChannelBindings { get; set; } = new(StringComparer.Ordinal);
    // Exact session IDs allow distinct rooms on the same transport. No sender heuristics.
    public Dictionary<string, string> SessionBindings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, AudienceProfile> Profiles { get; set; } = new(StringComparer.Ordinal)
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
        var name = config.SessionBindings.GetValueOrDefault(session.Id)
            ?? config.ChannelBindings.GetValueOrDefault(session.ChannelId)
            ?? config.DefaultAudience;
        // Unknown/misspelled profiles deny everything instead of widening access.
        return config.Profiles.GetValueOrDefault(name) ?? new AudienceProfile();
    }

    public static bool AllowsTool(AudienceProfile? profile, string name)
        => profile is null || profile.AllowAllTools || profile.AllowedTools.Contains(name, StringComparer.Ordinal);
}
