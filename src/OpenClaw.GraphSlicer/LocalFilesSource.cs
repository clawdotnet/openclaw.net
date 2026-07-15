using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using OpenClaw.Core.Models;

namespace OpenClaw.GraphSlicer;

internal sealed class LocalFilesSource : ISparqlSource
{
    private readonly SliceSourceConfig _config;

    public LocalFilesSource(SliceSourceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.Paths is null || _config.Paths.Count == 0)
            throw new ArgumentException("Paths are required for local-files source.");
    }

    public async Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct)
    {
        var store = new TripleStore();

        await Task.Run(() =>
        {
            foreach (var path in _config.Paths!)
            {
                ct.ThrowIfCancellationRequested();
                var g = new Graph();
                FileLoader.Load(g, path);
                store.Add(g, mergeIfExists: true);
            }
        }, ct);

        var parser = new SparqlQueryParser();
        var query = parser.ParseFromString(constructQuery);
        var processor = new LeviathanQueryProcessor(store);
        var results = processor.ProcessQuery(query) as IGraph;

        return results ?? new Graph();
    }
}