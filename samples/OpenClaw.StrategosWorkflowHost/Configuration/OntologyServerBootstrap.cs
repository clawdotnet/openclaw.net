using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

/// <summary>
/// Single wiring point for the sidecar's ontology MCP surface, shared by
/// <c>Program.cs</c> and the test harness so both exercise the same code path.
/// </summary>
/// <remarks>
/// The surface is split in two because ASP.NET Core needs DI registration before the
/// container is built and endpoint mapping after: <see cref="AddOntologyMcpServer"/> runs
/// against the service collection, <see cref="MapOntologyMcpEndpoint"/> against the
/// endpoint route builder. Both are gated by the same
/// <c>Strategos:Ontology:Enabled</c> flag, so an operator flipping one flag either gets
/// the whole ontology surface or none of it.
/// </remarks>
public static class OntologyServerBootstrap
{
    /// <summary>Configuration section the ontology options bind from.</summary>
    public const string SectionName = "Strategos:Ontology";

    /// <summary>Path the MCP streamable-HTTP transport is mounted at.</summary>
    public const string McpPath = "/mcp";

    /// <summary>
    /// Binds <see cref="OntologyOptions"/> and, when the ontology is enabled, registers the
    /// composed <see cref="Strategos.Ontology.OntologyGraph"/> singleton plus an MCP server
    /// carrying the ontology tools. A no-op beyond options binding when disabled.
    /// </summary>
    public static OntologyOptions AddOntologyMcpServer(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        services.Configure<OntologyOptions>(section);

        var options = section.Get<OntologyOptions>() ?? new OntologyOptions();
        if (!options.Enabled)
        {
            return options;
        }

        // The graph is immutable and cheap to compose, so building it once at startup keeps
        // AddOntologyTools()'s "registered as a singleton instance" precondition satisfied.
        services.AddSingleton(OntologyGraphFactory.Build(options));

        services
            .AddMcpServer(mcp =>
            {
                mcp.ServerInfo = new Implementation
                {
                    Name = "OpenClaw.StrategosWorkflowHost.Ontology",
                    Version = "1.0.0",
                };
            })
            .WithHttpTransport(http => http.Stateless = true)
            .AddOntologyTools();

        return options;
    }

    /// <summary>
    /// Maps the MCP streamable-HTTP transport at <see cref="McpPath"/> when the ontology is
    /// enabled. When disabled the path stays unmapped, so callers get a 404 rather than a
    /// half-wired endpoint.
    /// </summary>
    public static void MapOntologyMcpEndpoint(IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetValue($"{SectionName}:Enabled", false))
        {
            return;
        }

        endpoints.MapMcp(McpPath);
    }
}
