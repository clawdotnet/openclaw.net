using System.Text.Json;

namespace OpenClaw.Core.Plugins;

/// <summary>
/// Validates plugin config against a supported subset of JSON Schema.
/// </summary>
public static class PluginConfigValidator
{
    private static readonly HashSet<string> AllowedKeywords = new(StringComparer.Ordinal)
    {
        "type",
        "properties",
        "required",
        "additionalProperties",
        "items",
        "enum",
        "const",
        "description",
        "title",
        "default",
        "minLength",
        "maxLength",
        "minimum",
        "maximum",
        "minItems",
        "maxItems",
        "pattern",
        "oneOf",
        "anyOf"
    };

    public static IReadOnlyList<PluginCompatibilityDiagnostic> Validate(PluginManifest manifest, JsonElement? config)
    {
        if (manifest.ConfigSchema is null)
            return [];

        var diagnostics = new List<PluginCompatibilityDiagnostic>();
        var cfg = config ?? JsonDocument.Parse("{}").RootElement.Clone();
        ValidateSchemaObject(manifest.ConfigSchema.Value, "$schema", diagnostics);
        if (diagnostics.Count > 0)
            return diagnostics;

        ValidateValue(cfg, manifest.ConfigSchema.Value, "$", diagnostics);
        return diagnostics;
    }

    private static void ValidateSchemaObject(
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(Diagnostic("invalid_schema", $"Schema at '{path}' must be an object.", path));
            return;
        }

        foreach (var prop in schema.EnumerateObject())
        {
            if (!AllowedKeywords.Contains(prop.Name))
            {
                diagnostics.Add(Diagnostic(
                    "unsupported_schema_keyword",
                    $"Schema keyword '{prop.Name}' is not supported at '{path}'.",
                    path));
            }
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in properties.EnumerateObject())
                ValidateSchemaObject(prop.Value, $"{path}.properties.{prop.Name}", diagnostics);
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            ValidateSchemaObject(items, $"{path}.items", diagnostics);

