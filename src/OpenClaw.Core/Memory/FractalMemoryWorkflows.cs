using System.Text.Json;

namespace OpenClaw.Core.Memory;

/// <summary>Explicit allowlist shared by agent tools and the MCP provider. No arbitrary MCP forwarding.</summary>
public static class FractalMemoryWorkflows
{
    public static IReadOnlyList<FractalMemoryWorkflow> All { get; } =
    [
        new("read", "Read a full Fractal Memory document or section. Returns the whole-document hash for safe writes and source resource links.",
            """{"path":{"type":"string","minLength":1},"file":{"type":"string","default":"state"},"section":{"type":"string"}}""", ["path"]),
        new("list", "Discover Fractal Memory nodes by canonical path. Archived nodes are excluded by default.",
            """{"scope":{"type":"string"},"includeArchived":{"type":"boolean","default":false}}""", []),
        new("attention", "Find stale Fractal Memory nodes, overdue reviews and missing working context.",
            """{"scope":{"type":"string"},"staleDays":{"type":"integer","minimum":1,"default":30}}""", []),
        new("decisions", "List managed Fractal Memory decision IDs and content. Superseded decisions are excluded by default.",
            """{"path":{"type":"string","minLength":1},"includeSuperseded":{"type":"boolean","default":false}}""", ["path"]),
        new("context", "Build source-linked Fractal Memory context. maxCharacters bounds the text field only; metadata and resource links are additional. Artifacts must be node-relative artifacts/ paths.",
            """{"path":{"type":"string","minLength":1},"maxCharacters":{"type":"integer","minimum":256,"maximum":1000000,"default":6000},"artifacts":{"type":"array","items":{"type":"string"}}}""", ["path"]),
        new("resume", "Resume a Fractal Memory node with bounded context, attention, its latest handoff and changed contract files.",
            """{"path":{"type":"string","minLength":1},"maxCharacters":{"type":"integer","minimum":256,"maximum":1000000,"default":6000}}""", ["path"]),
        new("handoff_list", "List handoffs for one exact Fractal Memory node, latest first.",
            """{"path":{"type":"string","minLength":1}}""", ["path"]),
        new("handoff_read", "Read a node's latest Fractal Memory handoff or a filename returned by fractal_memory_handoff_list.",
            """{"path":{"type":"string","minLength":1},"file":{"type":"string"}}""", ["path"]),
        new("node_create", "Create a Fractal Memory node from templates. Existing nodes are never overwritten.",
            """{"path":{"type":"string","minLength":1},"format":{"type":"string","enum":["Markdown","Html"],"default":"Markdown"}}""", ["path"], AlwaysWrites: true),
        new("update", "Replace an existing Markdown section. Read first and supply expectedHash from fractal_memory_read. Stale writes are rejected; unrelated sections are preserved.",
            """{"path":{"type":"string","minLength":1},"section":{"type":"string","minLength":1},"content":{"type":"string"},"expectedHash":{"type":"string","minLength":1},"file":{"type":"string","default":"state"}}""", ["path", "section", "content", "expectedHash"], AlwaysWrites: true),
        new("append", "Append a timeline entry or managed decision using the latest document hash. A decision may supersede an active decision ID.",
            """{"path":{"type":"string","minLength":1},"file":{"type":"string","enum":["timeline","decisions"]},"content":{"type":"string"},"expectedHash":{"type":"string","minLength":1},"supersedes":{"type":"string"}}""", ["path", "file", "content", "expectedHash"], AlwaysWrites: true),
        new("review", "Record the next Fractal Memory review timestamp and optional status. expectedHash must come from reading the index document.",
            """{"path":{"type":"string","minLength":1},"reviewAfter":{"type":"string","format":"date-time"},"expectedHash":{"type":"string","minLength":1},"status":{"type":"string","enum":["Active","Paused","Archived","Draft"]}}""", ["path", "reviewAfter", "expectedHash"], AlwaysWrites: true),
        new("doctor", "Diagnose Fractal Memory repository problems. repair=true rebuilds derived indexes after source validation; it requires write permission.",
            """{"repair":{"type":"boolean","default":false}}""", [], WriteFlag: "repair"),
        new("import", "Preview supplied note content at an explicit Fractal Memory node. sourceName must end in .md, .html, .htm or .txt. apply=true persists it and requires write permission. Conflicts block writes; no external host files are read.",
            """{"path":{"type":"string","minLength":1},"sourceName":{"type":"string","minLength":1},"content":{"type":"string"},"sourceModified":{"type":"string","format":"date-time"},"apply":{"type":"boolean","default":false}}""", ["path", "sourceName", "content"], WriteFlag: "apply")
    ];

    public static FractalMemoryWorkflow? Find(string operation)
        => All.FirstOrDefault(item => string.Equals(item.Operation, operation, StringComparison.Ordinal));
}

public sealed record FractalMemoryWorkflow(
    string Operation, string Description, string Properties, string[] Required,
    bool AlwaysWrites = false, string? WriteFlag = null)
{
    public string ToolName => "fractal_memory_" + Operation;
    public string McpToolName => "memory_" + Operation;
    public string ParameterSchema => "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + Properties +
        ",\"required\":[" + string.Join(",", Required.Select(name => "\"" + name + "\"")) + "]}";

    public bool IsMutation(JsonElement arguments)
        => AlwaysWrites || (WriteFlag is not null && arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(WriteFlag, out var flag) && flag.ValueKind != JsonValueKind.False);

    public string? Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return "Arguments must be a JSON object.";
        foreach (var name in Required)
            if (!arguments.TryGetProperty(name, out _))
                return $"{name} is required.";

        using var schema = JsonDocument.Parse(Properties);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return $"Duplicate argument '{property.Name}'.";
            if (!schema.RootElement.TryGetProperty(property.Name, out var rule))
                return $"Unknown argument '{property.Name}'.";
            var value = property.Value;
            var validType = rule.GetProperty("type").GetString() switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                "array" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                _ => false
            };
            if (!validType)
                return $"Invalid type for '{property.Name}'.";
            if (rule.TryGetProperty("minLength", out _) && string.IsNullOrWhiteSpace(value.GetString()))
                return $"{property.Name} must not be empty.";
            if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.GetString() == value.GetString()))
                return $"Unsupported value for '{property.Name}'.";
            if ((rule.TryGetProperty("minimum", out var min) && value.GetInt32() < min.GetInt32()) ||
                (rule.TryGetProperty("maximum", out var max) && value.GetInt32() > max.GetInt32()))
                return $"{property.Name} is outside the supported range.";
            if (rule.TryGetProperty("format", out _) && !value.TryGetDateTimeOffset(out _))
                return $"{property.Name} must be an ISO 8601 timestamp.";
        }
        return null;
    }
}
