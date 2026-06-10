using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Initial meta entrypoint tool. Resolves a named meta skill and returns
/// a structured execution intent payload for downstream orchestration.
/// </summary>
public sealed class MetaInvokeTool : ITool
{
    private readonly Func<IReadOnlyList<SkillDefinition>> _provider;

    public MetaInvokeTool(Func<IReadOnlyList<SkillDefinition>> provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "meta_invoke";

    public string Description =>
        "Invoke a meta skill by name and return a structured execution intent payload. "
        + "Use when a user intent matches a kind=meta skill.";

    public string ParameterSchema =>
        """{"type":"object","properties":{"skill":{"type":"string","description":"Meta skill name to invoke."},"input":{"type":"string","description":"Optional user input passed to the meta execution pipeline."}},"required":["skill"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryParseArguments(argumentsJson, out var skillName, out var input, out var error))
            return ValueTask.FromResult(error!);

        var skills = _provider() ?? [];
        var matched = skills.FirstOrDefault(s =>
            s.Kind == SkillKind.Meta
            && string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)
            && !s.DisableModelInvocation);

        if (matched is null)
        {
            var available = string.Join(", ",
                skills.Where(s => s.Kind == SkillKind.Meta && !s.DisableModelInvocation)
                    .Select(s => s.Name));
            return ValueTask.FromResult(
                $"Error: meta skill '{skillName}' not found. Available: {(string.IsNullOrWhiteSpace(available) ? "(none)" : available)}.");
        }

        var payload = new MetaInvokeIntent
        {
            Skill = matched.Name,
            Input = input,
            FinalTextMode = matched.FinalTextMode,
            MetaPriority = matched.MetaPriority,
            Steps = [.. matched.Composition?.Steps.Select(s => new MetaInvokeStepSummary { Id = s.Id, Kind = s.Kind, DependsOn = [.. s.DependsOn] }) ?? []]
        };

        return ValueTask.FromResult(JsonSerializer.Serialize(payload, MetaInvokeToolJsonContext.Default.MetaInvokeIntent));
    }

    private static bool TryParseArguments(string json, out string? skill, out string? input, out string? error)
    {
        skill = null;
        input = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Error: missing required argument 'skill'.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Error: invalid JSON arguments. Expected an object like {\"skill\":\"<name>\"}.";
                return false;
            }

            if (doc.RootElement.TryGetProperty("skill", out var skillNode) && skillNode.ValueKind == JsonValueKind.String)
                skill = skillNode.GetString();

            if (doc.RootElement.TryGetProperty("input", out var inputNode) && inputNode.ValueKind == JsonValueKind.String)
                input = inputNode.GetString();
        }
        catch (JsonException)
        {
            error = "Error: invalid JSON arguments. Expected {\"skill\":\"<name>\"}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(skill))
        {
            error = "Error: missing required argument 'skill'.";
            return false;
        }

        return true;
    }
}

public sealed class MetaInvokeIntent
{
    public required string Skill { get; init; }
    public string? Input { get; init; }
    public string? FinalTextMode { get; init; }
    public int? MetaPriority { get; init; }
    public MetaInvokeStepSummary[] Steps { get; init; } = [];
}

public sealed class MetaInvokeStepSummary
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string[] DependsOn { get; init; } = [];
}

[JsonSerializable(typeof(MetaInvokeIntent))]
[JsonSerializable(typeof(MetaInvokeStepSummary[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class MetaInvokeToolJsonContext : JsonSerializerContext;
