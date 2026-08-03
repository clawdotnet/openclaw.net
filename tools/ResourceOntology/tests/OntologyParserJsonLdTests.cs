using ResourceOntology.Api.Services;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;
using Xunit;

namespace ResourceOntology.Tests;

public class OntologyParserJsonLdTests
{
    private static string FindResourceOwlPath()
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = start; dir != null; dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "ontology", "Resource.owl");
            if (File.Exists(direct))
                return direct;
        }

        // tests/bin/{config}/net10.0 → ../../../../ontology/Resource.owl
        var fromBase = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "ontology", "Resource.owl"));
        if (File.Exists(fromBase))
            return fromBase;

        throw new FileNotFoundException("Could not locate tools/ResourceOntology/ontology/Resource.owl");
    }

    private static string ExportOwlFileToJsonLd(string owlPath)
    {
        var graph = new Graph();
        new RdfXmlParser().Load(graph, owlPath);
        var store = new TripleStore();
        store.Add(graph);
        var writer = new JsonLdWriter(new JsonLdWriterOptions { UseNativeTypes = true });
        using var sw = new System.IO.StringWriter();
        writer.Save(store, sw);
        return sw.ToString();
    }

    [Fact]
    public void Parse_JsonLdRoundTrip_MatchesOwlCriticalStats()
    {
        var parser = new OntologyParser();
        var owlPath = FindResourceOwlPath();
        var fromOwl = parser.ParseFile(owlPath);

        var jsonLd = ExportOwlFileToJsonLd(owlPath);
        using var reader = new StringReader(jsonLd);
        var fromJsonLd = parser.Parse(reader, "Resource.jsonld");

        Assert.True(fromOwl.Stats.Classes > 0, "fixture OWL should have classes");
        Assert.True(
            fromOwl.Stats.Classes == fromJsonLd.Stats.Classes,
            $"Classes owl={fromOwl.Stats.Classes} jsonld={fromJsonLd.Stats.Classes}");
        Assert.True(
            fromOwl.Stats.Individuals == fromJsonLd.Stats.Individuals,
            $"Individuals owl={fromOwl.Stats.Individuals} jsonld={fromJsonLd.Stats.Individuals}");
        Assert.True(
            fromOwl.Stats.ObjectProperties == fromJsonLd.Stats.ObjectProperties,
            $"ObjectProperties owl={fromOwl.Stats.ObjectProperties} jsonld={fromJsonLd.Stats.ObjectProperties}");
        Assert.True(
            fromOwl.Stats.DatatypeProperties == fromJsonLd.Stats.DatatypeProperties,
            $"DatatypeProperties owl={fromOwl.Stats.DatatypeProperties} jsonld={fromJsonLd.Stats.DatatypeProperties}");
        Assert.True(
            fromOwl.Stats.SubClassAxioms == fromJsonLd.Stats.SubClassAxioms,
            $"SubClassAxioms owl={fromOwl.Stats.SubClassAxioms} jsonld={fromJsonLd.Stats.SubClassAxioms}");
    }

    [Fact]
    public void Parse_InvalidJsonLd_Throws()
    {
        var parser = new OntologyParser();
        using var reader = new StringReader("{ this is not json-ld");
        Assert.ThrowsAny<Exception>(() => parser.Parse(reader, "bad.jsonld"));
    }

    [Fact]
    public void ParseFile_RdfXml_StillWorks()
    {
        var parser = new OntologyParser();
        var dto = parser.ParseFile(FindResourceOwlPath());
        Assert.True(dto.Stats.Classes > 0);
        Assert.True(dto.Stats.Individuals > 0);
    }

    [Fact]
    public void LoadGraph_JsonLdExtension_LoadsTriples()
    {
        var p = new OntologyParser();
        var owlPath = FindResourceOwlPath();
        var json = ExportOwlFileToJsonLd(owlPath);
        var tmp = Path.Combine(Path.GetTempPath(), "resource-ontology-a1-" + Guid.NewGuid().ToString("n") + ".jsonld");
        try
        {
            File.WriteAllText(tmp, json);
            var g = p.LoadGraph(tmp);
            Assert.True(g.Triples.Count > 0);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
