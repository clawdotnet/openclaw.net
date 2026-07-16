using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.GraphSlicer;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Comprehensive tests for the JSON-LD Framing engine (dotNetRDF-backed).
/// </summary>
public sealed class JsonLdFramerTests
{
    private readonly JsonLdFramer _framer = new();

    // ── @type filtering ──────────────────────────────────────────────────

    [Fact]
    public void Frame_ByType_FiltersMatchedSubjects()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            { "@id": "ex:Alice", "@type": "ex:Person", "ex:name": "Alice" },
            { "@id": "ex:Widget", "@type": "ex:Product", "ex:name": "Widget" },
            { "@id": "ex:Bob", "@type": "ex:Person", "ex:name": "Bob" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person"
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Bob", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_ByMultipleTypes_UsesUnionMatching()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            { "@id": "ex:Alice", "@type": "ex:Person", "ex:name": "Alice" },
            { "@id": "ex:Acme", "@type": "ex:Organization", "ex:name": "Acme" },
            { "@id": "ex:Widget", "@type": "ex:Product", "ex:name": "Widget" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": ["ex:Person", "ex:Organization"]
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Acme", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget", result, StringComparison.Ordinal);
    }

    // ── @id matching ─────────────────────────────────────────────────────

    [Fact]
    public void Frame_ById_MatchesSpecificSubject()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            { "@id": "ex:Alice", "ex:name": "Alice" },
            { "@id": "ex:Bob", "ex:name": "Bob" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@id": "ex:Alice"
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", result, StringComparison.Ordinal);
    }

    // ── property filtering ───────────────────────────────────────────────

