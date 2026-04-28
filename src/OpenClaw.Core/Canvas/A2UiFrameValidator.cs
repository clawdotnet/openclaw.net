using System.Text;
using System.Text.Json;

namespace OpenClaw.Core.Canvas;

public sealed record A2UiValidationResult(bool IsValid, string? Error, int FrameCount)
{
    public static A2UiValidationResult Valid(int frameCount) => new(true, null, frameCount);
    public static A2UiValidationResult Invalid(string error, int frameCount = 0) => new(false, error, frameCount);
}

public static class A2UiFrameValidator
{
    public const string ContentTypeV08 = "application/x-a2ui+jsonl;version=0.8";

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "markdown",
        "card",
        "button",
        "input",
        "select",
        "checklist",
        "table",
        "image",
        "progress",
        "chart"
    };

    public static A2UiValidationResult ValidateJsonl(string? frames, int maxFrames, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(frames))
            return A2UiValidationResult.Invalid("A2UI frames are required.");

        if (Encoding.UTF8.GetByteCount(frames) > Math.Max(1, maxBytes))
            return A2UiValidationResult.Invalid($"A2UI frame payload exceeds {maxBytes} bytes.");

        var count = 0;
        foreach (var rawLine in frames.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            count++;
            if (count > Math.Max(1, maxFrames))
                return A2UiValidationResult.Invalid($"A2UI push exceeds {maxFrames} frames.", count);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                return A2UiValidationResult.Invalid($"Frame {count} is not valid JSON: {ex.Message}", count);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return A2UiValidationResult.Invalid($"Frame {count} must be a JSON object.", count);

                if (HasString(root, "command", "createSurface") || HasString(root, "type", "createSurface"))
                    return A2UiValidationResult.Invalid("A2UI v0.9 createSurface is not supported; use v0.8 JSONL frames.", count);

                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    return A2UiValidationResult.Invalid($"Frame {count} is missing string property 'type'.", count);

                var type = typeProp.GetString();
                if (string.IsNullOrWhiteSpace(type) || !SupportedTypes.Contains(type))
                    return A2UiValidationResult.Invalid($"Frame {count} has unsupported A2UI type '{type}'.", count);

                if (!HasNonEmptyString(root, "id"))
                    return A2UiValidationResult.Invalid($"Frame {count} is missing string property 'id'.", count);

                var typeError = ValidateByType(root, type);
                if (typeError is not null)
                    return A2UiValidationResult.Invalid($"Frame {count} {typeError}", count);
            }
        }

        return count == 0
            ? A2UiValidationResult.Invalid("A2UI frames are required.")
            : A2UiValidationResult.Valid(count);
    }

    private static string? ValidateByType(JsonElement root, string type)
        => type.ToLowerInvariant() switch
        {
            "text" or "markdown" => HasNonEmptyString(root, "text") ? null : "is missing string property 'text'.",
            "card" => HasNonEmptyString(root, "title") || HasNonEmptyString(root, "body") ? null : "requires 'title' or 'body'.",
            "button" => HasNonEmptyString(root, "label") ? null : "is missing string property 'label'.",
            "input" => null,
            "select" or "checklist" => HasArray(root, "options") ? null : "is missing array property 'options'.",
            "table" => HasArray(root, "columns") && HasArray(root, "rows") ? null : "requires array properties 'columns' and 'rows'.",
            "image" => HasNonEmptyString(root, "url") || HasNonEmptyString(root, "src") ? null : "requires 'url' or 'src'.",
            "progress" => HasNumber(root, "value") ? null : "is missing numeric property 'value'.",
            "chart" => HasProperty(root, "data") ? null : "is missing property 'data'.",
            _ => "has unsupported type."
        };

    private static bool HasProperty(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out _);

    private static bool HasArray(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array;

    private static bool HasNumber(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number;

    private static bool HasNonEmptyString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) &&
           prop.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(prop.GetString());

    private static bool HasString(JsonElement root, string propertyName, string expected)
        => root.TryGetProperty(propertyName, out var prop) &&
           prop.ValueKind == JsonValueKind.String &&
           string.Equals(prop.GetString(), expected, StringComparison.OrdinalIgnoreCase);
}
