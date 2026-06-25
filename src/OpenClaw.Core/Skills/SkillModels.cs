using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Top-level skills configuration. Maps to <c>Skills</c> section in config.
/// </summary>
public sealed class SkillsConfig
{
    /// <summary>Master toggle for the skills system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dedicated runtime policy for OpenClaw meta-skill invocation.</summary>
    public MetaSkillPolicyConfig MetaSkill { get; set; } = new();

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

public sealed class MetaSkillPolicyConfig
{
    /// <summary>When false, keep meta skills loaded for inventory/history but reject explicit meta invocation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional allowlist of declared risk levels that may load as meta skills.</summary>
    public string[] AllowedRiskLevels { get; set; } = [];

    /// <summary>Optional capability set that every meta skill must declare to remain eligible.</summary>
    public string[] RequiredCapabilities { get; set; } = [];
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

    /// <summary>
    /// When true, ExtraDirs and ManagedRoot are scanned recursively (all nested
    /// subdirectories) for SKILL.md files. This allows a parent skill directory
    /// to bundle sub-skills in nested folders (e.g. subskills/docx/SKILL.md).
    /// Defaults to false for backward compatibility.
    /// </summary>
    public bool ScanSubdirectories { get; set; } = false;
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

    /// <summary>Final text mode for meta-skill result shaping. Supported values are auto, raw, structured, or step:&lt;id&gt;.</summary>
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
    /// <summary>Optional raw JSON payload from the composition-level 'tool_args' field.</summary>
    public string? ToolArgsJson { get; init; }

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

    /// <summary>Optional skill_exec entrypoint name for deterministic subprocess execution.</summary>
    public string? SkillExecEntrypoint { get; init; }

    /// <summary>Optional argument vector for skill_exec entrypoints.</summary>
    public IReadOnlyList<string> SkillExecArgs { get; init; } = [];

    /// <summary>Optional stdin template for skill_exec entrypoints.</summary>
    public string? SkillExecStdin { get; init; }

    /// <summary>Optional relative working directory for skill_exec entrypoints.</summary>
    public string? SkillExecCwd { get; init; }

    /// <summary>Optional parse mode for skill_exec stdout.</summary>
    public string? SkillExecParseMode { get; init; }

    /// <summary>Optional raw JSON payload from the step's 'with' field.</summary>
    public string? WithJson { get; init; }

    /// <summary>Optional expression controlling whether this step should execute.</summary>
    public string? When { get; init; }

    /// <summary>Optional raw JSON payload from the step's 'tool_args' field.</summary>
    public string? ToolArgsJson { get; init; }

    /// <summary>Optional allowlist restricting tool access for this step.</summary>
    public IReadOnlyList<string> ToolAllowlist { get; init; } = [];

    /// <summary>Optional allowed output values for this step.</summary>
    public IReadOnlyList<string> OutputChoices { get; init; } = [];

    /// <summary>Optional clarify schema for interactive input collection.</summary>
    public MetaClarifySchema? Clarify { get; init; }

    /// <summary>Optional route definitions for branching to subsequent steps. Each target must resolve to another declared step.</summary>
    public IReadOnlyList<MetaRouteDefinition> Routes { get; init; } = [];

    /// <summary>Optional upstream step dependencies.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Optional substitute step to execute when this step fails.</summary>
    public string? OnFailure { get; init; }

    /// <summary>Optional step-local timeout in seconds.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Step-local retry policy. Defaults to one attempt and no backoff.</summary>
    public MetaStepRetryPolicy Retry { get; init; } = new();

    /// <summary>Optional contract for validating this step's intermediate output.</summary>
    public MetaStepOutputContract OutputContract { get; init; } = new();

    // ─── fan_out fields ───

    /// <summary>
    /// Jinja expression that must evaluate to a JSON array. Each element becomes
    /// the input for one child iteration. Only valid when <see cref="Kind"/> is <c>"fan_out"</c>.
    /// Example: <c>{{ outputs.extract.result | from_json }}</c>.
    /// </summary>
    public string? Iterable { get; init; }

