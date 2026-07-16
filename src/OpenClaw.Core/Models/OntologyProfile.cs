namespace OpenClaw.Core.Models;

/// <summary>
/// Configuration profile for ontology build operations.
/// </summary>
public sealed class OntologyBuildProfile
{
    /// <summary>Named profiles keyed by profile id.</summary>
    public Dictionary<string, OntologyBuildConfig> Profiles { get; set; } = [];
}

public sealed class OntologyBuildConfig
{
    /// <summary>Base namespace IRI for the ontology (e.g. http://openclaw.net/ontology/standard#).</summary>
    public string Namespace { get; set; } = "";

    /// <summary>Output format: turtle, jsonld, rdfxml.</summary>
    public string OutputFormat { get; set; } = "turtle";

    /// <summary>Output file path.</summary>
    public string OutputPath { get; set; } = "./tmp/ontology.ttl";

    /// <summary>When true, include the GB/T 48000.3 standard core ontology.</summary>
    public bool IncludeStandard { get; set; } = true;

    /// <summary>Optional comment for the ontology header.</summary>
    public string? Comment { get; set; }
}