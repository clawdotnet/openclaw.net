using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ToolPresetResolver : IToolPresetResolver
{
    private static readonly string[] DefaultWebDeny =
    [
        "shell",
        "process",
        "write_file",
        "code_exec",
        "git",
        "automation"
    ];

    private static readonly string[] DefaultReadonlyDeny =
    [
        "shell",
        "process",
        "write_file",
        "code_exec",
        "git",
        "automation",
        "memory_note",
        "delegate_agent",
        "home_assistant_write",
        "mqtt_publish",
        "notion_write"
    ];

    private static readonly string[] CodingPresetAllow =
    [
        "shell", "read_file", "write_file", "edit_file", "apply_patch", "process", "git",
        "code_exec", "browser", "memory", "memory_search", "memory_get", "project_memory",
        "sessions", "session_search", "session_status", "delegate_agent",
        "web_search", "web_fetch", "pdf_read", "image_gen", "vision_analyze"
    ];

    private static readonly string[] MessagingPresetAllow =
    [
        "message", "sessions", "sessions_send", "sessions_history", "sessions_spawn",
        "session_status", "session_search", "memory", "memory_search", "memory_get",
        "profile_read", "todo"
    ];

    private static readonly Dictionary<string, ToolsetConfig> BuiltInToolsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["group:runtime"] = new() { AllowTools = ["shell", "process", "code_exec"] },
        ["group:fs"] = new() { AllowTools = ["read_file", "write_file", "edit_file", "apply_patch"] },
        ["group:sessions"] = new() { AllowTools = ["sessions", "sessions_history", "sessions_send", "sessions_spawn", "session_status", "session_search", "agents_list"] },
        ["group:memory"] = new() { AllowTools = ["memory", "memory_search", "memory_get", "project_memory"] },
        ["group:web"] = new() { AllowTools = ["web_search", "web_fetch", "x_search", "browser"] },
        ["group:automation"] = new() { AllowTools = ["cron", "automation", "gateway", "todo"] },
        ["group:messaging"] = new() { AllowTools = ["message"] },
    };

    private readonly GatewayConfig _config;
    private readonly SessionMetadataStore _metadataStore;

    public ToolPresetResolver(GatewayConfig config, SessionMetadataStore metadataStore)
    {
        _config = config;
        _metadataStore = metadataStore;
    }

    public ResolvedToolPreset Resolve(Session session, IEnumerable<string> availableToolNames)
    {
        var toolNames = availableToolNames
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var surface = InferSurface(session);
        var metadata = _metadataStore.Get(session.Id);
        var requestedPresetId = !string.IsNullOrWhiteSpace(session.RoutePresetId)
            ? session.RoutePresetId
            : metadata.ActivePresetId;
        var presetId = string.IsNullOrWhiteSpace(requestedPresetId)
            ? ResolvePresetIdForSurface(surface)
            : requestedPresetId!.Trim();

        if (_config.Tooling.Presets.TryGetValue(presetId, out var configuredPreset))
            return ResolveConfiguredPreset(presetId, surface, configuredPreset, toolNames);

        return ResolveBuiltInPreset(presetId, surface, toolNames);
    }

    public IReadOnlyList<ResolvedToolPreset> ListPresets(IEnumerable<string> availableToolNames)
    {
        var names = availableToolNames.ToArray();
        var configured = _config.Tooling.Presets.Keys
            .Select(presetId => ResolveConfiguredPreset(presetId, surface: "", _config.Tooling.Presets[presetId], names));
        var builtInIds = new[] { "cli", "full", "coding", "messaging", "minimal", "web", "telegram", "automation", "readonly" }
            .Where(id => !_config.Tooling.Presets.ContainsKey(id))
            .Select(id => ResolveBuiltInPreset(id, surface: "", names));
        return configured.Concat(builtInIds)
            .OrderBy(static item => item.PresetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string InferSurface(Session session)
    {
        var aliasedSurface = session.ChannelId switch
        {
            "openai-http" or "openai-responses" => "openai-http",
            _ => session.ChannelId
        };

        if (_config.Tooling.SurfaceBindings.Count > 0 &&
            _config.Tooling.SurfaceBindings.TryGetValue(session.ChannelId, out var mappedPreset) &&
            !string.IsNullOrWhiteSpace(mappedPreset))
        {
            return session.ChannelId;
        }

        if (!string.Equals(aliasedSurface, session.ChannelId, StringComparison.OrdinalIgnoreCase) &&
            _config.Tooling.SurfaceBindings.TryGetValue(aliasedSurface, out mappedPreset) &&
            !string.IsNullOrWhiteSpace(mappedPreset))
        {
            return aliasedSurface;
        }

        if (string.Equals(aliasedSurface, "openai-http", StringComparison.OrdinalIgnoreCase))
            return "openai-http";
        if (string.Equals(session.ChannelId, "websocket", StringComparison.OrdinalIgnoreCase))
            return "web";
        if (session.ChannelId.Contains("telegram", StringComparison.OrdinalIgnoreCase))
            return "telegram";
        if (string.Equals(session.ChannelId, "cron", StringComparison.OrdinalIgnoreCase)
            || session.Id.StartsWith("automation:", StringComparison.OrdinalIgnoreCase))
            return "automation";

        return aliasedSurface;
    }

    private string ResolvePresetIdForSurface(string surface)
    {
        if (_config.Tooling.SurfaceBindings.TryGetValue(surface, out var presetId) &&
            !string.IsNullOrWhiteSpace(presetId))
            return presetId.Trim();

        return surface switch
        {
            "cli" => "cli",
            "openai-http" => "web",
            "web" => "web",
            "telegram" => "telegram",
            "automation" => "automation",
            _ => "cli"
        };
    }

    private ResolvedToolPreset ResolveConfiguredPreset(
        string presetId,
        string surface,
        ToolPresetConfig preset,
        IReadOnlyList<string> availableToolNames)
    {
        var allowed = new HashSet<string>(availableToolNames, StringComparer.OrdinalIgnoreCase);

        foreach (var toolsetId in preset.Toolsets)
        {
            if (!_config.Tooling.Toolsets.TryGetValue(toolsetId, out var toolset))
            {
                // Fall back to built-in toolsets (e.g. "group:runtime")
                if (!BuiltInToolsets.TryGetValue(toolsetId, out toolset))
                    continue;
            }

            ApplyToolset(allowed, availableToolNames, toolset);
        }

        ApplyDirectRules(allowed, availableToolNames, preset.AllowTools, preset.AllowPrefixes, preset.DenyTools, preset.DenyPrefixes);

        return new ResolvedToolPreset
        {
            PresetId = presetId,
            Surface = surface,
            Description = preset.Description,
            EffectiveAutonomyMode = preset.AutonomyMode ?? _config.Tooling.AutonomyMode,
            RequireToolApproval = preset.RequireToolApproval ?? _config.Tooling.RequireToolApproval,
            AllowedTools = allowed,
            ApprovalRequiredTools = new HashSet<string>(
                preset.ApprovalRequiredTools.Where(static item => !string.IsNullOrWhiteSpace(item)),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void ApplyToolset(HashSet<string> allowed, IReadOnlyList<string> allToolNames, ToolsetConfig toolset)
        => ApplyDirectRules(allowed, allToolNames, toolset.AllowTools, toolset.AllowPrefixes, toolset.DenyTools, toolset.DenyPrefixes);

    private static void ApplyDirectRules(
        HashSet<string> allowed,
        IReadOnlyList<string> allToolNames,
        IEnumerable<string> allowTools,
        IEnumerable<string> allowPrefixes,
        IEnumerable<string> denyTools,
        IEnumerable<string> denyPrefixes)
    {
        foreach (var tool in allowTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
            allowed.Add(tool.Trim());

        foreach (var prefix in allowPrefixes.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var toolName in allToolNames.Where(toolName => toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                allowed.Add(toolName);
        }

        foreach (var tool in denyTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
            allowed.Remove(tool.Trim());

        foreach (var prefix in denyPrefixes.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var toolName in allToolNames.Where(toolName => toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                allowed.Remove(toolName);
        }
    }

    private ResolvedToolPreset ResolveBuiltInPreset(string presetId, string surface, IReadOnlyList<string> availableToolNames)
    {
        var allowed = new HashSet<string>(availableToolNames, StringComparer.OrdinalIgnoreCase);
        var approvalRequired = new HashSet<string>(_config.Tooling.ApprovalRequiredTools, StringComparer.OrdinalIgnoreCase);

        switch (presetId.ToLowerInvariant())
        {
            case "full":
                // All tools allowed — no denies
                break;
            case "coding":
                allowed.IntersectWith(CodingPresetAllow);
                break;
            case "messaging":
                allowed.IntersectWith(MessagingPresetAllow);
                break;
            case "minimal":
                allowed.IntersectWith(["session_status"]);
                break;
            case "web":
                foreach (var tool in DefaultWebDeny)
                    allowed.Remove(tool);
                approvalRequired.UnionWith(["process", "automation"]);
                break;
            case "telegram":
                foreach (var tool in DefaultWebDeny.Concat(["browser", "delegate_agent"]))
                    allowed.Remove(tool);
                approvalRequired.UnionWith(["process", "automation"]);
                break;
            case "automation":
                foreach (var tool in availableToolNames.Where(static tool => tool is "shell" or "write_file" or "code_exec" or "git" or "browser"))
                    allowed.Remove(tool);
                allowed.Add("automation");
                allowed.Add("session_search");
                allowed.Add("profile_read");
                allowed.Add("todo");
                approvalRequired.UnionWith(["automation"]);
                break;
            case "readonly":
                foreach (var tool in DefaultReadonlyDeny)
                    allowed.Remove(tool);
                break;
            default:
                approvalRequired.UnionWith(["process", "automation"]);
                break;
        }

        return new ResolvedToolPreset
        {
            PresetId = presetId,
            Surface = surface,
            Description = $"Built-in preset '{presetId}'.",
            EffectiveAutonomyMode = presetId.Equals("readonly", StringComparison.OrdinalIgnoreCase) ? "readonly" : _config.Tooling.AutonomyMode,
            RequireToolApproval = _config.Tooling.RequireToolApproval || presetId is "web" or "telegram",
            AllowedTools = allowed,
            ApprovalRequiredTools = approvalRequired
        };
    }
}
