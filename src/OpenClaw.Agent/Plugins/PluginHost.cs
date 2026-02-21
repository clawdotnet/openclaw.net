using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Orchestrates plugin lifecycle: discovery, loading, tool registration, and shutdown.
/// Each plugin runs in its own Node.js child process via the plugin bridge.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly PluginsConfig _config;
    private readonly string _bridgeScriptPath;
    private readonly ILogger _logger;
    private readonly List<PluginBridgeProcess> _bridges = [];
    private readonly List<ITool> _pluginTools = [];

    public PluginHost(PluginsConfig config, string bridgeScriptPath, ILogger logger)
    {
        _config = config;
        _bridgeScriptPath = bridgeScriptPath;
        _logger = logger;
    }

    /// <summary>
    /// Discovered and loaded plugin tools. Available after <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlyList<ITool> Tools => _pluginTools;

    /// <summary>
    /// Discover, filter, and load all enabled plugins.
    /// Returns the list of tools registered by all plugins.
    /// </summary>
    public async Task<IReadOnlyList<ITool>> LoadAsync(string? workspacePath, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Plugin system is disabled");
            return [];
        }

        // Discover
        var discovered = PluginDiscovery.Discover(_config, workspacePath);
        _logger.LogInformation("Discovered {Count} plugin(s)", discovered.Count);

        // Filter by allow/deny/enabled/slots
        var enabled = PluginDiscovery.Filter(discovered, _config);
        _logger.LogInformation("{Count} plugin(s) enabled after filtering", enabled.Count);

        // Load each plugin
        foreach (var plugin in enabled)
        {
            try
            {
                await LoadPluginAsync(plugin, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin '{PluginId}'", plugin.Manifest.Id);
            }
        }

        _logger.LogInformation("Loaded {ToolCount} tool(s) from {PluginCount} plugin(s)",
            _pluginTools.Count, _bridges.Count);

        return _pluginTools;
    }

    private async Task LoadPluginAsync(DiscoveredPlugin plugin, CancellationToken ct)
    {
        var id = plugin.Manifest.Id;
        _logger.LogInformation("Loading plugin '{PluginId}' from {EntryPath}", id, plugin.EntryPath);

        var bridge = new PluginBridgeProcess(_bridgeScriptPath);
        var pluginConfig = _config.Entries.TryGetValue(id, out var entry) ? entry.Config : null;

        var tools = await bridge.StartAsync(plugin.EntryPath, id, pluginConfig, ct);
        _bridges.Add(bridge);

        foreach (var reg in tools)
        {
            // Skip tools that clash with existing names
            if (_pluginTools.Any(t => t.Name == reg.Name))
            {
                _logger.LogWarning("Plugin '{PluginId}' tool '{ToolName}' skipped — name already registered",
                    id, reg.Name);
                continue;
            }

            _pluginTools.Add(new BridgedPluginTool(bridge, id, reg));
            _logger.LogInformation("  Registered tool '{ToolName}' from plugin '{PluginId}'", reg.Name, id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bridge in _bridges)
        {
            try
            {
                await bridge.DisposeAsync();
            }
            catch
            {
                // Best effort
            }
        }
        _bridges.Clear();
        _pluginTools.Clear();
    }
}
