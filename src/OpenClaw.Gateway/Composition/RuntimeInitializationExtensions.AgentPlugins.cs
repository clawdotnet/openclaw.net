using OpenClaw.Agent;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Pipeline;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
    // Agent Plugins 1.0: discover and validate Agent Plugin skills + MCP servers at startup, then
    // keep them live (new/removed plugins and MCP servers picked up without a restart). Valid skill
    // directories are appended to the skill priority chain below (so they enter the same precedence
    // used by PluginHost.SkillRoots / NativeDynamicPluginHost.SkillRoots), and the MCP servers are
    // merged into the workspace MCP reload path in StartMcpWorkspaceWatcher.
    // Extracted to a partial file to keep RuntimeInitializationExtensions.cs within its line budget
    // (GatewayStructureTests.ThinGatewayOrchestratorFiles_StayWithinSizeBudgets).
    private static AgentPluginRuntimeManager? RefreshAgentPlugins(
        PluginsConfig config,
        string? workspacePath,
        ILoggerFactory loggerFactory,
        List<string> combinedPluginSkillRoots)
    {
        AgentPluginRuntimeManager? agentPluginRuntime = null;
        if (!config.Enabled)
            return agentPluginRuntime;

        var agentPluginLogger = loggerFactory.CreateLogger("AgentPlugins");
        agentPluginRuntime = new AgentPluginRuntimeManager(config, workspacePath, agentPluginLogger);
        var agentPluginRefresh = agentPluginRuntime.Refresh();
        combinedPluginSkillRoots.AddRange(agentPluginRefresh.SkillDirectories);

        // 失败边界：不静默吞掉诊断——启动时逐条记录（错误→LogError，警告→LogWarning）
        foreach (var diag in agentPluginRefresh.Diagnostics)
        {
            if (string.Equals(diag.Severity, "error", StringComparison.OrdinalIgnoreCase))
                agentPluginLogger.LogError("Agent Plugin {Surface} issue at {Path}: {Code} — {Message}",
                    diag.Surface, diag.Path, diag.Code, diag.Message);
            else
                agentPluginLogger.LogWarning("Agent Plugin {Surface} notice at {Path}: {Code} — {Message}",
                    diag.Surface, diag.Path, diag.Code, diag.Message);
        }

        return agentPluginRuntime;
    }

    /// <summary>
    /// Starts the runtime Agent Plugin watcher (live refresh for newly installed/removed plugins
    /// and their MCP servers) and roots it for the process lifetime via the shutdown coordinator.
    /// Returns null when the agent-plugin pipeline is disabled.
    ///
    /// Gating note (product decision): agent-plugin MCP servers are deliberately NOT gated by
    /// <c>Plugins.Mcp.Enabled</c>. That flag gates only the legacy config-declared server registry
    /// (<c>Plugins.Mcp.Servers</c> → McpRegistry.RegisterToolsAsync). Agent-plugin and workspace MCP
    /// servers share the workspace watcher path and are governed by the master <c>Plugins.Enabled</c>
    /// toggle — when it is off, this watcher is never created and the agent-plugin MCP provider is
    /// null. Gating agent-plugin MCP on Plugins.Mcp.Enabled (default false) would silently disable a
    /// capability a plugin manifest declares, and would be inconsistent with the ungated workspace
    /// mcp.json path.
    /// </summary>
    private static AgentPluginWatcherService? StartAgentPluginWatcher(
        WebApplication app,
        AgentPluginRuntimeManager? agentPluginRuntime,
        string? workspacePath,
        PluginsConfig config,
        IAgentRuntime agentRuntime,
        SkillWatcherService skillWatcher,
        McpWorkspaceWatcherService mcpWatcher,
        ILoggerFactory loggerFactory)
    {
        if (agentPluginRuntime is null)
            return null;

        var logger = loggerFactory.CreateLogger<AgentPluginWatcherService>();
        var roots = PluginDiscovery.GetAgentPluginDiscoveryRoots(config, workspacePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var watcher = new AgentPluginWatcherService(
            agentPluginRuntime,
            agentRuntime,
            skillWatcher,
            mcpWatcher,
            roots,
            logger);
        watcher.Start(app.Lifetime.ApplicationStopping);

        app.Services.GetRequiredService<GatewayRuntimeShutdownCoordinator>()
            .RegisterAsyncCleanup("agent plugin watcher", _ => watcher.DisposeAsync());
        logger.LogInformation("Agent Plugin live refresh watching {Count} discovery root(s).", roots.Length);

        return watcher;
    }
}
