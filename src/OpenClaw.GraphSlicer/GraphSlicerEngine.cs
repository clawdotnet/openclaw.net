using System.Text;
using System.Text.Json;
using VDS.RDF;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

public sealed record SliceResult(
    bool Success,
    string? OutputPath = null,
    int TripleCount = 0,
    bool Truncated = false,
    string? ErrorMessage = null);

public sealed class GraphSlicerEngine
{
    public async Task<SliceResult> ExecuteAsync(SliceProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            // 1. Build sources
            var sources = profile.Sources.Select(BuildSource).ToList();
            if (sources.Count == 0)
                return new SliceResult(false, ErrorMessage: "No sources configured.");

            // 2. Execute CONSTRUCT across all sources and merge
            var mergedGraph = new Graph();
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                var graph = await source.ExecuteConstructAsync(profile.Construct, ct).ConfigureAwait(false);
                mergedGraph.Merge(graph);
            }

            if (mergedGraph.IsEmpty)
                return new SliceResult(false, ErrorMessage: "CONSTRUCT produced an empty graph.");

            // 3. Check triple limit
            var tripleCount = mergedGraph.Triples.Count;
            var truncated = tripleCount > profile.Output.MaxTriples;

            // 4. Serialize to JSON-LD
            var outputPath = profile.Output.Path;
            var store = new VDS.RDF.TripleStore();
            store.Add(mergedGraph, mergeIfExists: true);
            using var sw = new System.IO.StringWriter();
            var jsonLdWriter = new VDS.RDF.Writing.JsonLdWriter();
            jsonLdWriter.Save(store, sw);
            var jsonLd = sw.ToString();

            // Apply simple framing if configured (best-effort)
            if (profile.Frame is { ValueKind: JsonValueKind.Object })
            {
                try
                {
                    jsonLd = ApplySimpleFrame(jsonLd, profile.Frame.Value);
                }
                catch
                {
                    // Framing is best-effort; keep unframed output
                }
            }

            // 5. Write output file
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, jsonLd, Encoding.UTF8, ct).ConfigureAwait(false);

            return new SliceResult(true, outputPath, tripleCount, truncated);
        }
        catch (OperationCanceledException)
        {
            return new SliceResult(false, ErrorMessage: "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return new SliceResult(false, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Simple JSON-LD framing: extract and restructure input JSON-LD
    /// using the frame's @context as the output context.
    /// </summary>
    private static string ApplySimpleFrame(string jsonLd, JsonElement frame)
    {
        using var inputDoc = JsonDocument.Parse(jsonLd);
        using var outputStream = new MemoryStream();
        using var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        // Write @context from frame if present, otherwise from input
        if (frame.TryGetProperty("@context", out var ctxEl))
        {
            writer.WritePropertyName("@context");
            ctxEl.WriteTo(writer);
        }
        else if (inputDoc.RootElement.TryGetProperty("@context", out ctxEl))
        {
            writer.WritePropertyName("@context");
            ctxEl.WriteTo(writer);
        }

        // Write @graph with all triples
        writer.WritePropertyName("@graph");
        inputDoc.RootElement.TryGetProperty("@graph", out var graph);
        if (graph.ValueKind == JsonValueKind.Array)
        {
            graph.WriteTo(writer);
        }
        else
        {
            // Compact form — wrap non-@context properties in @graph
            writer.WriteStartArray();
            writer.WriteStartObject();
            foreach (var prop in inputDoc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("@context"))
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(outputStream.ToArray());
    }

    private static ISparqlSource BuildSource(SliceSourceConfig config)
    {
        return config.Kind.ToLowerInvariant() switch
        {
            "remote-endpoint" => new RemoteEndpointSource(config),
            "local-files" => new LocalFilesSource(config),
            _ => throw new ArgumentException($"Unknown source kind '{config.Kind}'.")
        };
    }
}