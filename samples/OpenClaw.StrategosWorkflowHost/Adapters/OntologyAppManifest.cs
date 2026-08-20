using System.Text.Json.Nodes;

using OpenClaw.StrategosWorkflowHost.Configuration;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// Builds the <c>openclaw.mcpapp.json</c> manifest the OpenClaw gateway's
/// <c>McpAppDiscovery</c> scans for. The AppId is stable ("strategos-ontology") so the
/// same gateway config picks up the sidecar across restarts and version bumps. The URL is
/// hardcoded to loopback + the configured port — the gateway is expected to run on the
/// same machine (or have a tunnel to the loopback), matching the rest of the sidecar's
/// trust model.
/// </summary>
/// <remarks>
/// The manifest is produced as a <see cref="JsonObject"/> rather than via the
/// <c>OpenClaw.McpApp.Models.McpAppManifest</c> type on purpose: that type lives in a
/// project that pins <c>ModelContextProtocol</c> 2.0.0, which is binary-incompatible with
/// the 1.3.0 line the Strategos ontology runtime requires (see the csproj pin note). The
/// sidecar must not take that dependency into its build graph, so we emit the same JSON
/// shape directly.
/// </remarks>
public static class OntologyAppManifest
{
    /// <summary>Stable MCP App id — also the gateway-side <c>pluginId</c> prefix.</summary>
    public const string AppId = "strategos-ontology";

    /// <summary>OpenClaw MCP protocol version this manifest targets.</summary>
    public const string ProtocolVersion = "2025-03-26";

    /// <summary>Filename the gateway's discovery scans for.</summary>
    public const string FileName = "openclaw.mcpapp.json";

    /// <summary>
    /// Builds the manifest as a JSON object matching the <c>openclaw.mcpapp.json</c> schema
    /// the gateway validates: <c>id</c> (required), <c>transport</c> ∈ {stdio, http}, and
    /// <c>url</c> when transport is http.
    /// </summary>
    public static JsonObject Build(OntologyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new JsonObject
        {
            ["id"] = AppId,
            ["name"] = "Strategos Workflow Ontology",
            ["description"] =
                "Explore, query, validate, and act on the durable agent review ontology surfaced by " +
                "the Strategos workflow sidecar. Backed by Marten event-sourced state on the sidecar; " +
                "queries are read-only by default, mutations go through ontology_action with hard " +
                "constraints enforced server-side.",
            ["version"] = "1.0.0",
            ["protocolVersion"] = ProtocolVersion,
            ["transport"] = "http",
            ["url"] = $"http://127.0.0.1:{options.Port}/mcp",
            ["capabilities"] = new JsonArray("tools"),
        };
    }
}
