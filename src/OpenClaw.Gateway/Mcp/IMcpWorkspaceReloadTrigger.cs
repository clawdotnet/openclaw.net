namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// Triggers a workspace MCP reload. Implemented by <c>McpWorkspaceWatcherService</c>
/// (file-based reload); the Nacos config event subscription (issue #238) funnels
/// into the same trigger so publish events reuse the established reload path
/// instead of duplicating it. Extracted to an interface so the subscription
/// service depends on the trigger, not on the file-watching implementation.
/// </summary>
public interface IMcpWorkspaceReloadTrigger
{
    /// <summary>Requests one workspace MCP reload; coalesced by the implementation's reload loop.</summary>
    void TriggerReload();
}