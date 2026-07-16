using VDS.RDF;
using VDS.RDF.Parsing;

namespace OpenClaw.StandardOntology;

/// <summary>
/// Traces version chains in standard ontology data using the
/// <c>std:replaces</c> / <c>std:hasVersion</c> / <c>std:versionNumber</c> properties.
/// </summary>
public static class VersionTracer
{
    /// <summary>
    /// Build the version lineage for a standard, following replaces chains
    /// from newest to oldest.
    /// </summary>
    /// <param name="graph">The RDF graph containing standard instances.</param>
    /// <param name="standardNode">The starting standard node IRI.</param>
    /// <returns>Ordered list of version entries (most recent first).</returns>
    public static IReadOnlyList<VersionEntry> TraceReplacesChain(IGraph graph, string standardNode)
    {
        var stdNode = graph.GetUriNode(new Uri(standardNode));
        if (stdNode is null)
            return [];

        var replaceProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#replaces"));
        var versionProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#versionNumber"));
        var nameProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#standardName"));
        var numberProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#standardNumber"));

        var entries = new List<VersionEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var current = stdNode;
        while (current != null && visited.Add(current.Uri.ToString()))
        {
            entries.Add(ExtractEntry(graph, current, nameProp, numberProp, versionProp));

            // Follow replaces → next older version
            current = graph.GetTriplesWithSubjectPredicate(current, replaceProp)
                .Select(t => t.Object)
                .OfType<IUriNode>()
                .FirstOrDefault();
        }

        return entries;
    }

    /// <summary>
    /// Get all versions of a standard via the <c>std:hasVersion</c> property.
    /// </summary>
    public static IReadOnlyList<VersionEntry> GetVersions(IGraph graph, string standardNode)
    {
        var stdNode = graph.GetUriNode(new Uri(standardNode));
        if (stdNode is null)
            return [];

        var hasVersionProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#hasVersion"));
        var versionNumProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#versionNumber"));
        var statusProp = graph.CreateUriNode(new Uri("http://openclaw.net/ontology/standard#versionStatus"));

        return graph.GetTriplesWithSubjectPredicate(stdNode, hasVersionProp)
            .Select(t => t.Object)
            .OfType<IUriNode>()
            .Select(vn => new VersionEntry(
                StandardNumber: "",
                StandardName: "",
                VersionNumber: GetLiteralValue(graph, vn, versionNumProp),
                Status: GetLiteralValue(graph, vn, statusProp),
                Iri: vn.Uri.ToString()
            ))
            .OrderByDescending(e => e.VersionNumber)
            .ToList();
    }

    /// <summary>
    /// Compute the difference between two version entries.
    /// </summary>
    public static VersionDiff Diff(IGraph graph, string oldIri, string newIri)
    {
        var oldNode = graph.GetUriNode(new Uri(oldIri));
        var newNode = graph.GetUriNode(new Uri(newIri));
        if (oldNode is null || newNode is null)
            return new VersionDiff(oldIri, newIri, [], [], []);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();

        // Compare outgoing properties
        foreach (var oldTriple in graph.GetTriplesWithSubject(oldNode))
        {
            var prop = oldTriple.Predicate.ToString();
            var matching = graph.GetTriplesWithSubjectPredicate(newNode, oldTriple.Predicate);
            if (!matching.Any())
                removed.Add(prop);
            else if (!matching.Any(t => t.Object.ToString() == oldTriple.Object.ToString()))
                changed.Add(prop);
        }

        foreach (var newTriple in graph.GetTriplesWithSubject(newNode))
        {
            var prop = newTriple.Predicate.ToString();
            if (!graph.GetTriplesWithSubjectPredicate(oldNode, newTriple.Predicate).Any())
                added.Add(prop);
        }

        return new VersionDiff(oldIri, newIri,
            added.Distinct().ToList(),
            removed.Distinct().ToList(),
            changed.Distinct().ToList());
    }

    /// <summary>
    /// Load an RDF graph from a file path.
    /// </summary>
    public static IGraph LoadGraph(string path)
    {
        var g = new Graph();
        FileLoader.Load(g, path);
        return g;
    }

    private static VersionEntry ExtractEntry(IGraph graph, IUriNode node,
        IUriNode nameProp, IUriNode numberProp, IUriNode versionProp)
    {
        return new VersionEntry(
            StandardNumber: GetLiteralValue(graph, node, numberProp),
            StandardName: GetLiteralValue(graph, node, nameProp),
            VersionNumber: GetLiteralValue(graph, node, versionProp),
            Status: null,
            Iri: node.Uri.ToString()
        );
    }

    private static string GetLiteralValue(IGraph graph, IUriNode subject, IUriNode predicate)
    {
        return graph.GetTriplesWithSubjectPredicate(subject, predicate)
            .Select(t => t.Object)
            .OfType<ILiteralNode>()
            .Select(n => n.Value)
            .FirstOrDefault() ?? "";
    }
}

/// <summary>
/// A single version entry in a standard's history.
/// </summary>
public sealed record VersionEntry(
    string StandardNumber,
    string StandardName,
    string VersionNumber,
    string? Status,
    string Iri
);

/// <summary>
/// Difference between two versions of a standard.
/// </summary>
public sealed record VersionDiff(
    string OldIri,
    string NewIri,
    IReadOnlyList<string> AddedProperties,
    IReadOnlyList<string> RemovedProperties,
    IReadOnlyList<string> ChangedProperties
);