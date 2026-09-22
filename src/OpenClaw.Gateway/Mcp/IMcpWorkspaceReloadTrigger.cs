namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// Triggers a workspace MCP reload. Implemented by <c>McpWorkspaceWatcherService</c>
/// (file-based reload). Nacos events independently invalidate capability bindings;
/// they do not reload workspace configuration.
/// </summary>
public interface IMcpWorkspaceReloadTrigger
{
    /// <summary>Requests one workspace MCP reload; coalesced by the implementation's reload loop.</summary>
    void TriggerReload();
}