        ValidateSchemaArray(schema, "oneOf", path, diagnostics);
        ValidateSchemaArray(schema, "anyOf", path, diagnostics);
    }

    private static void ValidateValue(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = CountSchemaMatches(value, oneOf, path);
            if (matches != 1)
            {
                diagnostics.Add(Diagnostic(
                    "config_one_of_mismatch",
                    $"Config value at '{path}' must match exactly one schema in 'oneOf'.",
                    path));
            }
            return;
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            var matches = CountSchemaMatches(value, anyOf, path);
            if (matches == 0)
            {
                diagnostics.Add(Diagnostic(
                    "config_any_of_mismatch",
                    $"Config value at '{path}' must match at least one schema in 'anyOf'.",
                    path));
            }
            return;
        }

        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString() ?? "";
            if (!MatchesType(value, type))
            {
                diagnostics.Add(Diagnostic(
                    "config_type_mismatch",
                    $"Config value at '{path}' must be of type '{type}'.",
                    path));
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var matched = enumEl.EnumerateArray().Any(candidate => JsonElementsEqual(candidate, value));
            if (!matched)
            {
                diagnostics.Add(Diagnostic(
                    "config_enum_mismatch",
                    $"Config value at '{path}' must match one of the allowed enum values.",
                    path));
                return;
            }
        }

        if (schema.TryGetProperty("const", out var constEl) && !JsonElementsEqual(constEl, value))
        {
            diagnostics.Add(Diagnostic(
                "config_const_mismatch",
                $"Config value at '{path}' must match the schema const value.",
                path));
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(value, schema, path, diagnostics);
                break;
            case JsonValueKind.Array:
                ValidateArray(value, schema, path, diagnostics);
                break;
            case JsonValueKind.String:
                ValidateString(value.GetString() ?? "", schema, path, diagnostics);
                break;
            case JsonValueKind.Number:
                ValidateNumber(value, schema, path, diagnostics);
                break;
        }
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        var properties = schema.TryGetProperty("properties", out var propEl) && propEl.ValueKind == JsonValueKind.Object
            ? propEl
            : default;
        var required = schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array
            ? reqEl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var allowAdditional = !schema.TryGetProperty("additionalProperties", out var addEl) ||
                              addEl.ValueKind != JsonValueKind.False;

        foreach (var requiredName in required)
        {
            if (!value.TryGetProperty(requiredName, out _))
            {
                diagnostics.Add(Diagnostic(
                    "config_required_missing",
                    $"Config value at '{path}' is missing required property '{requiredName}'.",
                    path));
            }
        }

        foreach (var prop in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(prop.Name, out var propSchema))
            {
                ValidateValue(prop.Value, propSchema, $"{path}.{prop.Name}", diagnostics);
            }
            else if (!allowAdditional)
            {
                diagnostics.Add(Diagnostic(
                    "config_additional_property",
                    $"Config value at '{path}' contains unsupported property '{prop.Name}'.",
                    $"{path}.{prop.Name}"));
            }
        }
    }

    private static void ValidateSchemaArray(
        JsonElement schema,
        string keyword,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (!schema.TryGetProperty(keyword, out var subschemas))
            return;

        if (subschemas.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Diagnostic(
                "invalid_schema",
                $"Schema keyword '{keyword}' at '{path}' must be an array.",
                path));
            return;
        }

        var index = 0;
        foreach (var subSchema in subschemas.EnumerateArray())
        {
            ValidateSchemaObject(subSchema, $"{path}.{keyword}[{index}]", diagnostics);
            index++;
        }
    }

    private static int CountSchemaMatches(JsonElement value, JsonElement schemas, string path)
    {
        var matches = 0;
        foreach (var schema in schemas.EnumerateArray())
        {
            var probeDiagnostics = new List<PluginCompatibilityDiagnostic>();
            ValidateValue(value, schema, path, probeDiagnostics);
            if (probeDiagnostics.Count == 0)
                matches++;
        }

        return matches;
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (schema.TryGetProperty("minItems", out var minItems) &&
            minItems.ValueKind == JsonValueKind.Number &&
            value.GetArrayLength() < minItems.GetInt32())
        {
            diagnostics.Add(Diagnostic(
                "config_min_items",
                $"Config array at '{path}' must contain at least {minItems.GetInt32()} items.",
                path));
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) &&
            maxItems.ValueKind == JsonValueKind.Number &&
            value.GetArrayLength() > maxItems.GetInt32())
        {
            diagnostics.Add(Diagnostic(
                "config_max_items",
                $"Config array at '{path}' must contain at most {maxItems.GetInt32()} items.",
                path));
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(item, items, $"{path}[{index}]", diagnostics);
                index++;
            }
        }
    }

    private static void ValidateString(
        string value,
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        if (schema.TryGetProperty("minLength", out var minLength) &&
            minLength.ValueKind == JsonValueKind.Number &&
            value.Length < minLength.GetInt32())
        {
            diagnostics.Add(Diagnostic(
                "config_min_length",
                $"Config string at '{path}' must be at least {minLength.GetInt32()} characters.",
                path));
        }

        if (schema.TryGetProperty("maxLength", out var maxLength) &&
            maxLength.ValueKind == JsonValueKind.Number &&
            value.Length > maxLength.GetInt32())
        {
            diagnostics.Add(Diagnostic(
                "config_max_length",
                $"Config string at '{path}' must be at most {maxLength.GetInt32()} characters.",
                path));
        }

        if (schema.TryGetProperty("pattern", out var pattern) &&
            pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        value,
                        pattern.GetString() ?? "",
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromSeconds(1)))
                {
                    diagnostics.Add(Diagnostic(
                        "config_pattern_mismatch",
                        $"Config string at '{path}' does not match the required pattern.",
                        path));
                }
            }
            catch (ArgumentException)
            {
                diagnostics.Add(Diagnostic(
                    "invalid_schema_pattern",
                    $"Schema pattern at '{path}' is invalid.",
                    path));
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                diagnostics.Add(Diagnostic(
                    "schema_pattern_timeout",
                    $"Schema pattern at '{path}' timed out during validation.",
                    path));
            }
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<PluginCompatibilityDiagnostic> diagnostics)
    {
        var number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out var minimum) &&
            minimum.ValueKind == JsonValueKind.Number &&
            number < minimum.GetDouble())
        {
            diagnostics.Add(Diagnostic(
                "config_minimum",
                $"Config number at '{path}' must be >= {minimum.GetDouble()}.",
                path));
        }

        if (schema.TryGetProperty("maximum", out var maximum) &&
            maximum.ValueKind == JsonValueKind.Number &&
            number > maximum.GetDouble())
        {
            diagnostics.Add(Diagnostic(
                "config_maximum",
                $"Config number at '{path}' must be <= {maximum.GetDouble()}.",
                path));
        }
    }

    private static bool MatchesType(JsonElement value, string type)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
        => left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();

    private static PluginCompatibilityDiagnostic Diagnostic(string code, string message, string? path)
        => new()
        {
            Code = code,
            Message = message,
            Path = path
        };
}
