using VDS.RDF;
using VDS.RDF.Writing;

namespace OpenClaw.Ontology;

/// <summary>
/// Fluent builder for OWL 2 ontologies using dotNetRDF's Graph API.
/// Constructs RDF triples with OWL/RDF/RDFS vocabulary.
/// </summary>
public sealed class OntologyBuilder
{
    private readonly Graph _graph;
    private readonly string _baseNs;

    // Well-known vocabulary nodes (lazily created)
    private IUriNode? _owlClass, _owlObjectProperty, _owlDatatypeProperty;
    private IUriNode? _owlHasKey;
    private IUriNode? _owlFunctionalProperty, _owlInverseFunctionalProperty;
    private IUriNode? _owlSymmetricProperty, _owlTransitiveProperty;
    private IUriNode? _owlEquivalentClass, _owlDisjointWith;
    private IUriNode? _rdfType, _rdfProperty, _rdfFirst, _rdfRest;
    private IUriNode? _rdfsLabel, _rdfsComment, _rdfsDomain, _rdfsRange;
    private IUriNode? _rdfsSubClassOf, _rdfsSubPropertyOf;

    public OntologyBuilder(string baseNamespace)
    {
        _baseNs = baseNamespace.TrimEnd('#', '/');
        _graph = new Graph { BaseUri = new Uri(_baseNs + "#") };

        // Register standard prefixes
        _graph.NamespaceMap.AddNamespace("owl", new Uri(Namespaces.Owl));
        _graph.NamespaceMap.AddNamespace("rdf", new Uri(Namespaces.Rdf));
        _graph.NamespaceMap.AddNamespace("rdfs", new Uri(Namespaces.Rdfs));
        _graph.NamespaceMap.AddNamespace("xsd", new Uri(Namespaces.Xsd));
    }

    /// <summary>
    /// Register a custom namespace prefix for the ontology.
    /// </summary>
    public OntologyBuilder WithPrefix(string prefix, string namespaceUri)
    {
        _graph.NamespaceMap.AddNamespace(prefix, new Uri(namespaceUri));
        return this;
    }

    /// <summary>
    /// Add an ontology header with metadata.
    /// </summary>
    public OntologyBuilder WithHeader(string ontologyIri, string? label = null, string? comment = null)
    {
        var ontNode = Node(ontologyIri);
        _graph.Assert(ontNode, RdfType(), Owl("Ontology"));
        if (label != null)
            _graph.Assert(ontNode, RdfsLabel(), Literal(label));
        if (comment != null)
            _graph.Assert(ontNode, RdfsComment(), Literal(comment));
        return this;
    }

    // ── Classes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Declare an OWL class.
    /// </summary>
    public OntologyBuilder DeclareClass(
        string iri, string label, string? comment = null,
        string[]? subClassOf = null, string[]? disjointWith = null,
        string[]? equivalentClasses = null,
        string[]? hasKey = null)
    {
        var classNode = Node(iri);
        _graph.Assert(classNode, RdfType(), OwlClass());

        _graph.Assert(classNode, RdfsLabel(), Literal(label));
        if (comment != null)
            _graph.Assert(classNode, RdfsComment(), Literal(comment));

        if (subClassOf != null)
            foreach (var sc in subClassOf)
                _graph.Assert(classNode, RdfsSubClassOf(), Node(sc));

        if (disjointWith != null)
            foreach (var dw in disjointWith)
                _graph.Assert(classNode, OwlDisjointWith(), Node(dw));

        if (equivalentClasses != null)
            foreach (var ec in equivalentClasses)
                _graph.Assert(classNode, OwlEquivalentClass(), Node(ec));

        if (hasKey != null && hasKey.Length > 0)
        {
            var listNode = _graph.CreateBlankNode();
            _graph.Assert(classNode, OwlHasKey(), listNode);
            for (int i = 0; i < hasKey.Length; i++)
            {
                _graph.Assert(listNode, RdfFirst(), Node(hasKey[i]));
                if (i == hasKey.Length - 1)
                {
                    _graph.Assert(listNode, RdfRest(), OwlNil());
                }
                else
                {
                    var rest = _graph.CreateBlankNode();
                    _graph.Assert(listNode, RdfRest(), rest);
                    listNode = rest;
                }
            }
        }

        return this;
    }

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>
    /// Declare an OWL ObjectProperty (linking two individuals/classes).
    /// </summary>
    public OntologyBuilder DeclareObjectProperty(
        string iri, string label, string? comment = null,
        string? domain = null, string? range = null,
        string[]? subPropertyOf = null,
        bool functional = false, bool inverseFunctional = false,
        bool symmetric = false, bool transitive = false)
    {
        return DeclareProperty(iri, OwlObjectProperty(), label, comment,
            domain, range, subPropertyOf, functional, inverseFunctional,
            symmetric, transitive);
    }

