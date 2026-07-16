using ResourceOntology.Api.Models;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace ResourceOntology.Api.Services;

/// <summary>
/// Parses an OWL ontology serialised as RDF/XML into a UI-friendly JSON model.
/// Uses dotNetRDF for robust RDF/XML handling (blank nodes, collections, rdf:ID/about resolution),
/// then lifts the relevant OWL axioms (sub-class, disjointness, restrictions, property
/// domains/ranges, individuals and assertions) out of the resulting triple store.
/// </summary>
public class OntologyParser
{
    // Vocabulary IRIs.
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    private const string Owl = "http://www.w3.org/2002/07/owl#";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    public OntologyDto Parse(TextReader reader, string sourceName)
    {
        IGraph g = new Graph();
        new RdfXmlParser().Load(g, reader);
        return BuildModel(g, sourceName);
    }

    public OntologyDto ParseFile(string path)
    {
        IGraph g = new Graph();
        new RdfXmlParser().Load(g, path);
        return BuildModel(g, Path.GetFileName(path));
    }

    private OntologyDto BuildModel(IGraph g, string sourceName)
    {
        var dto = new OntologyDto();
        dto.Meta.SourceName = sourceName;

        // --- Vocabulary nodes -------------------------------------------------
        var rdfType = U(g, Rdf + "type");
        var subClassOf = U(g, Rdfs + "subClassOf");
        var domain = U(g, Rdfs + "domain");
        var range = U(g, Rdfs + "range");
        var label = U(g, Rdfs + "label");
        var comment = U(g, Rdfs + "comment");
        var subPropertyOf = U(g, Rdfs + "subPropertyOf");

        var owlClass = U(g, Owl + "Class");
        var owlRestriction = U(g, Owl + "Restriction");
        var owlOntology = U(g, Owl + "Ontology");
        var owlImports = U(g, Owl + "imports");
        var owlObjectProp = U(g, Owl + "ObjectProperty");
        var owlDatatypeProp = U(g, Owl + "DatatypeProperty");
        var owlAnnotationProp = U(g, Owl + "AnnotationProperty");
        var owlDisjointWith = U(g, Owl + "disjointWith");
        var owlEquivalentClass = U(g, Owl + "equivalentClass");
        var owlInverseOf = U(g, Owl + "inverseOf");
        var owlOnProperty = U(g, Owl + "onProperty");
        var owlSomeValuesFrom = U(g, Owl + "someValuesFrom");
        var owlAllValuesFrom = U(g, Owl + "allValuesFrom");
        var owlHasValue = U(g, Owl + "hasValue");
        var owlCardinality = U(g, Owl + "cardinality");
        var owlMinCardinality = U(g, Owl + "minCardinality");
        var owlMaxCardinality = U(g, Owl + "maxCardinality");
        var owlNamedIndividual = U(g, Owl + "NamedIndividual");
        var owlThing = U(g, Owl + "Thing");

        // Property "characteristic" types keyed by their short name.
        var characteristicTypes = new Dictionary<string, INode>
        {
            ["Functional"] = U(g, Owl + "FunctionalProperty"),
            ["InverseFunctional"] = U(g, Owl + "InverseFunctionalProperty"),
            ["Transitive"] = U(g, Owl + "TransitiveProperty"),
            ["Symmetric"] = U(g, Owl + "SymmetricProperty"),
            ["Asymmetric"] = U(g, Owl + "AsymmetricProperty"),
            ["Reflexive"] = U(g, Owl + "ReflexiveProperty"),
            ["Irreflexive"] = U(g, Owl + "IrreflexiveProperty"),
        };

        // --- Identify entity sets --------------------------------------------
        // Named classes: URI subjects typed owl:Class (blank nodes are anonymous expressions).
        var classIris = new HashSet<string>();
        foreach (var t in g.GetTriplesWithPredicateObject(rdfType, owlClass))
            if (t.Subject is IUriNode u) classIris.Add(u.Uri.AbsoluteUri);

        // Property subjects, with the full set of rdf:type values for each.
        var propertyTypes = new Dictionary<string, HashSet<string>>();
        void RecordPropertyTypes(INode typeNode, string _)
        {
            foreach (var t in g.GetTriplesWithPredicateObject(rdfType, typeNode))
                if (t.Subject is IUriNode u)
                {
                    var iri = u.Uri.AbsoluteUri;
                    if (!propertyTypes.TryGetValue(iri, out var set))
                        propertyTypes[iri] = set = new HashSet<string>();
                    if (t.Object is IUriNode ot) set.Add(ot.Uri.AbsoluteUri);
                }
        }
        RecordPropertyTypes(owlObjectProp, "object");
        RecordPropertyTypes(owlDatatypeProp, "datatype");
        RecordPropertyTypes(owlAnnotationProp, "annotation");
        foreach (var ct in characteristicTypes.Values) RecordPropertyTypes(ct, "");

        var propertyIris = new HashSet<string>(propertyTypes.Keys);

        // --- Ontology metadata -----------------------------------------------
        foreach (var t in g.GetTriplesWithPredicateObject(rdfType, owlOntology))
        {
            if (t.Subject is IUriNode ou) dto.Meta.Iri = ou.Uri.AbsoluteUri;
            dto.Meta.Comment ??= LiteralOf(g, t.Subject, comment);
            dto.Meta.Title ??= LiteralOf(g, t.Subject, label);
            foreach (var imp in g.GetTriplesWithSubjectPredicate(t.Subject, owlImports))
                if (imp.Object is IUriNode iu) dto.Meta.Imports.Add(iu.Uri.AbsoluteUri);
        }

        // --- Classes ----------------------------------------------------------
        var classMap = new Dictionary<string, OwlClassDto>();
        foreach (var iri in classIris)
        {
            var node = g.CreateUriNode(UriFactory.Create(iri));
            var c = new OwlClassDto
            {
                Id = iri,
                Name = LocalName(iri),
                Label = LiteralOf(g, node, label),
                Comment = LiteralOf(g, node, comment),
            };

            foreach (var t in g.GetTriplesWithSubjectPredicate(node, subClassOf))
            {
                if (t.Object is IUriNode su && classIris.Contains(su.Uri.AbsoluteUri))
                {
                    c.SubClassOf.Add(su.Uri.AbsoluteUri);
                    dto.Stats.SubClassAxioms++;
                }
                else if (t.Object is IBlankNode bn)
                {
                    var r = ParseRestriction(g, bn, owlRestriction, owlOnProperty,
                        owlSomeValuesFrom, owlAllValuesFrom, owlHasValue,
                        owlCardinality, owlMinCardinality, owlMaxCardinality, rdfType);
                    if (r != null) { c.Restrictions.Add(r); dto.Stats.Restrictions++; }
                }
            }

            foreach (var t in g.GetTriplesWithSubjectPredicate(node, owlDisjointWith))
                if (t.Object is IUriNode du && classIris.Contains(du.Uri.AbsoluteUri))
                {
                    c.DisjointWith.Add(du.Uri.AbsoluteUri);
                    dto.Stats.DisjointAxioms++;
                }

            foreach (var t in g.GetTriplesWithSubjectPredicate(node, owlEquivalentClass))
                if (t.Object is IUriNode eu && classIris.Contains(eu.Uri.AbsoluteUri))
                    c.EquivalentClasses.Add(eu.Uri.AbsoluteUri);

            classMap[iri] = c;
        }

        // --- Properties -------------------------------------------------------
        foreach (var iri in propertyIris)
        {
            var node = g.CreateUriNode(UriFactory.Create(iri));
            var types = propertyTypes[iri];
            var p = new OwlPropertyDto
            {
                Id = iri,
                Name = LocalName(iri),
                Label = LiteralOf(g, node, label),
                Comment = LiteralOf(g, node, comment),
                Kind = types.Contains(Owl + "DatatypeProperty") ? "datatype"
                     : types.Contains(Owl + "AnnotationProperty") ? "annotation"
                     : "object",
            };

            foreach (var (name, typeNode) in characteristicTypes)
                if (typeNode is IUriNode ctu && types.Contains(ctu.Uri.AbsoluteUri))
                    p.Characteristics.Add(name);

            foreach (var t in g.GetTriplesWithSubjectPredicate(node, domain))
                if (t.Object is IUriNode dn) p.Domains.Add(dn.Uri.AbsoluteUri);
            foreach (var t in g.GetTriplesWithSubjectPredicate(node, range))
                if (t.Object is IUriNode rn) p.Ranges.Add(rn.Uri.AbsoluteUri);
            foreach (var t in g.GetTriplesWithSubjectPredicate(node, owlInverseOf))
                if (t.Object is IUriNode inv) p.InverseOf = inv.Uri.AbsoluteUri;
            foreach (var t in g.GetTriplesWithSubjectPredicate(node, subPropertyOf))
                if (t.Object is IUriNode sp) p.SubPropertyOf.Add(sp.Uri.AbsoluteUri);

            dto.Properties.Add(p);
        }

        // --- Individuals ------------------------------------------------------
        // A URI subject is an individual when at least one of its rdf:type values is a
        // named class (or owl:NamedIndividual / owl:Thing), and it is not itself a
        // class or property.
        var individualMap = new Dictionary<string, OwlIndividualDto>();
        foreach (var t in g.GetTriplesWithPredicate(rdfType))
        {
            if (t.Subject is not IUriNode su) continue;
            var sIri = su.Uri.AbsoluteUri;
            if (classIris.Contains(sIri) || propertyIris.Contains(sIri)) continue;
            if (t.Object is not IUriNode ou) continue;
            var typeIri = ou.Uri.AbsoluteUri;

            bool isIndividualType = classIris.Contains(typeIri)
                || typeIri == Owl + "NamedIndividual" || typeIri == Owl + "Thing";
            if (!isIndividualType) continue;

            if (!individualMap.TryGetValue(sIri, out var ind))
            {
                ind = new OwlIndividualDto
                {
                    Id = sIri,
                    Name = LocalName(sIri),
                    Label = LiteralOf(g, su, label),
                    Comment = LiteralOf(g, su, comment),
                };
                individualMap[sIri] = ind;
            }
            if (classIris.Contains(typeIri) && !ind.Types.Contains(typeIri))
                ind.Types.Add(typeIri);
        }

        // Property assertions on individuals + per-class instance counts.
        foreach (var ind in individualMap.Values)
        {
            var node = g.CreateUriNode(UriFactory.Create(ind.Id));
            foreach (var t in g.GetTriplesWithSubject(node))
            {
                if (t.Predicate is not IUriNode pred) continue;
                var pIri = pred.Uri.AbsoluteUri;
                if (!propertyIris.Contains(pIri)) continue; // skip rdf:type, labels, comments

                var a = new AssertionDto
                {
                    Property = pIri,
                    PropertyName = LocalName(pIri),
                };
                if (t.Object is IUriNode ov)
                {
                    a.IsObject = true;
                    a.Value = ov.Uri.AbsoluteUri;
                    a.ValueName = LocalName(ov.Uri.AbsoluteUri);
                }
                else if (t.Object is ILiteralNode lit)
                {
                    a.IsObject = false;
                    a.Value = lit.Value;
                    a.Datatype = lit.DataType?.AbsoluteUri;
                }
                else continue;
                ind.Assertions.Add(a);
            }

            foreach (var typeIri in ind.Types)
                if (classMap.TryGetValue(typeIri, out var c)) c.InstanceCount++;
        }

        dto.Classes = classMap.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        dto.Properties = dto.Properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        dto.Individuals = individualMap.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        dto.Stats.Classes = dto.Classes.Count;
        dto.Stats.ObjectProperties = dto.Properties.Count(p => p.Kind == "object");
        dto.Stats.DatatypeProperties = dto.Properties.Count(p => p.Kind == "datatype");
        dto.Stats.AnnotationProperties = dto.Properties.Count(p => p.Kind == "annotation");
        dto.Stats.Individuals = dto.Individuals.Count;

        return dto;
    }

