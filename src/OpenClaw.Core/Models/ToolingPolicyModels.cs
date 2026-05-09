namespace OpenClaw.Core.Models;

public sealed class ToolsetConfig
{
    public string[] AllowTools { get; set; } = [];
    public string[] AllowPrefixes { get; set; } = [];
    public string[] DenyTools { get; set; } = [];
    public string[] DenyPrefixes { get; set; } = [];
}

public sealed class ToolPresetConfig
{
    public string[] Toolsets { get; set; } = [];
    public string[] AllowTools { get; set; } = [];
    public string[] AllowPrefixes { get; set; } = [];
    public string[] DenyTools { get; set; } = [];
    public string[] DenyPrefixes { get; set; } = [];
    public string[] ApprovalRequiredTools { get; set; } = [];
    public string? AutonomyMode { get; set; }
    public bool? RequireToolApproval { get; set; }
    public string Description { get; set; } = "";
}

public sealed class ResolvedToolPreset
{
    public required string PresetId { get; init; }
    public string Description { get; init; } = "";
    public string Surface { get; init; } = "";
    public string EffectiveAutonomyMode { get; init; } = "";
    public bool RequireToolApproval { get; init; }
    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ApprovalRequiredTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ToolActionDescriptor
{
    public string Action { get; init; } = "";
    public bool IsMutation { get; init; }
    public bool RequiresApproval { get; init; }
    public string Summary { get; init; } = "";
    public string? ApprovalFingerprint { get; init; }
    public string? RiskLevel { get; init; }
    public bool? ReadOnly { get; init; }
}
