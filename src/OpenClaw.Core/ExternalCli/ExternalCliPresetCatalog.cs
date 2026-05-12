using OpenClaw.Core.Models;

namespace OpenClaw.Core.ExternalCli;

public static class ExternalCliPresetCatalog
{
    private const string RepoPattern = "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$";
    private const string NumberPattern = "^[0-9]+$";
    private const string SimpleNamePattern = "^[A-Za-z0-9_.:-]+$";

    private static readonly IReadOnlyDictionary<string, ExternalCliPresetDefinition> Presets = BuildPresets();

    public static IReadOnlyList<ExternalCliPresetSummary> List()
        => Presets.Values
            .Select(static item => item.Summary)
            .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryGet(string? id, out ExternalCliPresetSummary? summary)
    {
        if (!string.IsNullOrWhiteSpace(id) && Presets.TryGetValue(id.Trim(), out var preset))
        {
            summary = preset.Summary;
            return true;
        }

        summary = null;
        return false;
    }

    public static IReadOnlyList<string> FindUnknownPresetIds(ExternalCliOptions options)
        => (options.Presets ?? [])
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(static id => !Presets.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static ExternalCliOptions Apply(ExternalCliOptions options)
    {
        var effective = new ExternalCliOptions
        {
            Enabled = options.Enabled,
            DefaultTimeoutSeconds = options.DefaultTimeoutSeconds,
            MaxStdoutBytes = options.MaxStdoutBytes,
            MaxStderrBytes = options.MaxStderrBytes,
            RedactSecrets = options.RedactSecrets,
            AllowFreeformCommands = options.AllowFreeformCommands,
            RequireApprovalForMutatingCommands = options.RequireApprovalForMutatingCommands,
            Presets = (options.Presets ?? []).ToArray(),
            Connectors = new Dictionary<string, ExternalCliConnectorOptions>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var preset in (options.Presets ?? [])
            .Where(static id => !string.IsNullOrWhiteSpace(id) && Presets.ContainsKey(id.Trim()))
            .Select(static id => Presets[id.Trim()]))
        {
            effective.Connectors[preset.Summary.Connector] = CloneConnector(preset.Connector);
        }

        foreach (var (name, connector) in options.Connectors)
        {
            effective.Connectors[name] = effective.Connectors.TryGetValue(name, out var presetConnector)
                ? MergeConnector(presetConnector, connector)
                : CloneConnector(connector);
        }

        return effective;
    }

    private static IReadOnlyDictionary<string, ExternalCliPresetDefinition> BuildPresets()
    {
        var definitions = new[]
        {
            Preset(
                "gh",
                "gh",
                "GitHub CLI",
                "Read-oriented GitHub repository, issue, pull request, and release commands.",
                ["github", "vcs"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "GitHub CLI",
                    Executable = "gh",
                    DefaultOutputFormat = ExternalCliOutputFormat.Json,
                    StatusCommand = Status(["auth", "status"], 20),
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["repo_view"] = Command(
                            "View repository metadata.",
                            ["repo", "view", "{{repo}}", "--json", "name,owner,description,url,isPrivate"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["github", "repository"]),
                        ["issue_list"] = Command(
                            "List repository issues.",
                            ["issue", "list", "--repo", "{{repo}}", "--json", "number,title,state,author,url,labels"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["github", "issues"]),
                        ["pr_list"] = Command(
                            "List repository pull requests.",
                            ["pr", "list", "--repo", "{{repo}}", "--json", "number,title,state,author,url,headRefName,baseRefName"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["github", "pull-requests"]),
                        ["pr_view"] = Command(
                            "View pull request metadata.",
                            ["pr", "view", "{{number}}", "--repo", "{{repo}}", "--json", "number,title,state,url,mergeStateStatus,reviewDecision,headRefName,baseRefName"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern),
                                ["number"] = Required("Pull request number.", pattern: NumberPattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["github", "pull-requests"]),
                        ["release_list"] = Command(
                            "List repository releases.",
                            ["release", "list", "--repo", "{{repo}}", "--json", "name,tagName,isDraft,isPrerelease,publishedAt,url"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["github", "releases"]),
                        ["issue_comment"] = Command(
                            "Add a comment to a GitHub issue or pull request.",
                            ["issue", "comment", "{{number}}", "--repo", "{{repo}}", "--body", "{{body}}"],
                            readOnly: false,
                            risk: ExternalCliRiskLevel.Medium,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["repo"] = Required("owner/repo repository name.", pattern: RepoPattern),
                                ["number"] = Required("Issue or pull request number.", pattern: NumberPattern),
                                ["body"] = Required("Comment body.", maxLength: 16_000)
                            },
                            tags: ["github", "issues", "mutating"])
                    }
                }),
            Preset(
                "az",
                "az",
                "Azure CLI",
                "Read-only Azure account, resource group, and resource inventory commands.",
                ["azure", "cloud"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "Azure CLI",
                    Executable = "az",
                    DefaultOutputFormat = ExternalCliOutputFormat.Json,
                    StatusCommand = Status(["account", "show", "--output", "json"], 20),
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["account_show"] = Command("Show the active Azure account.", ["account", "show", "--output", "json"], output: ExternalCliOutputFormat.Json, tags: ["azure", "account"]),
                        ["group_list"] = Command("List Azure resource groups.", ["group", "list", "--output", "json"], output: ExternalCliOutputFormat.Json, tags: ["azure", "resource-groups"]),
                        ["resource_list"] = Command("List Azure resources.", ["resource", "list", "--output", "json"], output: ExternalCliOutputFormat.Json, tags: ["azure", "resources"])
                    }
                }),
            Preset(
                "kubectl",
                "kubectl",
                "kubectl",
                "Read-only Kubernetes context and workload inventory commands.",
                ["kubernetes", "cluster"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "kubectl",
                    Executable = "kubectl",
                    DefaultOutputFormat = ExternalCliOutputFormat.Json,
                    StatusCommand = Status(["config", "current-context"], 10),
                    VersionCommand = Status(["version", "--client=true", "-o", "json"], 15),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["current_context"] = Command("Show the current Kubernetes context.", ["config", "current-context"], output: ExternalCliOutputFormat.Text, tags: ["kubernetes", "context"]),
                        ["get_pods_all"] = Command("List pods in all namespaces.", ["get", "pods", "--all-namespaces", "-o", "json"], output: ExternalCliOutputFormat.Json, tags: ["kubernetes", "pods"]),
                        ["get_pods"] = Command(
                            "List pods in one namespace.",
                            ["get", "pods", "--namespace", "{{namespace}}", "-o", "json"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["namespace"] = Required("Kubernetes namespace.", pattern: SimpleNamePattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["kubernetes", "pods"]),
                        ["get_services_all"] = Command("List services in all namespaces.", ["get", "services", "--all-namespaces", "-o", "json"], output: ExternalCliOutputFormat.Json, tags: ["kubernetes", "services"]),
                        ["get_deployments_all"] = Command("List deployments in all namespaces.", ["get", "deployments", "--all-namespaces", "-o", "json"], output: ExternalCliOutputFormat.Json, tags: ["kubernetes", "deployments"])
                    }
                }),
            Preset(
                "stripe",
                "stripe",
                "Stripe CLI",
                "Conservative Stripe CLI commands with PII-bearing reads approval-gated.",
                ["stripe", "payments"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "Stripe CLI",
                    Executable = "stripe",
                    DefaultOutputFormat = ExternalCliOutputFormat.Text,
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["version"] = Command("Show Stripe CLI version.", ["--version"], output: ExternalCliOutputFormat.Text, tags: ["stripe", "diagnostics"]),
                        ["customers_list"] = Command(
                            "List Stripe customers with a small limit. Customer data can contain PII.",
                            ["customers", "list", "--limit", "{{limit}}"],
                            risk: ExternalCliRiskLevel.Medium,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["limit"] = Required("Maximum customers to list.", pattern: NumberPattern)
                            },
                            output: ExternalCliOutputFormat.Text,
                            tags: ["stripe", "customers", "pii"])
                    }
                }),
            Preset(
                "lark",
                "lark",
                "Lark CLI",
                "Generic lark-cli compatible templates for auth checks, docs, sheets, and guarded sends.",
                ["lark", "feishu", "collaboration"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "Lark CLI",
                    Executable = "lark-cli",
                    DefaultOutputFormat = ExternalCliOutputFormat.Json,
                    StatusCommand = Status(["auth", "status"], 20),
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["auth_status"] = Command("Show Lark CLI authentication status.", ["auth", "status"], output: ExternalCliOutputFormat.Json, tags: ["lark", "auth"]),
                        ["docs_search"] = Command(
                            "Search Lark docs.",
                            ["docs", "search", "{{query}}", "--output", "json"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["query"] = Required("Search query.", maxLength: 500)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["lark", "docs"]),
                        ["docs_read"] = Command(
                            "Read a Lark document.",
                            ["docs", "read", "{{document_id}}", "--output", "json"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["document_id"] = Required("Lark document identifier.", pattern: SimpleNamePattern)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["lark", "docs"]),
                        ["sheets_read"] = Command(
                            "Read a Lark sheet range.",
                            ["sheets", "read", "{{sheet_id}}", "{{range}}", "--output", "json"],
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["sheet_id"] = Required("Lark sheet identifier.", pattern: SimpleNamePattern),
                                ["range"] = Required("Sheet range.", maxLength: 200)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["lark", "sheets"]),
                        ["message_send"] = Command(
                            "Send a Lark message.",
                            ["messages", "send", "--chat", "{{chat_id}}", "--text", "{{text}}", "--output", "json"],
                            readOnly: false,
                            risk: ExternalCliRiskLevel.Medium,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["chat_id"] = Required("Lark chat identifier.", pattern: SimpleNamePattern),
                                ["text"] = Required("Message text.", maxLength: 4000)
                            },
                            output: ExternalCliOutputFormat.Json,
                            tags: ["lark", "messages", "mutating"])
                    }
                }),
            Preset(
                "github-copilot",
                "github-copilot",
                "GitHub Copilot CLI",
                "GitHub Copilot CLI non-interactive prompt and diagnostic commands.",
                ["github", "copilot", "ai"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "GitHub Copilot CLI",
                    Executable = "copilot",
                    DefaultOutputFormat = ExternalCliOutputFormat.Text,
                    VersionCommand = Status(["version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["version"] = Command("Show GitHub Copilot CLI version.", ["version"], output: ExternalCliOutputFormat.Text, tags: ["github", "copilot", "diagnostics"]),
                        ["prompt"] = Command(
                            "Run a non-interactive GitHub Copilot CLI prompt.",
                            ["-p", "{{prompt}}"],
                            readOnly: false,
                            risk: ExternalCliRiskLevel.High,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["prompt"] = Required("Prompt to send to GitHub Copilot CLI.", maxLength: 8000)
                            },
                            tags: ["github", "copilot", "ai-agent"])
                    }
                }),
            Preset(
                "codex",
                "codex",
                "Codex CLI",
                "Codex CLI non-interactive exec commands with explicit sandbox choices.",
                ["codex", "openai", "ai"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "Codex CLI",
                    Executable = "codex",
                    DefaultOutputFormat = ExternalCliOutputFormat.Text,
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["version"] = Command("Show Codex CLI version.", ["--version"], output: ExternalCliOutputFormat.Text, tags: ["codex", "diagnostics"]),
                        ["exec_readonly"] = Command(
                            "Run Codex non-interactively in an explicit read-only sandbox.",
                            ["exec", "--sandbox", "read-only", "{{prompt}}"],
                            risk: ExternalCliRiskLevel.Medium,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["prompt"] = Required("Prompt to send to Codex CLI.", maxLength: 8000)
                            },
                            tags: ["codex", "ai-agent"]),
                        ["exec_readonly_json"] = Command(
                            "Run Codex non-interactively with JSON Lines events in a read-only sandbox.",
                            ["exec", "--sandbox", "read-only", "--json", "{{prompt}}"],
                            risk: ExternalCliRiskLevel.Medium,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["prompt"] = Required("Prompt to send to Codex CLI.", maxLength: 8000)
                            },
                            output: ExternalCliOutputFormat.Ndjson,
                            tags: ["codex", "ai-agent", "jsonl"]),
                        ["exec_workspace_write"] = Command(
                            "Run Codex non-interactively with workspace-write sandboxing.",
                            ["exec", "--sandbox", "workspace-write", "{{prompt}}"],
                            readOnly: false,
                            risk: ExternalCliRiskLevel.High,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["prompt"] = Required("Prompt to send to Codex CLI.", maxLength: 8000)
                            },
                            tags: ["codex", "ai-agent", "mutating"])
                    }
                }),
            Preset(
                "gemini",
                "gemini",
                "Gemini CLI",
                "Gemini CLI non-interactive prompt and diagnostic commands.",
                ["gemini", "google", "ai"],
                new ExternalCliConnectorOptions
                {
                    Enabled = false,
                    DisplayName = "Gemini CLI",
                    Executable = "gemini",
                    DefaultOutputFormat = ExternalCliOutputFormat.Text,
                    VersionCommand = Status(["--version"], 10),
                    Commands = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["version"] = Command("Show Gemini CLI version.", ["--version"], output: ExternalCliOutputFormat.Text, tags: ["gemini", "diagnostics"]),
                        ["prompt"] = Command(
                            "Run a non-interactive Gemini CLI prompt.",
                            ["-p", "{{prompt}}"],
                            readOnly: false,
                            risk: ExternalCliRiskLevel.High,
                            requiresApproval: true,
                            parameters: new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["prompt"] = Required("Prompt to send to Gemini CLI.", maxLength: 8000)
                            },
                            tags: ["gemini", "ai-agent"])
                    }
                })
        };

        return definitions.ToDictionary(static item => item.Summary.Id, static item => item, StringComparer.OrdinalIgnoreCase);
    }

    private static ExternalCliPresetDefinition Preset(
        string id,
        string connector,
        string displayName,
        string description,
        string[] tags,
        ExternalCliConnectorOptions options)
        => new(
            new ExternalCliPresetSummary
            {
                Id = id,
                Connector = connector,
                DisplayName = displayName,
                Description = description,
                Tags = tags,
                Commands = options.Commands.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            options);

    private static ExternalCliCommandOptions Command(
        string description,
        string[] args,
        bool readOnly = true,
        string risk = ExternalCliRiskLevel.Low,
        bool requiresApproval = false,
        Dictionary<string, ExternalCliParameterOptions>? parameters = null,
        string output = "",
        string[]? tags = null)
        => new()
        {
            Description = description,
            ArgsTemplate = args,
            ReadOnly = readOnly,
            RiskLevel = risk,
            RequiresApproval = requiresApproval,
            Parameters = parameters ?? new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase),
            StructuredOutput = output,
            Tags = tags ?? []
        };

    private static ExternalCliParameterOptions Required(
        string description,
        int? maxLength = null,
        string? pattern = null,
        params string[] allowedValues)
        => new()
        {
            Required = true,
            Description = description,
            MaxLength = maxLength,
            Pattern = pattern,
            AllowedValues = allowedValues
        };

    private static ExternalCliStatusCommandOptions Status(string[] args, int timeoutSeconds)
        => new()
        {
            Args = args,
            TimeoutSeconds = timeoutSeconds
        };

    private static ExternalCliConnectorOptions MergeConnector(
        ExternalCliConnectorOptions preset,
        ExternalCliConnectorOptions configured)
    {
        var commands = CloneCommands(preset.Commands);
        foreach (var (name, command) in configured.Commands)
        {
            commands[name] = commands.TryGetValue(name, out var presetCommand)
                ? MergeCommand(presetCommand, command)
                : CloneCommand(command);
        }

        return new ExternalCliConnectorOptions
        {
            Enabled = preset.Enabled || configured.Enabled,
            DisplayName = Prefer(configured.DisplayName, preset.DisplayName),
            Executable = Prefer(configured.Executable, preset.Executable),
            DefaultArgs = configured.DefaultArgs.Length > 0 ? configured.DefaultArgs.ToArray() : preset.DefaultArgs.ToArray(),
            WorkingDirectory = PreferNullable(configured.WorkingDirectory, preset.WorkingDirectory),
            Environment = MergeDictionary(preset.Environment, configured.Environment),
            StatusCommand = MergeStatusCommand(preset.StatusCommand, configured.StatusCommand),
            VersionCommand = MergeStatusCommand(preset.VersionCommand, configured.VersionCommand),
            DefaultOutputFormat = PreferOutputFormat(configured.DefaultOutputFormat, preset.DefaultOutputFormat),
            RequiresApproval = preset.RequiresApproval || configured.RequiresApproval,
            RedactionRules = MergeStringSet(preset.RedactionRules, configured.RedactionRules),
            Commands = commands
        };
    }

    private static ExternalCliCommandOptions MergeCommand(
        ExternalCliCommandOptions preset,
        ExternalCliCommandOptions configured)
        => new()
        {
            Description = Prefer(configured.Description, preset.Description),
            ArgsTemplate = configured.ArgsTemplate.Length > 0 ? configured.ArgsTemplate.ToArray() : preset.ArgsTemplate.ToArray(),
            Parameters = MergeParameters(preset.Parameters, configured.Parameters),
            AllowUnknownParameters = preset.AllowUnknownParameters || configured.AllowUnknownParameters,
            RiskLevel = MaxRisk(preset.RiskLevel, configured.RiskLevel),
            ReadOnly = preset.ReadOnly && configured.ReadOnly,
            RequiresApproval = preset.RequiresApproval || configured.RequiresApproval,
            SupportsDryRun = preset.SupportsDryRun || configured.SupportsDryRun,
            DryRunArgsTemplate = configured.DryRunArgsTemplate.Length > 0 ? configured.DryRunArgsTemplate.ToArray() : preset.DryRunArgsTemplate.ToArray(),
            StructuredOutput = Prefer(configured.StructuredOutput, preset.StructuredOutput),
            TimeoutSeconds = configured.TimeoutSeconds ?? preset.TimeoutSeconds,
            WorkingDirectory = PreferNullable(configured.WorkingDirectory, preset.WorkingDirectory),
            Environment = MergeDictionary(preset.Environment, configured.Environment),
            RedactionRules = MergeStringSet(preset.RedactionRules, configured.RedactionRules),
            RequiredScopes = MergeStringSet(preset.RequiredScopes, configured.RequiredScopes),
            RequiredIdentity = PreferNullable(configured.RequiredIdentity, preset.RequiredIdentity),
            Tags = MergeStringSet(preset.Tags, configured.Tags)
        };

    private static ExternalCliConnectorOptions CloneConnector(ExternalCliConnectorOptions source)
        => new()
        {
            Enabled = source.Enabled,
            DisplayName = source.DisplayName,
            Executable = source.Executable,
            DefaultArgs = source.DefaultArgs.ToArray(),
            WorkingDirectory = source.WorkingDirectory,
            Environment = new Dictionary<string, string>(source.Environment, StringComparer.Ordinal),
            StatusCommand = CloneStatusCommand(source.StatusCommand),
            VersionCommand = CloneStatusCommand(source.VersionCommand),
            DefaultOutputFormat = source.DefaultOutputFormat,
            RequiresApproval = source.RequiresApproval,
            RedactionRules = source.RedactionRules.ToArray(),
            Commands = CloneCommands(source.Commands)
        };

    private static Dictionary<string, ExternalCliCommandOptions> CloneCommands(IReadOnlyDictionary<string, ExternalCliCommandOptions> source)
    {
        var result = new Dictionary<string, ExternalCliCommandOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, command) in source)
            result[name] = CloneCommand(command);

        return result;
    }

    private static ExternalCliCommandOptions CloneCommand(ExternalCliCommandOptions source)
        => new()
        {
            Description = source.Description,
            ArgsTemplate = source.ArgsTemplate.ToArray(),
            Parameters = CloneParameters(source.Parameters),
            AllowUnknownParameters = source.AllowUnknownParameters,
            RiskLevel = source.RiskLevel,
            ReadOnly = source.ReadOnly,
            RequiresApproval = source.RequiresApproval,
            SupportsDryRun = source.SupportsDryRun,
            DryRunArgsTemplate = source.DryRunArgsTemplate.ToArray(),
            StructuredOutput = source.StructuredOutput,
            TimeoutSeconds = source.TimeoutSeconds,
            WorkingDirectory = source.WorkingDirectory,
            Environment = new Dictionary<string, string>(source.Environment, StringComparer.Ordinal),
            RedactionRules = source.RedactionRules.ToArray(),
            RequiredScopes = source.RequiredScopes.ToArray(),
            RequiredIdentity = source.RequiredIdentity,
            Tags = source.Tags.ToArray()
        };

    private static Dictionary<string, ExternalCliParameterOptions> CloneParameters(IReadOnlyDictionary<string, ExternalCliParameterOptions> source)
    {
        var result = new Dictionary<string, ExternalCliParameterOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, parameter) in source)
            result[name] = CloneParameter(parameter);

        return result;
    }

    private static ExternalCliParameterOptions CloneParameter(ExternalCliParameterOptions source)
        => new()
        {
            Required = source.Required,
            Description = source.Description,
            MaxLength = source.MaxLength,
            Pattern = source.Pattern,
            AllowedValues = source.AllowedValues.ToArray()
        };

    private static ExternalCliStatusCommandOptions? CloneStatusCommand(ExternalCliStatusCommandOptions? source)
        => source is null
            ? null
            : new ExternalCliStatusCommandOptions
            {
                Args = source.Args.ToArray(),
                TimeoutSeconds = source.TimeoutSeconds
            };

    private static ExternalCliStatusCommandOptions? MergeStatusCommand(
        ExternalCliStatusCommandOptions? preset,
        ExternalCliStatusCommandOptions? configured)
    {
        if (preset is null)
            return CloneStatusCommand(configured);
        if (configured is null)
            return CloneStatusCommand(preset);

        return new ExternalCliStatusCommandOptions
        {
            Args = configured.Args.Length > 0 ? configured.Args.ToArray() : preset.Args.ToArray(),
            TimeoutSeconds = configured.TimeoutSeconds ?? preset.TimeoutSeconds
        };
    }

    private static Dictionary<string, ExternalCliParameterOptions> MergeParameters(
        IReadOnlyDictionary<string, ExternalCliParameterOptions> preset,
        IReadOnlyDictionary<string, ExternalCliParameterOptions> configured)
    {
        var result = CloneParameters(preset);
        foreach (var (name, parameter) in configured)
        {
            result[name] = result.TryGetValue(name, out var presetParameter)
                ? MergeParameter(presetParameter, parameter)
                : CloneParameter(parameter);
        }

        return result;
    }

    private static ExternalCliParameterOptions MergeParameter(
        ExternalCliParameterOptions preset,
        ExternalCliParameterOptions configured)
        => new()
        {
            Required = preset.Required || configured.Required,
            Description = Prefer(configured.Description, preset.Description),
            MaxLength = configured.MaxLength ?? preset.MaxLength,
            Pattern = PreferNullable(configured.Pattern, preset.Pattern),
            AllowedValues = configured.AllowedValues.Length > 0 ? configured.AllowedValues.ToArray() : preset.AllowedValues.ToArray()
        };

    private static Dictionary<string, string> MergeDictionary(
        IReadOnlyDictionary<string, string> preset,
        IReadOnlyDictionary<string, string> configured)
    {
        var result = new Dictionary<string, string>(preset, StringComparer.Ordinal);
        foreach (var (key, value) in configured)
            result[key] = value;

        return result;
    }

    private static string[] MergeStringSet(IReadOnlyList<string> preset, IReadOnlyList<string> configured)
        => preset.Concat(configured)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Prefer(string configured, string preset)
        => string.IsNullOrWhiteSpace(configured) ? preset : configured;

    private static string? PreferNullable(string? configured, string? preset)
        => string.IsNullOrWhiteSpace(configured) ? preset : configured;

    private static string PreferOutputFormat(string configured, string preset)
        => string.IsNullOrWhiteSpace(configured) ? preset : configured;

    private static string MaxRisk(string left, string right)
    {
        var normalizedLeft = ExternalCliRiskLevel.Normalize(left);
        var normalizedRight = ExternalCliRiskLevel.Normalize(right);
        return RiskRank(normalizedRight) > RiskRank(normalizedLeft) ? normalizedRight : normalizedLeft;
    }

    private static int RiskRank(string risk)
        => ExternalCliRiskLevel.Normalize(risk) switch
        {
            ExternalCliRiskLevel.High => 3,
            ExternalCliRiskLevel.Medium => 2,
            _ => 1
        };

    private sealed record ExternalCliPresetDefinition(ExternalCliPresetSummary Summary, ExternalCliConnectorOptions Connector);
}