    [Fact]
    public void Frame_PropertyFilter_IncludesSpecifiedProperties()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:age": 30,
              "ex:city": "Beijing"
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "@explicit": true,
          "ex:name": {},
          "ex:city": {}
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Beijing", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ex:age\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_WithoutExplicit_IncludesAllProperties()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:age": 30,
              "ex:city": "Beijing"
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "ex:name": {}
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("30", result, StringComparison.Ordinal);
        Assert.Contains("Beijing", result, StringComparison.Ordinal);
    }

    // ── nested framing (object references) ───────────────────────────────

    [Fact]
    public void Frame_NestedObject_EmbedsAndFiltersReferencedNode()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:worksFor": { "@id": "ex:Acme" }
            },
            {
              "@id": "ex:Acme",
              "@type": "ex:Organization",
              "ex:name": "Acme Corp",
              "ex:employeeCount": 5000,
              "ex:stockSymbol": "ACM"
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "ex:name": {},
          "ex:worksFor": {
            "@type": "ex:Organization",
            "@explicit": true,
            "ex:name": {}
          }
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Acme Corp", result, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ACM", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_NestedArray_EmbedsAndFiltersAllReferencedNodes()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:knows": [
                { "@id": "ex:Bob" },
                { "@id": "ex:Carol" }
              ]
            },
            {
              "@id": "ex:Bob",
              "@type": "ex:Person",
              "ex:name": "Bob",
              "ex:age": 25,
              "ex:hobby": "Golf"
            },
            {
              "@id": "ex:Carol",
              "@type": "ex:Person",
              "ex:name": "Carol",
              "ex:age": 28,
              "ex:hobby": "Yoga"
            },
            {
              "@id": "ex:Dan",
              "@type": "ex:Person",
              "ex:name": "Dan"
            }
          ]
        }
        """;

        // Use @id to select Alice as entry point
        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@id": "ex:Alice",
          "ex:name": {},
          "ex:knows": {
            "@explicit": true,
            "ex:name": {}
          }
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Bob", result, StringComparison.Ordinal);
        Assert.Contains("Carol", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Dan", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Golf", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Yoga", result, StringComparison.Ordinal);
    }

    // ── @embed modes ──────────────────────────────────────────────────────

    [Fact]
    public void Frame_EmbedNever_OutputsReferenceOnly()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:worksFor": { "@id": "ex:Acme" }
            },
            {
              "@id": "ex:Acme",
              "@type": "ex:Organization",
              "ex:name": "Acme Corp"
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "ex:name": {},
          "ex:worksFor": {
            "@embed": "@never"
          }
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Acme", result, StringComparison.Ordinal);
        // With @never, Acme Corp (embedded value) should NOT appear
        Assert.DoesNotContain("Acme Corp", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_EmbedAlways_AlwaysEmbedsFullNode()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "ex:name": "Alice",
              "ex:friend": { "@id": "ex:Bob" }
            },
            {
              "@id": "ex:Bob",
              "ex:name": "Bob",
              "ex:age": 25
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "ex:name": {},
          "ex:friend": {
            "@embed": "@always"
          }
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.Contains("Bob", result, StringComparison.Ordinal);
        Assert.Contains("25", result, StringComparison.Ordinal);
    }

    // ── requireAll ────────────────────────────────────────────────────────

    [Fact]
    public void Frame_RequireAll_ExcludesNodesMissingProperties()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:email": "alice@example.org"
            },
            {
              "@id": "ex:Bob",
              "@type": "ex:Person",
              "ex:name": "Bob"
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "@requireAll": true,
          "ex:name": {},
          "ex:email": {}
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("Alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", result, StringComparison.Ordinal);
    }

    // ── @context handling ─────────────────────────────────────────────────

    [Fact]
    public void Frame_ContextFromFrame_TakesPrecedence()
    {
        var jsonLd = """
        {
          "@context": { "old": "http://old.example.org/" },
          "@graph": [
            { "@id": "old:Alice", "old:name": "Alice" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "new": "http://new.example.org/" }
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        // Frame's @context replaces input @context in the output
        Assert.Contains("\"@context\":{\"new\":\"http://new.example.org/\"}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\":\"http://old.example.org/\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_NoFrameContext_InheritsInputContext()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            { "@id": "ex:Alice", "@type": "ex:Person", "ex:name": "Alice" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person"
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        Assert.Contains("http://example.org/", result, StringComparison.Ordinal);
        Assert.Contains("Alice", result, StringComparison.Ordinal);
    }

    // ── edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void Frame_NoMatches_ReturnsEmptyGraph()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            { "@id": "ex:Alice", "@type": "ex:Person", "ex:name": "Alice" }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:NonExistent"
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        // dotNetRDF produces empty @graph
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.TryGetProperty("@graph", out var graph))
            Assert.Equal(0, graph.GetArrayLength());
    }

    [Fact]
    public void Frame_SingleNode_ProducesStructuredOutput()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:age": 30
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person"
        }
        """;

        var result = _framer.Frame(jsonLd, frame);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("@context", out _));
        Assert.True(root.TryGetProperty("@graph", out var graph));
        Assert.Equal(1, graph.GetArrayLength());
        var node = graph[0];
        Assert.True(node.TryGetProperty("@id", out _));
    }

    [Fact]
    public void Frame_LiteralValues_ArePreservedAsIs()
    {
        var jsonLd = """
        {
          "@context": { "ex": "http://example.org/" },
          "@graph": [
            {
              "@id": "ex:Alice",
              "@type": "ex:Person",
              "ex:name": "Alice",
              "ex:age": 30,
              "ex:score": 95.5,
              "ex:active": true
            }
          ]
        }
        """;

        var frame = """
        {
          "@context": { "ex": "http://example.org/" },
          "@type": "ex:Person",
          "@explicit": true,
          "ex:name": {},
          "ex:age": {},
          "ex:score": {},
          "ex:active": {}
        }
        """;

        var result = _framer.Frame(jsonLd, frame);
        var doc = JsonDocument.Parse(result);
        var node = doc.RootElement.GetProperty("@graph")[0];

        Assert.Equal("Alice", node.GetProperty("ex:name").GetString());
        Assert.Equal(30, node.GetProperty("ex:age").GetInt32());
        Assert.Equal(95.5, node.GetProperty("ex:score").GetDouble());
        Assert.True(node.GetProperty("ex:active").GetBoolean());
    }

    // ── integration: framer + engine ──────────────────────────────────────

    [Fact]
    public async Task Engine_WithTypeFrame_OutputsFilteredJsonLd()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .
        @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .

        ex:Alice a ex:Person ; ex:name "Alice" .
        ex:Widget a ex:Product ; ex:name "Widget" .
        ex:Bob a ex:Person ; ex:name "Bob" .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            FrameJson = """
            {
              "@context": { "ex": "http://example.org/" },
              "@type": "ex:Person"
            }
            """,
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage ?? "Expected success.");
        Assert.True(File.Exists(outputPath));

        var content = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Alice", content, StringComparison.Ordinal);
        Assert.Contains("Bob", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_WithNestedFrame_EmbedsReferencedNodes()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .

        ex:Alice a ex:Person ; ex:name "Alice" ; ex:worksFor ex:Acme .
        ex:Bob a ex:Person ; ex:name "Bob" .
        ex:Acme a ex:Organization ; ex:name "Acme Corp" ; ex:employeeCount 5000 .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            FrameJson = """
            {
              "@context": { "ex": "http://example.org/" },
              "@id": "ex:Alice",
              "ex:name": {},
              "ex:worksFor": {
                "@type": "ex:Organization",
                "@explicit": true,
                "ex:name": {}
              }
            }
            """,
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage ?? "Expected success.");
        var content = await File.ReadAllTextAsync(outputPath);

        Assert.Contains("Alice", content, StringComparison.Ordinal);
        Assert.Contains("Acme Corp", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", content, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_WithExplicitFrame_OmitsUnlistedProperties()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .

        ex:Alice a ex:Person ; ex:name "Alice" ; ex:age 30 ; ex:city "Beijing" .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            FrameJson = """
            {
              "@context": { "ex": "http://example.org/" },
              "@type": "ex:Person",
              "@explicit": true,
              "ex:name": {}
            }
            """,
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage ?? "Expected success.");
        var content = await File.ReadAllTextAsync(outputPath);

        Assert.Contains("Alice", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ex:age\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Beijing", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_InvalidFrameJson_ReturnsError()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .
        ex:Alice a ex:Person ; ex:name "Alice" .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            FrameJson = "not valid json {{{",
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("JSON-LD framing failed", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-framer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}