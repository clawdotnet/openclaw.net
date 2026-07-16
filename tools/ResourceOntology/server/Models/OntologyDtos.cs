namespace ResourceOntology.Api.Models;

/// <summary>High-level description of the loaded ontology document.</summary>
public class OntologyMeta
{
    public string? Iri { get; set; }
    public string? Title { get; set; }
    public string? Comment { get; set; }
    public string SourceName { get; set; } = "";
    public List<string> Imports { get; set; } = new();
}

/// <summary>An anonymous class expression used as a super-class, e.g. an owl:Restriction.</summary>
public class RestrictionDto
{
    /// <summary>Property the restriction is on (full IRI).</summary>
    public string? OnProperty { get; set; }
    public string? OnPropertyName { get; set; }

    /// <summary>some | all | value | min | max | exactly | cardinality.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Filler class / datatype IRI (for some/all/cardinality fillers).</summary>
    public string? Filler { get; set; }
    public string? FillerName { get; set; }

    /// <summary>Cardinality number for min/max/exactly restrictions.</summary>
    public int? Cardinality { get; set; }

    /// <summary>Human-readable rendering, e.g. "providesAccessTo some Variable".</summary>
    public string Display { get; set; } = "";
}

public class OwlClassDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? Comment { get; set; }
    public List<string> SubClassOf { get; set; } = new();
    public List<string> DisjointWith { get; set; } = new();
    public List<string> EquivalentClasses { get; set; } = new();
    public List<RestrictionDto> Restrictions { get; set; } = new();
    public int InstanceCount { get; set; }
    public bool Declared { get; set; } = true;
}

public class OwlPropertyDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? Comment { get; set; }

    /// <summary>object | datatype | annotation.</summary>
    public string Kind { get; set; } = "object";

    public List<string> Domains { get; set; } = new();
    public List<string> Ranges { get; set; } = new();
    public string? InverseOf { get; set; }
    public List<string> SubPropertyOf { get; set; } = new();

    /// <summary>Functional, InverseFunctional, Transitive, Symmetric, Asymmetric, Reflexive, Irreflexive.</summary>
    public List<string> Characteristics { get; set; } = new();
}

public class AssertionDto
{
    public string Property { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public string Value { get; set; } = "";
    public string? ValueName { get; set; }
    public string? Datatype { get; set; }

    /// <summary>True when Value is another individual (object property assertion).</summary>
    public bool IsObject { get; set; }
}

public class OwlIndividualDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? Comment { get; set; }
    public List<string> Types { get; set; } = new();
    public List<AssertionDto> Assertions { get; set; } = new();
}

public class StatsDto
{
    public int Classes { get; set; }
    public int ObjectProperties { get; set; }
    public int DatatypeProperties { get; set; }
    public int AnnotationProperties { get; set; }
    public int Individuals { get; set; }
    public int Restrictions { get; set; }
    public int DisjointAxioms { get; set; }
    public int SubClassAxioms { get; set; }
}

public class OntologyDto
{
    public OntologyMeta Meta { get; set; } = new();
    public List<OwlClassDto> Classes { get; set; } = new();
    public List<OwlPropertyDto> Properties { get; set; } = new();
    public List<OwlIndividualDto> Individuals { get; set; } = new();
    public StatsDto Stats { get; set; } = new();
}

public class OntologyFileEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class OntologyFileList
{
    public List<OntologyFileEntry> Files { get; set; } = new();
}

public class HealthDto
{
    public string Status { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(OntologyDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OntologyFileList))]
[System.Text.Json.Serialization.JsonSerializable(typeof(HealthDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ErrorDto))]
internal partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

public class ErrorDto
{
    public string Error { get; set; } = "";
}
