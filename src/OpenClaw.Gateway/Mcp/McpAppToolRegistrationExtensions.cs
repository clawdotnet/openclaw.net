using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;
using OpenClaw.McpApp;
using OpenClaw.McpApp.Shared;

namespace OpenClaw.Gateway.Mcp;

/// <summary>
/// Gateway-layer extensions that bridge <see cref="McpAppRegistry"/>
/// to the <see cref="NativePluginRegistry"/> so discovered MCP App tools
/// become available to the OpenClaw agent.
/// </summary>
internal static class McpAppToolRegistrationExtensions
{
    /// <summary>
    /// Discovers, connects, and registers all enabled MCP Apps into the
    /// native plugin registry. Call after the runtime is created.
    /// </summary>
    public static async Task RegisterMcpAppToolsAsync(
        this McpAppRegistry appRegistry,
        NativePluginRegistry nativeRegistry,
        McpAppsConfig config,
        CancellationToken ct = default)
    {
        if (!config.Enabled)
            return;

        await appRegistry.LoadAllAsync(ct);

        foreach (var app in appRegistry.Apps)
        {
            var pluginId = $"mcpapp:{app.AppId}";
            var displayName = app.DisplayName;

            foreach (var tool in app.GetToolDescriptors())
            {
                var nativeTool = new McpAppNativeTool(
                    app.Client!,
                    tool.LocalName,
                    tool.RemoteName,
                    tool.Description,
                    tool.InputSchemaText,
                    app);

                nativeRegistry.RegisterExternalTool(nativeTool, pluginId, displayName);
            }
        }
    }

    /// <summary>
    /// Creates an OpenClaw MCP tool that wraps an MCP App tool for use
    /// with the OpenClaw gateway's own MCP server (the /mcp endpoint).
    /// This allows external MCP clients to call MCP App tools through OpenClaw.
    /// </summary>
    public static IReadOnlyList<(IMcpAppInfoProvider App, McpAppToolDescriptor Tool)> GetRegisteredMcpAppTools(
        this McpAppRegistry appRegistry)
    {
        var results = new List<(IMcpAppInfoProvider, McpAppToolDescriptor)>();

        foreach (var app in appRegistry.Apps)
        {
            foreach (var tool in app.GetToolDescriptors())
            {
                results.Add((app, tool));
            }
        }

        return results;
    }
}