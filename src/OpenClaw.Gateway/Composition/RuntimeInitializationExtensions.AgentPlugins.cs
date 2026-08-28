using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway.Composition;

internal static partial class RuntimeInitializationExtensions
{
    // Agent Plugins 1.0: discover and validate Agent Plugin skills + MCP servers once at
    // startup. Valid skill directories are appended to the skill priority chain below (so they
    // enter the same precedence used by PluginHost.SkillRoots / NativeDynamicPluginHost.SkillRoots),
    // and the MCP servers are merged into the workspace MCP reload path in StartMcpWorkspaceWatcher.
    // Extracted to a partial file to keep RuntimeInitializationExtensions.cs within its line budget
    // (GatewayStructureTests.ThinGatewayOrchestratorFiles_StayWithinSizeBudgets).
    private static async Task<AgentPluginRuntimeManager?> RefreshAgentPluginsAsync(
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
        var agentPluginRefresh = await agentPluginRuntime.RefreshAsync();
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
}
