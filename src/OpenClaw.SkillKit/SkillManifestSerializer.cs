using System.Text;
using OpenClaw.SkillKit.Abstractions;

namespace OpenClaw.SkillKit;

public static class SkillManifestSerializer
{
    public static async Task WriteAsync(string path, SkillManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Serialize(manifest), Encoding.UTF8, cancellationToken);
    }

    public static async Task<SkillManifest> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return Deserialize(text);
    }

    public static string Serialize(SkillManifest manifest)
    {
        var builder = new StringBuilder();
        WriteScalar(builder, 0, "id", manifest.Id);
        WriteScalar(builder, 0, "name", manifest.Name);
        WriteScalar(builder, 0, "version", manifest.Version);
        WriteScalar(builder, 0, "category", manifest.Category);
        WriteScalar(builder, 0, "description", manifest.Description);
        WriteList(builder, 0, "aliases", manifest.Aliases);
        builder.AppendLine("intent:");
        WriteScalar(builder, 2, "outcome", manifest.Intent.Outcome);
        builder.AppendLine("inputs:");
        WriteList(builder, 2, "required", manifest.Inputs.Required);
        WriteList(builder, 2, "optional", manifest.Inputs.Optional);
        builder.AppendLine("outputs:");
        WriteList(builder, 2, "required", manifest.Outputs.Required);
        WriteList(builder, 2, "optional", manifest.Outputs.Optional);
        builder.AppendLine("tools:");
        WriteList(builder, 2, "allowed", manifest.Tools.Allowed);
        WriteList(builder, 2, "forbidden", manifest.Tools.Forbidden);
        WriteList(builder, 2, "approval_required", manifest.Tools.ApprovalRequired);
        builder.AppendLine("guardrails:");
        WriteList(builder, 2, "must_not", manifest.Guardrails.MustNot);
        builder.AppendLine("human_approval:");
        WriteList(builder, 2, "required_for", manifest.HumanApproval.RequiredFor);
        builder.AppendLine("validation:");
        WriteList(builder, 2, "checks", manifest.Validation.Checks);
        builder.AppendLine("workflow:");
        builder.AppendLine("  steps:");
        foreach (var step in manifest.Workflow.Steps)
        {
            builder.Append("    - id: ").AppendLine(EncodeScalar(step.Id));
            WriteScalar(builder, 6, "name", step.Name);
            WriteScalar(builder, 6, "type", StepTypeToYaml(step.Type));
            WriteScalar(builder, 6, "description", step.Description);
        }

        return builder.ToString();
    }

    public static SkillManifest Deserialize(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<StepBuilder>();
        string section = string.Empty;
        string listKey = string.Empty;
        StepBuilder? currentStep = null;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')))
        {
            var trimmed = rawLine.Trim();
            var indent = rawLine.Length - rawLine.TrimStart(' ').Length;
            if (trimmed.EndsWith(':') && !trimmed.StartsWith('-'))
            {
                var key = trimmed[..^1];
                if (indent == 0)
                {
                    section = key;
                    listKey = key;
                    currentStep = null;
                }
                else if (section == "workflow" && key == "steps")
                {
                    listKey = "workflow.steps";
                }
                else
                {
                    listKey = Combine(section, key);
                    EnsureList(lists, listKey);
                }

                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = trimmed[2..].Trim();
                if (section == "workflow" && listKey == "workflow.steps" && item.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    currentStep = new StepBuilder { Id = DecodeScalar(item[3..].Trim()) };
                    steps.Add(currentStep);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(listKey))
                    EnsureList(lists, listKey).Add(DecodeScalar(item));
                continue;
            }

            var colonIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
                continue;

            var scalarKey = trimmed[..colonIndex].Trim();
            var scalarValue = DecodeScalar(trimmed[(colonIndex + 1)..].Trim());
            if (section == "workflow" && currentStep is not null)
            {
                currentStep.Set(scalarKey, scalarValue);
                continue;
            }

            var fullKey = indent == 0 ? scalarKey : Combine(section, scalarKey);
            values[fullKey] = scalarValue;
        }

        return new SkillManifest
        {
            Id = Get(values, "id"),
            Name = Get(values, "name"),
            Version = Get(values, "version", "0.1.0"),
            Category = Get(values, "category", "general"),
            Description = Get(values, "description"),
            Aliases = GetList(lists, "aliases"),
            Intent = new SkillIntent { Outcome = Get(values, "intent.outcome") },
            Inputs = new SkillInputs
            {
                Required = GetList(lists, "inputs.required"),
                Optional = GetList(lists, "inputs.optional")
            },
            Outputs = new SkillOutputs
            {
                Required = GetList(lists, "outputs.required"),
                Optional = GetList(lists, "outputs.optional")
            },
            Tools = new SkillToolPolicy
            {
                Allowed = GetList(lists, "tools.allowed"),
                Forbidden = GetList(lists, "tools.forbidden"),
                ApprovalRequired = GetList(lists, "tools.approval_required")
            },
            Guardrails = new SkillGuardrails { MustNot = GetList(lists, "guardrails.must_not") },
            HumanApproval = new SkillHumanApprovalPolicy { RequiredFor = GetList(lists, "human_approval.required_for") },
            Validation = new SkillValidationPolicy { Checks = GetList(lists, "validation.checks") },
            Workflow = new SkillWorkflow { Steps = steps.Select(static step => step.ToStep()).ToArray() }
        };
    }

    public static string SerializeWorkflow(SkillWorkflow workflow)
    {
        var builder = new StringBuilder();
        builder.AppendLine("steps:");
        foreach (var step in workflow.Steps)
        {
            builder.Append("  - id: ").AppendLine(EncodeScalar(step.Id));
            WriteScalar(builder, 4, "name", step.Name);
            WriteScalar(builder, 4, "type", StepTypeToYaml(step.Type));
            WriteScalar(builder, 4, "description", step.Description);
        }

        return builder.ToString();
    }

    public static string SerializeTools(SkillToolPolicy tools)
    {
        var builder = new StringBuilder();
        WriteList(builder, 0, "allowed", tools.Allowed);
        WriteList(builder, 0, "forbidden", tools.Forbidden);
        WriteList(builder, 0, "approval_required", tools.ApprovalRequired);
        return builder.ToString();
    }

    private static void WriteScalar(StringBuilder builder, int indent, string key, string value)
    {
        builder.Append(' ', indent).Append(key).Append(": ").AppendLine(EncodeScalar(value));
    }

    private static void WriteList(StringBuilder builder, int indent, string key, IReadOnlyList<string> values)
    {
        builder.Append(' ', indent).Append(key).AppendLine(":");
        foreach (var value in values)
            builder.Append(' ', indent + 2).Append("- ").AppendLine(EncodeScalar(value));
    }

    private static string EncodeScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string DecodeScalar(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next
                });
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static string StepTypeToYaml(SkillWorkflowStepType type) => type switch
    {
        SkillWorkflowStepType.Input => "input",
        SkillWorkflowStepType.Generation => "generation",
        SkillWorkflowStepType.Validation => "validation",
        SkillWorkflowStepType.Approval => "approval",
        SkillWorkflowStepType.Output => "output",
        _ => "reasoning"
    };

    private static SkillWorkflowStepType ParseStepType(string value) => value.ToLowerInvariant() switch
    {
        "input" => SkillWorkflowStepType.Input,
        "generation" => SkillWorkflowStepType.Generation,
        "validation" => SkillWorkflowStepType.Validation,
        "approval" => SkillWorkflowStepType.Approval,
        "output" => SkillWorkflowStepType.Output,
        _ => SkillWorkflowStepType.Reasoning
    };

    private static string Combine(string section, string key) => string.IsNullOrWhiteSpace(section) ? key : $"{section}.{key}";

    private static List<string> EnsureList(Dictionary<string, List<string>> lists, string key)
    {
        if (!lists.TryGetValue(key, out var list))
        {
            list = [];
            lists[key] = list;
        }

        return list;
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static IReadOnlyList<string> GetList(Dictionary<string, List<string>> values, string key) =>
        values.TryGetValue(key, out var list) ? list : [];

    private sealed class StepBuilder
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; private set; } = string.Empty;
        public string Type { get; private set; } = string.Empty;
        public string Description { get; private set; } = string.Empty;

        public void Set(string key, string value)
        {
            switch (key)
            {
                case "name":
                    Name = value;
                    break;
                case "type":
                    Type = value;
                    break;
                case "description":
                    Description = value;
                    break;
            }
        }

        public SkillWorkflowStep ToStep() => new()
        {
            Id = Id,
            Name = Name,
            Type = ParseStepType(Type),
            Description = Description
        };
    }
}
