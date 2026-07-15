using VDS.RDF;

namespace OpenClaw.GraphSlicer;

internal interface ISparqlSource
{
    Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct);
}