    /// <summary>
    /// Declare an OWL DatatypeProperty (linking individual to literal value).
    /// </summary>
    public OntologyBuilder DeclareDatatypeProperty(
        string iri, string label, string? comment = null,
        string? domain = null, string? range = null,
        string[]? subPropertyOf = null,
        bool functional = false)
    {
        return DeclareProperty(iri, OwlDatatypeProperty(), label, comment,
            domain, range, subPropertyOf, functional, false, false, false);
    }

    private OntologyBuilder DeclareProperty(
        string iri, IUriNode propertyType, string label, string? comment,
        string? domain, string? range, string[]? subPropertyOf,
        bool functional, bool inverseFunctional, bool symmetric, bool transitive)
    {
        var propNode = Node(iri);
        _graph.Assert(propNode, RdfType(), propertyType);
        _graph.Assert(propNode, RdfType(), RdfProperty());
        _graph.Assert(propNode, RdfsLabel(), Literal(label));
        if (comment != null)
            _graph.Assert(propNode, RdfsComment(), Literal(comment));
        if (domain != null)
            _graph.Assert(propNode, RdfsDomain(), Node(domain));
        if (range != null)
            _graph.Assert(propNode, RdfsRange(), Node(range));
        if (subPropertyOf != null)
            foreach (var sp in subPropertyOf)
                _graph.Assert(propNode, RdfsSubPropertyOf(), Node(sp));

        if (functional)
            _graph.Assert(propNode, RdfType(), OwlFunctionalProperty());
        if (inverseFunctional)
            _graph.Assert(propNode, RdfType(), OwlInverseFunctionalProperty());
        if (symmetric)
            _graph.Assert(propNode, RdfType(), OwlSymmetricProperty());
        if (transitive)
            _graph.Assert(propNode, RdfType(), OwlTransitiveProperty());

        return this;
    }

    // ── Axioms ───────────────────────────────────────────────────────────

    /// <summary>
    /// Assert that two classes are disjoint.
    /// </summary>
    public OntologyBuilder AssertDisjointClasses(string classA, string classB)
    {
        _graph.Assert(Node(classA), OwlDisjointWith(), Node(classB));
        return this;
    }

    /// <summary>
    /// Assert a subclass relationship.
    /// </summary>
    public OntologyBuilder AssertSubClassOf(string subClass, string superClass)
    {
        _graph.Assert(Node(subClass), RdfsSubClassOf(), Node(superClass));
        return this;
    }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Get the underlying dotNetRDF Graph.
    /// </summary>
    public IGraph Build() => _graph;

    /// <summary>
    /// Serialize the ontology to a string in the requested format.
    /// </summary>
    public string Serialize(OntologyOutputFormat format)
    {
        using var sw = new System.IO.StringWriter();
        SaveGraph(_graph, sw, format);
        return sw.ToString();
    }

