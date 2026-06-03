using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonConverter(typeof(ReadOnlyStringSetJsonConverter))]
    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [JsonConverter(typeof(ReadOnlyStringSetJsonConverter))]
    public IReadOnlySet<string> ApprovalRequiredTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override bool HandleNull => true;

    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected a JSON array for {typeToConvert}.");

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return values;
            if (reader.TokenType == JsonTokenType.Null)
                continue;
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected a string value in the tool set.");

            values.Add(reader.GetString()!);
        }

        throw new JsonException("Unexpected end of JSON while reading the tool set.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
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