    /// <summary>Maximum number of child steps to execute concurrently. 0 = no limit.</summary>
    public int FanOutMaxConcurrency { get; init; } = 4;

    /// <summary>
    /// Step template cloned for each iterable item. The template's <c>Id</c> and <c>input</c>
    /// are rewritten per iteration. Only valid when <see cref="Kind"/> is <c>"fan_out"</c>.
    /// </summary>
    public MetaSkillStepDefinition? FanOutTemplate { get; init; }

    /// <summary>
    /// Merge mode for child step outputs:
    /// <c>"concat"</c> — join with newlines (default);
    /// <c>"json_array"</c> — wrap as JSON array;
    /// <c>"first"</c> — return first child output;
    /// <c>"last"</c> — return last child output.
    /// </summary>
    public string FanOutMergeMode { get; init; } = "concat";
}

/// <summary>
/// Conditional route to another declared step in the same composition.
/// </summary>
public sealed class MetaRouteDefinition
{
    /// <summary>Optional expression controlling whether the route is taken.</summary>
    public string? When { get; init; }

    /// <summary>Destination step identifier.</summary>
    public required string To { get; init; }
}

/// <summary>
/// Schema for a clarify step.
/// </summary>
public sealed class MetaClarifySchema
{
    /// <summary>Interaction mode, such as chat or form.</summary>
    public string Mode { get; init; } = "chat";

    /// <summary>Whether freeform natural-language extraction is enabled.</summary>
    public bool ExtractNaturalLanguage { get; init; }

    /// <summary>Declared fields for form mode.</summary>
    public IReadOnlyList<MetaClarifyField> Fields { get; init; } = [];

    /// <summary>Words that cancel the clarify interaction.</summary>
    public IReadOnlyList<string> CancelWords { get; init; } = [];

    /// <summary>Optional expression that skips clarify prompting when truthy.</summary>
    public string? SkipIf { get; init; }

    /// <summary>Optional timeout in seconds for clarify input.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// One field inside a clarify schema.
/// </summary>
public sealed class MetaClarifyField
{
    /// <summary>Field identifier.</summary>
    public required string Name { get; init; }

    /// <summary>Field type, such as string or enum.</summary>
    public required string Type { get; init; }

    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>Optional default value.</summary>
    public JsonElement? DefaultValue { get; init; }

    /// <summary>Allowed values for enum fields.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Optional minimum string length.</summary>
    public int? MinLength { get; init; }

    /// <summary>Optional maximum string length.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Optional numeric lower bound.</summary>
    public double? Min { get; init; }

    /// <summary>Optional numeric upper bound.</summary>
    public double? Max { get; init; }
}

/// <summary>
/// Retry policy for a single meta-skill step.
/// </summary>
public sealed class MetaStepRetryPolicy
{
    /// <summary>Total attempts, including the initial try.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Delay before retry attempts, in milliseconds.</summary>
    public int BackoffMs { get; init; }
}

/// <summary>
/// Contract for validating one meta-skill step's output before dependents run.
/// </summary>
public sealed class MetaStepOutputContract
{
    /// <summary>Expected format. Supported values: text, json.</summary>
    public string Format { get; init; } = "text";

    /// <summary>Required top-level properties when <see cref="Format"/> is json.</summary>
    public IReadOnlyList<string> RequiredProperties { get; init; } = [];
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

/// <summary>
/// Lightweight descriptor for a tool that can be serialized into templates
/// and prompt contexts without exposing the full <see cref="ITool"/> contract.
/// </summary>
public sealed record ToolDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameterSchema")] string ParameterSchema);

/// <summary>
/// Runtime execution result for a single meta-skill step.
/// Shared between <c>AgentRuntime</c> and <c>MafAgentRuntime</c>.
/// </summary>
public readonly record struct MetaStepExecutionResult(
    string Id,
    string Kind,
    string Status,
    string? FailureCode,
    double DurationMs,
    bool Continued,
    SessionMetaStepExecutionEvidence? ExecutionEvidence = null);
