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

        // ── Shape: Standard must carry the mandatory C.1–C.8 data properties ──
        var stdShape = BN(g);
        Assert(g, stdShape, U(Sh, "targetClass"), U(Std, "Standard"));

        // standardNumber: exactly 1, type string (C.6, also the owl:hasKey)
        AddPropertyShape(g, stdShape, U(Std, "standardNumber"),
            minCount: 1, maxCount: 1, datatype: U(XsdNs, "string"));
        // documentName: at least 1 (C.5)
        AddPropertyShape(g, stdShape, U(Std, "documentName"), minCount: 1);
        // issuedDate: at least 1 (C.7)
        AddPropertyShape(g, stdShape, U(Std, "issuedDate"), minCount: 1);
        // effectiveDate: at least 1 (C.8; must be >= issuedDate per 8.2 b)2)
        AddPropertyShape(g, stdShape, U(Std, "effectiveDate"), minCount: 1);
        // status: at least 1 (C.3)
        AddPropertyShape(g, stdShape, U(Std, "status"), minCount: 1);
        // languageVersion: at least 1 (C.2)
        AddPropertyShape(g, stdShape, U(Std, "languageVersion"), minCount: 1);
        // purpose: at least 1 (C.1)
        AddPropertyShape(g, stdShape, U(Std, "purpose"), minCount: 1);
        // constraintType: at least 1 (C.4)
        AddPropertyShape(g, stdShape, U(Std, "constraintType"), minCount: 1);

        // ── Shape: Organization must have orgName (C.11) ──
        var orgShape = BN(g);
        Assert(g, orgShape, U(Sh, "targetClass"), U(Std, "Organization"));
        AddPropertyShape(g, orgShape, U(Std, "orgName"), minCount: 1);

        // ── Shape: Individual must have personName (C.14) ──
        var indShape = BN(g);
        Assert(g, indShape, U(Sh, "targetClass"), U(Std, "Individual"));
        AddPropertyShape(g, indShape, U(Std, "personName"), minCount: 1);

        // ── Shape: Clause must have clauseNumber (C.26) ──
        var clauseShape = BN(g);
        Assert(g, clauseShape, U(Sh, "targetClass"), U(Std, "Clause"));
        AddPropertyShape(g, clauseShape, U(Std, "clauseNumber"), minCount: 1);

        // ── Shape: ExternalResource must have fileType (C.42) ──
        var extShape = BN(g);
        Assert(g, extShape, U(Sh, "targetClass"), U(Std, "ExternalResource"));
        AddPropertyShape(g, extShape, U(Std, "fileType"), minCount: 1);

        // ── Shape: StandardizationProcess must have stageCode (C.45) ──
        var procShape = BN(g);
        Assert(g, procShape, U(Sh, "targetClass"), U(Std, "StandardizationProcess"));
        AddPropertyShape(g, procShape, U(Std, "stageCode"), minCount: 1, maxCount: 1);

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