    private RestrictionDto? ParseRestriction(IGraph g, IBlankNode bn,
        INode owlRestriction, INode onProperty, INode someValuesFrom, INode allValuesFrom,
        INode hasValue, INode cardinality, INode minCardinality, INode maxCardinality, INode rdfType)
    {
        // Confirm it is an owl:Restriction (some files omit the explicit type; be lenient).
        bool typed = g.GetTriplesWithSubjectPredicate(bn, rdfType)
            .Any(t => t.Object.Equals(owlRestriction));
        var onPropTriple = g.GetTriplesWithSubjectPredicate(bn, onProperty).FirstOrDefault();
        if (!typed && onPropTriple == null) return null;

        var r = new RestrictionDto();
        if (onPropTriple?.Object is IUriNode op)
        {
            r.OnProperty = op.Uri.AbsoluteUri;
            r.OnPropertyName = LocalName(op.Uri.AbsoluteUri);
        }

        void SetFiller(INode obj)
        {
            if (obj is IUriNode fu) { r.Filler = fu.Uri.AbsoluteUri; r.FillerName = LocalName(fu.Uri.AbsoluteUri); }
            else if (obj is ILiteralNode fl) { r.Filler = fl.Value; r.FillerName = fl.Value; }
        }

        var some = g.GetTriplesWithSubjectPredicate(bn, someValuesFrom).FirstOrDefault();
        var all = g.GetTriplesWithSubjectPredicate(bn, allValuesFrom).FirstOrDefault();
        var val = g.GetTriplesWithSubjectPredicate(bn, hasValue).FirstOrDefault();
        var card = g.GetTriplesWithSubjectPredicate(bn, cardinality).FirstOrDefault();
        var min = g.GetTriplesWithSubjectPredicate(bn, minCardinality).FirstOrDefault();
        var max = g.GetTriplesWithSubjectPredicate(bn, maxCardinality).FirstOrDefault();

        if (some != null) { r.Kind = "some"; SetFiller(some.Object); }
        else if (all != null) { r.Kind = "all"; SetFiller(all.Object); }
        else if (val != null) { r.Kind = "value"; SetFiller(val.Object); }
        else if (card != null) { r.Kind = "exactly"; r.Cardinality = IntOf(card.Object); }
        else if (min != null) { r.Kind = "min"; r.Cardinality = IntOf(min.Object); }
        else if (max != null) { r.Kind = "max"; r.Cardinality = IntOf(max.Object); }
        else r.Kind = "some"; // onProperty only; treat as existential-ish placeholder

        r.Display = r.Kind switch
        {
            "some" => $"{r.OnPropertyName} some {r.FillerName}",
            "all" => $"{r.OnPropertyName} only {r.FillerName}",
            "value" => $"{r.OnPropertyName} value {r.FillerName}",
            "exactly" => $"{r.OnPropertyName} exactly {r.Cardinality}",
            "min" => $"{r.OnPropertyName} min {r.Cardinality}",
            "max" => $"{r.OnPropertyName} max {r.Cardinality}",
            _ => r.OnPropertyName ?? "restriction",
        };
        return r;
    }

    private static INode U(IGraph g, string uri) => g.CreateUriNode(UriFactory.Create(uri));

    private static string? LiteralOf(IGraph g, INode subject, INode predicate)
    {
        var t = g.GetTriplesWithSubjectPredicate(subject, predicate).FirstOrDefault();
        return t?.Object is ILiteralNode l ? l.Value : null;
    }

    private static int? IntOf(INode node) =>
        node is ILiteralNode l && int.TryParse(l.Value, out var v) ? v : null;

    /// <summary>Returns the fragment or last path segment of an IRI for display.</summary>
    private static string LocalName(string iri)
    {
        var hash = iri.LastIndexOf('#');
        string name;
        if (hash >= 0 && hash < iri.Length - 1)
            name = iri[(hash + 1)..];
        else {
            var slash = iri.LastIndexOf('/');
            if (slash >= 0 && slash < iri.Length - 1)
                name = iri[(slash + 1)..];
            else
                return iri;
        }
        // .NET System.Uri percent-encodes non-ASCII characters (e.g. Chinese).
        // Decode so the frontend displays human-readable names.
        return Uri.UnescapeDataString(name);
    }
}
