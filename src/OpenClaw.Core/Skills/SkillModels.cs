using System.Text.Json.Serialization;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Top-level skills configuration. Maps to <c>Skills</c> section in config.
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Master toggle for the skills system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skill loading configuration.</summary>
    public SkillLoadConfig Load { get; set; } = new();

    /// <summary>Per-skill entry overrides (keyed by skill name or skillKey).</summary>
    public Dictionary<string, SkillEntryConfig> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional allowlist for bundled skills only. If set, only listed bundled skills are eligible.</summary>
    public string[] AllowBundled { get; set; } = [];
}

/// <summary>
/// Controls where skills are loaded from.
/// </summary>
public sealed class SkillLoadConfig
{
    /// <summary>Additional skill directories (lowest precedence).</summary>
    public string[] ExtraDirs { get; set; } = [];

    /// <summary>Enable file-system watching for hot reload.</summary>
    public bool Watch { get; set; } = false;

    /// <summary>Debounce interval for the watcher (ms).</summary>
    public int WatchDebounceMs { get; set; } = 250;
}

/// <summary>
/// Per-skill config override.
/// </summary>
public sealed class SkillEntryConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>API key shorthand — injected as the env var named by <c>primaryEnv</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Environment variables injected for this skill's agent run.</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom per-skill config bag.</summary>
    public Dictionary<string, string> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A parsed skill definition, loaded from a <c>SKILL.md</c> file.
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>Skill name from frontmatter.</summary>
    public required string Name { get; init; }

    /// <summary>Short description from frontmatter.</summary>
    public required string Description { get; init; }

    /// <summary>The full instructions body (markdown below the frontmatter).</summary>
    public required string Instructions { get; init; }

    /// <summary>Filesystem path of the skill directory.</summary>
    public required string Location { get; init; }

    /// <summary>Where the skill came from.</summary>
    public SkillSource Source { get; init; }

    /// <summary>Parsed metadata from the frontmatter.</summary>
    public SkillMetadata Metadata { get; init; } = new();

    /// <summary>Whether the skill is user-invocable as a slash command.</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Whether the skill is excluded from the model prompt.</summary>
    public bool DisableModelInvocation { get; init; } = false;

    /// <summary>Optional tool dispatch settings.</summary>
    public string? CommandDispatch { get; init; }
    public string? CommandTool { get; init; }
    public string? CommandArgMode { get; init; }
}

/// <summary>
/// Where a skill was loaded from.
/// </summary>
public enum SkillSource : byte
{
    Bundled,
    Managed,
    Workspace,
    Extra,
    Plugin
}

/// <summary>
/// Metadata block parsed from the <c>metadata</c> frontmatter line.
/// </summary>
public sealed class SkillMetadata
{
    /// <summary>If true, skill is always eligible regardless of other gates.</summary>
    public bool Always { get; set; }

    /// <summary>Optional emoji for UI display.</summary>
    public string? Emoji { get; set; }

    /// <summary>Optional homepage URL.</summary>
    public string? Homepage { get; set; }

    /// <summary>OS filter (darwin, linux, win32). Empty = any OS.</summary>
    public string[] Os { get; set; } = [];

    /// <summary>Required binary names on PATH.</summary>
    public string[] RequireBins { get; set; } = [];

    /// <summary>At least one of these binaries must exist on PATH.</summary>
    public string[] RequireAnyBins { get; set; } = [];

    /// <summary>Required environment variables.</summary>
    public string[] RequireEnv { get; set; } = [];

    /// <summary>Required config paths that must be truthy.</summary>
    public string[] RequireConfig { get; set; } = [];

    /// <summary>Primary env var associated with <c>skills.entries.*.apiKey</c>.</summary>
    public string? PrimaryEnv { get; set; }

    /// <summary>Alternative config key used by <c>skills.entries.*</c>.</summary>
    public string? SkillKey { get; set; }
}
