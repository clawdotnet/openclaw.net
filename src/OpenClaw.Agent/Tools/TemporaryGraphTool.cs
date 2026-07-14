using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Loads a temporary graph payload from JSON/JSON-LD files or markdown code blocks.
/// Designed for MetaSkill DAG tool_call steps that need per-run graph slices.
/// </summary>
public sealed class LoadTemporaryGraphTool : ITool
{
    private static readonly Regex FencedCodeRegex = new(
        "```(?<lang>[a-zA-Z0-9_-]*)\\s*\\r?\\n(?<code>[\\s\\S]*?)```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ToolingConfig _config;

    public LoadTemporaryGraphTool(ToolingConfig config)
        => _config = config;

    public string Name => "load_temporary_graph";

    public string Description => "Load a temporary graph payload from JSON/JSON-LD or markdown code blocks for MetaSkill DAG consumption.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Absolute or relative file path"
            },
            "format": {
              "type": "string",
              "description": "Input format hint: auto|json|jsonld|markdown",
              "default": "auto"
            },
            "code_block_language": {
              "type": "string",
              "description": "When format=markdown, pick first fenced block by language (for example: json, jsonld, turtle, sparql)"
            },
            "code_block_index": {
              "type": "integer",
              "description": "When format=markdown and no language filter is used, pick fenced block index (0-based)",
              "default": 0
            },
            "max_chars": {
              "type": "integer",
              "description": "Maximum payload characters returned in payload_text",
              "default": 200000
            }
          },
          "required": ["path"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (!args.RootElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
            return SerializeError("invalid_arguments", "'path' is required.");

        var path = pathElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return SerializeError("invalid_arguments", "'path' is required.");

        var formatHint = ReadString(args.RootElement, "format") ?? "auto";
        var blockLanguage = ReadString(args.RootElement, "code_block_language");
        var blockIndex = ReadInt(args.RootElement, "code_block_index") ?? 0;
        var maxChars = Math.Clamp(ReadInt(args.RootElement, "max_chars") ?? 200_000, 256, 2_000_000);

        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);
        if (!ToolPathPolicy.IsReadAllowed(_config, resolvedPath))
            return SerializeError("read_access_denied", $"Read access denied for path: {path}");

        if (!File.Exists(resolvedPath))
            return SerializeError("file_not_found", $"File not found: {path}");

        var raw = await File.ReadAllTextAsync(resolvedPath, ct);
        var format = ResolveFormat(formatHint, resolvedPath);

        string payloadText;
        string payloadFormat;

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryExtractMarkdownCode(raw, blockLanguage, blockIndex, out payloadText, out payloadFormat))
                return SerializeError("code_block_not_found", "No matching fenced code block found in markdown input.");
        }
        else
        {
            payloadText = raw;
            payloadFormat = format;
        }

        payloadText = payloadText.Trim();
        var truncatedPayload = payloadText.Length > maxChars
            ? payloadText[..maxChars]
            : payloadText;

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("status", "ok");
        writer.WriteString("source", path);
        writer.WriteString("input_format", format);
        writer.WriteString("payload_format", payloadFormat);
        writer.WriteNumber("payload_length", payloadText.Length);
        writer.WriteBoolean("truncated", payloadText.Length > maxChars);
        writer.WriteString("payload_text", truncatedPayload);

        if (LooksLikeJson(payloadText) && TryParseJson(payloadText, out var normalizedJson))
            writer.WriteString("payload_json", normalizedJson);

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return value.TryGetInt32(out var result) ? result : null;
    }

    private static string ResolveFormat(string formatHint, string path)
    {
        if (!string.Equals(formatHint, "auto", StringComparison.OrdinalIgnoreCase))
            return formatHint.Trim().ToLowerInvariant();

        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".md" => "markdown",
            ".markdown" => "markdown",
            ".jsonld" => "jsonld",
            _ => "json"
        };
    }

    private static bool TryExtractMarkdownCode(string markdown, string? blockLanguage, int blockIndex, out string payloadText, out string payloadFormat)
    {
        payloadText = string.Empty;
        payloadFormat = "text";

        var matches = FencedCodeRegex.Matches(markdown);
        if (matches.Count == 0)
            return false;

        var candidates = new List<Match>(matches.Count);
        foreach (Match match in matches)
        {
            var language = match.Groups["lang"].Value.Trim();
            if (string.IsNullOrWhiteSpace(blockLanguage) ||
                string.Equals(language, blockLanguage, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(match);
            }
        }

        if (candidates.Count == 0 || blockIndex < 0 || blockIndex >= candidates.Count)
            return false;

        var selected = candidates[blockIndex];
        payloadText = selected.Groups["code"].Value;
        var languageLabel = selected.Groups["lang"].Value.Trim().ToLowerInvariant();
        payloadFormat = string.IsNullOrWhiteSpace(languageLabel) ? "text" : languageLabel;
        return true;
    }

    private static bool LooksLikeJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var trimmed = payload.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool TryParseJson(string payload, out string normalizedJson)
    {
        normalizedJson = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            normalizedJson = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SerializeError(string code, string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("status", "error");
        writer.WriteString("errorCode", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
