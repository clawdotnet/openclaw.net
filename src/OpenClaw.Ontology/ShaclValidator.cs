using VDS.RDF;
using VDS.RDF.Shacl;
using VDS.RDF.Shacl.Validation;

namespace OpenClaw.Ontology;

/// <summary>
/// Wraps dotNetRDF's SHACL processor for validating RDF data graphs
/// against SHACL shapes graphs.
/// </summary>
public sealed class ShaclValidator
{
    /// <summary>
    /// Validate an RDF data graph against a SHACL shapes graph.
    /// </summary>
    /// <param name="dataGraph">The ontology or instance data to validate.</param>
    /// <param name="shapesGraph">The SHACL shapes defining constraints.</param>
    /// <returns>A <see cref="ShaclReport"/> with conformance status and detailed results.</returns>
    public ShaclReport Validate(IGraph dataGraph, IGraph shapesGraph)
    {
        ArgumentNullException.ThrowIfNull(dataGraph);
        ArgumentNullException.ThrowIfNull(shapesGraph);

        var shapes = new ShapesGraph(shapesGraph);
        var report = shapes.Validate(dataGraph);

        return MapToShaclReport(report);
    }

    /// <summary>
    /// Quick conformance check — returns true if the data graph
    /// satisfies all constraints in the shapes graph.
    /// </summary>
    public bool Conforms(IGraph dataGraph, IGraph shapesGraph)
    {
        var shapes = new ShapesGraph(shapesGraph);
        return shapes.Conforms(dataGraph);
    }

    private static ShaclReport MapToShaclReport(Report dotNetReport)
    {
        var results = new List<ShaclResult>();
        foreach (var r in dotNetReport.Results)
        {
            results.Add(new ShaclResult(
                FocusNode: r.FocusNode?.ToString() ?? "(anonymous)",
                Severity: MapSeverity(r.Severity),
                Message: r.Message?.Value ?? "",
                SourceShape: r.SourceShape?.ToString() ?? "",
                SourceConstraint: r.SourceConstraintComponent?.ToString() ?? "",
                ResultPath: r.ResultPath?.ToString()
            ));
        }

        return new ShaclReport(
            Conforms: dotNetReport.Conforms,
            ResultCount: results.Count,
            Results: results
        );
    }

    private static ShaclSeverity MapSeverity(INode? node)
    {
        if (node is null)
            return ShaclSeverity.Info;

        var iri = node.ToString();
        if (iri.Contains("Violation"))
            return ShaclSeverity.Violation;
        if (iri.Contains("Warning"))
            return ShaclSeverity.Warning;
        if (iri.Contains("Info"))
            return ShaclSeverity.Info;

        return ShaclSeverity.Violation;
    }
}

/// <summary>
/// SHACL validation report.
/// </summary>
/// <param name="Conforms">True if all constraints are satisfied.</param>
/// <param name="ResultCount">Number of validation results.</param>
/// <param name="Results">Individual validation results.</param>
public sealed record ShaclReport(
    bool Conforms,
    int ResultCount,
    IReadOnlyList<ShaclResult> Results);

/// <summary>
/// A single SHACL validation result.
/// </summary>
/// <param name="FocusNode">The node that was validated.</param>
/// <param name="Severity">Severity level of the result.</param>
/// <param name="Message">Human-readable validation message.</param>
/// <param name="SourceShape">The shape that triggered this result.</param>
/// <param name="SourceConstraint">The constraint component.</param>
/// <param name="ResultPath">Optional property path.</param>
public sealed record ShaclResult(
    string FocusNode,
    ShaclSeverity Severity,
    string Message,
    string SourceShape,
    string SourceConstraint,
    string? ResultPath);

/// <summary>
/// SHACL severity levels.
/// </summary>
public enum ShaclSeverity
{
    Info,
    Warning,
    Violation
}
