using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;

namespace OpenClaw.Agent.Tools;

public sealed class ResolveCapabilityTool : ITool
{
    private readonly McpServerToolRegistry _registry;

    public ResolveCapabilityTool(McpServerToolRegistry registry)
    {
        _registry = registry;
    }

    public string Name => "resolve_capability";

    public string Description =>
        "Resolve an intent into a Router-bound server+tool+schema. Zero LLM round-trips.";

    public string ParameterSchema =>
        "{\"type\":\"object\",\"required\":[\"task_description\"],\"properties\":{" +
        "\"task_description\":{\"type\":\"string\"}," +
        "\"key_words\":{\"type\":\"string\"}," +
        "\"selection_policy\":{\"type\":\"string\",\"enum\":[\"first\",\"exact_name\"]}" +
        "}}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => throw new NotImplementedException("ResolveCapabilityTool not yet implemented — see Task 4.");
}
