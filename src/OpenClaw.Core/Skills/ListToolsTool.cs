using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Runtime tool discovery — returns lightweight descriptors for every registered tool
/// so that MetaSKILL steps and LLM agents can reason about available capabilities.
/// Registered as a built-in tool alongside the runtime itself.
/// </summary>
public sealed class ListToolsTool : ITool
{
    private readonly Func<IReadOnlyList<ToolDescriptor>> _provider;

    public ListToolsTool(Func<IReadOnlyList<ToolDescriptor>> provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "list_tools";

    public string Description =>
        "List all available tools with their names, descriptions, and parameter schemas. "
        + "Use this to discover which tools are registered before calling them. "
        + "Optionally filter by a substring in the tool name.";

    public string ParameterSchema =>
        """{"type":"object","properties":{"filter":{"type":"string","description":"Optional substring filter for tool names"}}}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var filter = TryGetFilter(argumentsJson);
        var descriptors = _provider();
        var filtered = string.IsNullOrEmpty(filter)
            ? descriptors.ToList()
            : descriptors.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var result = JsonSerializer.Serialize(filtered, CoreJsonContext.Default.ListToolDescriptor);
        return ValueTask.FromResult(result);
    }

    private static string? TryGetFilter(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("filter", out var filterEl)
                && filterEl.ValueKind == JsonValueKind.String)
            {
                var value = filterEl.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            // Best-effort — ignore parse failures and return unfiltered.
        }

        return null;
    }
}
