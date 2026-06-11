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

    /// <summary>
    /// Optional override for the system-prompt template that surfaces the skill index.
    /// Supports the placeholders:
    /// <list type="bullet">
    ///   <item><description><c>{skills}</c> (required) — replaced with the &lt;skill&gt; XML entries.</description></item>
    ///   <item><description><c>{load_instruction}</c> — replaced with the <c>load_skill</c> usage hint.</description></item>
    ///   <item><description><c>{resource_instruction}</c> — replaced with the <c>read_skill_resource</c> usage hint when at least one skill exposes resources, otherwise an empty string.</description></item>
    /// </list>
    /// When null or whitespace, <see cref="SkillPromptBuilder.BuildIndex"/> falls back to its built-in default template.
    /// </summary>
    public string? InstructionPrompt { get; set; }

    /// <summary>
    /// Maximum bytes returned by a single <c>read_skill_resource</c> tool call. Resources larger
    /// than this are rejected with an error pointing the model at workspace file tools instead.
    /// Values &lt;= 0 fall back to the built-in default (256 KB).
    /// </summary>
    public int MaxResourceReadBytes { get; set; } = 256 * 1024;
}

/// <summary>
/// Controls where skills are loaded from.
/// </summary>
public sealed class SkillLoadConfig
{
    /// <summary>Additional skill directories (lowest precedence).</summary>
    public string[] ExtraDirs { get; set; } = [];

    /// <summary>Load bundled skills shipped with the gateway.</summary>
    public bool IncludeBundled { get; set; } = true;

    /// <summary>Load managed/local skills from ~/.openclaw/skills.</summary>
    public bool IncludeManaged { get; set; } = true;

    /// <summary>Override the managed/local skills directory. Defaults to ~/.openclaw/skills.</summary>
    public string? ManagedRoot { get; set; }

    /// <summary>Load workspace skills from $OPENCLAW_WORKSPACE/skills.</summary>
    public bool IncludeWorkspace { get; set; } = true;

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

    /// <summary>Skill execution kind. Defaults to standard skills.</summary>
    public SkillKind Kind { get; init; } = SkillKind.Standard;

    /// <summary>Optional natural-language triggers for intent matching.</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>Optional priority for meta-skill resolution.</summary>
    public int? MetaPriority { get; init; }

    /// <summary>Final text mode for meta-skill result shaping.</summary>
    public string? FinalTextMode { get; init; }

    /// <summary>Meta-skill composition definition (steps DAG).</summary>
    public MetaSkillComposition? Composition { get; init; }

    /// <summary>Whether the skill is user-invocable as a slash command.</summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Whether the skill is excluded from the model prompt.</summary>
    public bool DisableModelInvocation { get; init; } = false;

    /// <summary>Optional tool dispatch settings.</summary>
    public string? CommandDispatch { get; init; }
    public string? CommandTool { get; init; }
    public string? CommandArgMode { get; init; }

    /// <summary>
    /// Auxiliary files discovered under <c>references/</c> and <c>scripts/</c> in the skill directory.
    /// Surfaced in the prompt index so the model can fetch them on demand
    /// (Anthropic-style progressive disclosure, level 3).
    /// </summary>
    public IReadOnlyList<SkillResource> Resources { get; init; } = [];
}

/// <summary>
/// Execution kind declared by skill frontmatter.
/// </summary>
public enum SkillKind : byte
{
    Standard,
    Meta
}

/// <summary>
/// Parsed composition section for meta skills.
/// </summary>
public sealed class MetaSkillComposition
{
    /// <summary>Ordered step definitions.</summary>
    public IReadOnlyList<MetaSkillStepDefinition> Steps { get; init; } = [];
}

/// <summary>
/// A single composition step.
/// </summary>
public sealed class MetaSkillStepDefinition
{
    /// <summary>Unique step identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Step kind (agent, tool_call, llm_chat, etc.).</summary>
    public required string Kind { get; init; }

    /// <summary>Optional delegated skill name.</summary>
    public string? Skill { get; init; }

    /// <summary>Optional delegated tool name.</summary>
    public string? Tool { get; init; }

    /// <summary>Optional raw JSON payload from the step's 'with' field.</summary>
    public string? WithJson { get; init; }

    /// <summary>Optional upstream step dependencies.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
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
/// Kind of auxiliary skill resource (controls how the model is expected to use it).
/// </summary>
public enum SkillResourceKind : byte
{
    /// <summary>Read-only reference material under <c>references/</c>.</summary>
    Reference,
    /// <summary>Executable helper under <c>scripts/</c>.</summary>
    Script
}

/// <summary>
/// Auxiliary file packaged alongside a skill (under <c>references/</c> or <c>scripts/</c>).
/// Listed in the prompt index but loaded on demand to keep token cost low.
/// </summary>
public sealed class SkillResource
{
    /// <summary>File name (e.g. <c>lookup.md</c>).</summary>
    public required string Name { get; init; }

    /// <summary>POSIX-style path relative to the skill root (e.g. <c>references/lookup.md</c>).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path on disk.</summary>
    public required string AbsolutePath { get; init; }

    /// <summary>Whether the resource is reference material or an executable script.</summary>
    public required SkillResourceKind Kind { get; init; }
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

    /// <summary>Optional declared risk level (for example: low, medium, high).</summary>
    public string? Risk { get; set; }

    /// <summary>Optional capability allowlist used by governance and runtime guards.</summary>
    public string[] Capabilities { get; set; } = [];
}