    /// <summary>
    /// Write the ontology to a file in the requested format.
    /// </summary>
    public void WriteToFile(string path, OntologyOutputFormat format)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var fileWriter = File.CreateText(path);
        SaveGraph(_graph, fileWriter, format);
    }

    private static void SaveGraph(IGraph graph, TextWriter writer, OntologyOutputFormat format)
    {
        switch (format)
        {
            case OntologyOutputFormat.Turtle:
                new CompressingTurtleWriter().Save(graph, writer);
                break;
            case OntologyOutputFormat.JsonLd:
                var store = new TripleStore();
                store.Add(graph, mergeIfExists: true);
                new JsonLdWriter().Save(store, writer);
                break;
            case OntologyOutputFormat.RdfXml:
                new RdfXmlWriter().Save(graph, writer);
                break;
            default:
                new CompressingTurtleWriter().Save(graph, writer);
                break;
        }
    }

    // ── Node factories ───────────────────────────────────────────────────

    private IUriNode Node(string iri)
    {
        // If it's already a full IRI, create directly
        if (iri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            iri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            iri.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
            return _graph.CreateUriNode(new Uri(iri));

        // Compact IRI: prefix:local → resolve via namespace map
        var colonIndex = iri.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = iri[..colonIndex];
            var local = iri[(colonIndex + 1)..];
            return _graph.CreateUriNode(prefix + ":" + local);
        }

        // Plain local name → use base namespace
        return _graph.CreateUriNode(new Uri(_baseNs + "#" + iri));
    }

    private ILiteralNode Literal(string value, string lang = "zh") =>
        _graph.CreateLiteralNode(value, lang);

    // ── Vocabulary node factories (lazy) ──────────────────────────────────

    private IUriNode OwlClass() => _owlClass ??= Owl("Class");
    private IUriNode OwlObjectProperty() => _owlObjectProperty ??= Owl("ObjectProperty");
    private IUriNode OwlDatatypeProperty() => _owlDatatypeProperty ??= Owl("DatatypeProperty");
    private IUriNode OwlHasKey() => _owlHasKey ??= Owl("hasKey");
    private IUriNode OwlFunctionalProperty() => _owlFunctionalProperty ??= Owl("FunctionalProperty");
    private IUriNode OwlInverseFunctionalProperty() => _owlInverseFunctionalProperty ??= Owl("InverseFunctionalProperty");
    private IUriNode OwlSymmetricProperty() => _owlSymmetricProperty ??= Owl("SymmetricProperty");
    private IUriNode OwlTransitiveProperty() => _owlTransitiveProperty ??= Owl("TransitiveProperty");
    private IUriNode OwlEquivalentClass() => _owlEquivalentClass ??= Owl("equivalentClass");
    private IUriNode OwlDisjointWith() => _owlDisjointWith ??= Owl("disjointWith");

    private IUriNode RdfType() => _rdfType ??= Rdf("type");
    private IUriNode RdfProperty() => _rdfProperty ??= Rdf("Property");
    private IUriNode RdfFirst() => _rdfFirst ??= Rdf("first");
    private IUriNode RdfRest() => _rdfRest ??= Rdf("rest");

    private IUriNode RdfsLabel() => _rdfsLabel ??= Rdfs("label");
    private IUriNode RdfsComment() => _rdfsComment ??= Rdfs("comment");
    private IUriNode RdfsDomain() => _rdfsDomain ??= Rdfs("domain");
    private IUriNode RdfsRange() => _rdfsRange ??= Rdfs("range");
    private IUriNode RdfsSubClassOf() => _rdfsSubClassOf ??= Rdfs("subClassOf");
    private IUriNode RdfsSubPropertyOf() => _rdfsSubPropertyOf ??= Rdfs("subPropertyOf");

    private IUriNode OwlNil()
    {
        var nil = _graph.CreateUriNode(new Uri(Namespaces.Rdf + "nil"));
        return nil;
    }

    private IUriNode Owl(string local) =>
        _graph.CreateUriNode(new Uri(Namespaces.Owl + local));
    private IUriNode Rdf(string local) =>
        _graph.CreateUriNode(new Uri(Namespaces.Rdf + local));
    private IUriNode Rdfs(string local) =>
        _graph.CreateUriNode(new Uri(Namespaces.Rdfs + local));
}

/// <summary>
/// Well-known RDF/OWL namespace URIs.
/// </summary>
public static class Namespaces
{
    public const string Owl = "http://www.w3.org/2002/07/owl#";
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    public const string OwlOntology = "http://www.w3.org/2002/07/owl#Ontology"; // for external refs
}

/// <summary>
/// Supported ontology serialization formats.
/// </summary>
public enum OntologyOutputFormat
{
    Turtle = 0,
    JsonLd = 1,
    RdfXml = 2
}
