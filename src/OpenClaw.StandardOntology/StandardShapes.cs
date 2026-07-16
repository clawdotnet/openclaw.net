using VDS.RDF;

namespace OpenClaw.StandardOntology;

/// <summary>
/// Default SHACL shapes for validating GB/T 48000.3 standard ontologies.
/// These shapes encode the core constraints from section 8.2 of the standard.
/// </summary>
public static class StandardShapes
{
    private const string Prefix = "std";
    private const string Namespace = "http://openclaw.net/ontology/standard#";
    private const string ShNs = "http://www.w3.org/ns/shacl#";
    private const string XsdNs = "http://www.w3.org/2001/XMLSchema#";

    /// <summary>
    /// Build a SHACL shapes graph that validates GB/T 48000.3 compliance.
    /// </summary>
    public static IGraph BuildShapesGraph()
    {
        var g = new Graph();
        g.NamespaceMap.AddNamespace(Prefix, new Uri(Namespace));
        g.NamespaceMap.AddNamespace("sh", new Uri(ShNs));
        g.NamespaceMap.AddNamespace("xsd", new Uri(XsdNs));

        var Sh = ShNs;
        var Std = Namespace;

        // ── Shape: Standard must have standardNumber, issuedDate, standardStatus ──
        var stdShape = BN(g);
        Assert(g, stdShape, U(Sh, "targetClass"), U(Std, "Standard"));

        // standardNumber: exactly 1, type string
        AddPropertyShape(g, stdShape, U(Std, "standardNumber"),
            minCount: 1, maxCount: 1, datatype: U(XsdNs, "string"));

        // issuedDate: at least 1
        AddPropertyShape(g, stdShape, U(Std, "issuedDate"), minCount: 1);

        // effectiveDate: at least 1 (must be >= issuedDate per standard)
        AddPropertyShape(g, stdShape, U(Std, "effectiveDate"), minCount: 1);

        // standardStatus: at least 1
        AddPropertyShape(g, stdShape, U(Std, "standardStatus"), minCount: 1);

        // standardName: at least 1
        AddPropertyShape(g, stdShape, U(Std, "standardName"), minCount: 1);

        // ── Shape: Organization must have partyName ──
        var orgShape = BN(g);
        Assert(g, orgShape, U(Sh, "targetClass"), U(Std, "Organization"));
        AddPropertyShape(g, orgShape, U(Std, "partyName"), minCount: 1);

        // ── Shape: Person must have partyName ──
        var persShape = BN(g);
        Assert(g, persShape, U(Sh, "targetClass"), U(Std, "Person"));
        AddPropertyShape(g, persShape, U(Std, "partyName"), minCount: 1);

        // ── Shape: Term must have termName ──
        var termShape = BN(g);
        Assert(g, termShape, U(Sh, "targetClass"), U(Std, "Term"));
        AddPropertyShape(g, termShape, U(Std, "termName"), minCount: 1);

        // ── Shape: DevelopmentStage must have stageCode ──
        var dsShape = BN(g);
        Assert(g, dsShape, U(Sh, "targetClass"), U(Std, "DevelopmentStage"));
        AddPropertyShape(g, dsShape, U(Std, "stageCode"), minCount: 1, maxCount: 1);

        return g;
    }

    private static void AddPropertyShape(IGraph g, INode shape, Uri path,
        int minCount = 0, int? maxCount = null, Uri? datatype = null)
    {
        var prop = BN(g);
        Assert(g, shape, U(ShNs, "property"), prop);
        Assert(g, prop, U(ShNs, "path"), path);
        if (minCount > 0)
            Assert(g, prop, U(ShNs, "minCount"), g.CreateLiteralNode(
                minCount.ToString(), new Uri(XsdNs + "integer")));
        if (maxCount.HasValue)
            Assert(g, prop, U(ShNs, "maxCount"), g.CreateLiteralNode(
                maxCount.Value.ToString(), new Uri(XsdNs + "integer")));
        if (datatype != null)
            Assert(g, prop, U(ShNs, "datatype"), datatype);
    }

    private static INode BN(IGraph g) => g.CreateBlankNode();
    private static Uri U(string ns, string local) => new(ns + local);
    private static INode UN(IGraph g, string ns, string local) => g.CreateUriNode(new Uri(ns + local));

    private static void Assert(IGraph g, INode s, Uri p, INode o)
        => g.Assert(new Triple(s, g.CreateUriNode(p), o));
    private static void Assert(IGraph g, INode s, Uri p, Uri o)
        => g.Assert(new Triple(s, g.CreateUriNode(p), g.CreateUriNode(o)));
}