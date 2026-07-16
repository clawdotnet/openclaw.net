using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Shacl;

namespace OpenClaw.Ontology;

/// <summary>
/// Validates RDF data files against SHACL shapes files.
/// Designed for MetaSkill DAG tool_call steps:
///
///   - id: validate_ontology
///     kind: tool_call
///     tool: ontology_validate
///     with:
///       data: "./tmp/my-data.ttl"
///       shapes: "./tmp/my-shapes.ttl"
/// </summary>
public sealed class OntologyValidateTool : ITool
{
    public string Name => "ontology_validate";

    public string Description =>
        "Validate RDF ontology or instance data against SHACL shapes. " +
        "Supports .ttl, .rdf, .jsonld, .nt files.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "data": {
              "type": "string",
              "description": "Path to the RDF data file to validate"
            },
            "shapes": {
              "type": "string",
              "description": "Path to SHACL shapes file (.ttl, .rdf, etc.)"
            }
          },
          "required": ["data", "shapes"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        if (!TryReadRequired(args.RootElement, "data", out var dataPath, out var err))
            return err;
        if (!TryReadRequired(args.RootElement, "shapes", out var shapesPath, out err))
            return err;

        if (!File.Exists(dataPath))
            return Error("file_not_found", $"Data file not found: {dataPath}");
        if (!File.Exists(shapesPath))
            return Error("file_not_found", $"Shapes file not found: {shapesPath}");

        try
        {
            var dataGraph = new Graph();
            var shapesGraph = new Graph();
            await Task.Run(() =>
            {
                FileLoader.Load(dataGraph, dataPath);
                FileLoader.Load(shapesGraph, shapesPath);
            }, ct);

            var shaclShapes = new ShapesGraph(shapesGraph);
            var report = await Task.Run(() => shaclShapes.Validate(dataGraph), ct);

            return SerializeReport(report, dataPath, shapesPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error("validation_error", ex.Message);
        }
    }

    private static string SerializeReport(VDS.RDF.Shacl.Validation.Report report,
        string dataPath, string shapesPath)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteBoolean("conforms", report.Conforms);
        writer.WriteNumber("result_count", report.Results.Count);
        writer.WriteString("data", dataPath);
        writer.WriteString("shapes", shapesPath);

        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var r in report.Results)
        {
            writer.WriteStartObject();
            writer.WriteString("focus_node", r.FocusNode?.ToString() ?? "(anonymous)");
            writer.WriteString("message", r.Message?.Value ?? "");
            if (r.ResultPath != null)
                writer.WriteString("path", r.ResultPath.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryReadRequired(JsonElement root, string name,
        out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            error = Error("invalid_arguments", $"'{name}' is required.");
            return false;
        }

        value = el.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = Error("invalid_arguments", $"'{name}' is required.");
            return false;
        }

        return true;
    }

    private static string Error(string code, string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("status", "error");
        writer.WriteString("error_code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}