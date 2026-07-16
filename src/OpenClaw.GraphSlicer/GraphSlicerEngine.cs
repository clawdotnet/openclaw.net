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
    private readonly JsonLdFramer _framer;

    public GraphSlicerEngine(JsonLdFramer? framer = null)
        => _framer = framer ?? new JsonLdFramer();

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

            // Apply JSON-LD framing if configured (delegates to dotNetRDF JsonLdProcessor.Frame)
            if (!string.IsNullOrWhiteSpace(profile.FrameJson))
            {
                try
                {
                    jsonLd = _framer.Frame(jsonLd, profile.FrameJson);
                }
                catch (Exception ex) when (ex is JsonException or Newtonsoft.Json.JsonException)
                {
                    return new SliceResult(false, ErrorMessage:
                        $"JSON-LD framing failed: {ex.Message}");
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