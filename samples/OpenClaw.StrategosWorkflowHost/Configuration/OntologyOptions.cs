namespace OpenClaw.StrategosWorkflowHost.Configuration;

/// <summary>
/// Binds the <c>Strategos:Ontology</c> configuration section.
/// </summary>
public sealed class OntologyOptions
{
    /// <summary>
    /// Gates the whole ontology surface. When false neither the MCP server DI block nor
    /// the <c>/mcp</c> endpoint is registered, so the sidecar behaves exactly as it did
    /// before P2. Off by default; the Development profile turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Port the gateway is told to reach the sidecar's <c>/mcp</c> endpoint on. The
    /// sidecar itself listens on whatever ASP.NET Core is configured with; this value is
    /// what lands in the MCP App manifest.
    /// </summary>
    public int Port { get; set; } = 5098;

    /// <summary>
    /// Absolute or relative path the <c>openclaw.mcpapp.json</c> manifest is written to at
    /// startup. Null means "do not write a manifest" — the manifest writer is wired in a
    /// follow-up task; Task 1 only binds the value so the config shape is stable.
    /// </summary>
    public string? ManifestOutputPath { get; set; }
}
