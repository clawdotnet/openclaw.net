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
    private readonly List<PluginLoadReport> _reports = [];
    private readonly List<string> _skillRoots = [];

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
    /// Per-plugin reports for doctor/status surfaces.
    /// </summary>
    public IReadOnlyList<PluginLoadReport> Reports => _reports;

    /// <summary>
    /// Skill directories declared by successfully loaded plugins.
    /// </summary>
    public IReadOnlyList<string> SkillRoots => _skillRoots;

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
        _reports.Clear();
        _skillRoots.Clear();
        var discovery = PluginDiscovery.DiscoverWithDiagnostics(_config, workspacePath);
        var discovered = discovery.Plugins;
        _reports.AddRange(discovery.Reports);
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
                _reports.Add(new PluginLoadReport
                {
                    PluginId = plugin.Manifest.Id,
                    SourcePath = plugin.RootPath,
                    EntryPath = plugin.EntryPath,
                    Loaded = false,
                    Error = ex.Message
                });
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

        var configDiagnostics = PluginConfigValidator.Validate(plugin.Manifest, GetPluginConfig(id));
        if (configDiagnostics.Count > 0)
        {
            _reports.Add(new PluginLoadReport
            {
                PluginId = id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.EntryPath,
                Loaded = false,
                Diagnostics = configDiagnostics.ToArray(),
                Error = "Plugin config validation failed."
            });
            _logger.LogError("Plugin '{PluginId}' failed config validation: {Errors}",
                id, string.Join(" | ", configDiagnostics.Select(d => d.Message)));
            return;
        }

        var bridge = new PluginBridgeProcess(_bridgeScriptPath, _logger);
        var pluginConfig = GetPluginConfig(id);

        var initResult = await bridge.StartAsync(plugin.EntryPath, id, pluginConfig, ct);
        if (!initResult.Compatible)
        {
            _reports.Add(new PluginLoadReport
            {
                PluginId = id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.EntryPath,
                Loaded = false,
                Diagnostics = initResult.Diagnostics,
                Error = "Plugin uses unsupported OpenClaw extension APIs."
            });
            _logger.LogError("Plugin '{PluginId}' is incompatible: {Errors}",
                id, string.Join(" | ", initResult.Diagnostics.Select(d => d.Message)));
            await bridge.DisposeAsync();
            return;
        }

        _bridges.Add(bridge);
        var skillDirs = ResolveSkillDirectories(plugin).ToArray();
        foreach (var skillDir in skillDirs)
        {
            if (!_skillRoots.Contains(skillDir, StringComparer.Ordinal))
                _skillRoots.Add(skillDir);
        }

        var reportDiagnostics = new List<PluginCompatibilityDiagnostic>();
        var registeredCount = 0;
        foreach (var reg in initResult.Tools)
        {
            // Skip tools that clash with existing names
            if (_pluginTools.Any(t => t.Name == reg.Name))
            {
                _logger.LogWarning("Plugin '{PluginId}' tool '{ToolName}' skipped — name already registered",
                    id, reg.Name);
                reportDiagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Severity = "warning",
                    Code = "duplicate_tool_name",
                    Message = $"Tool '{reg.Name}' from plugin '{id}' was skipped because that tool name is already registered.",
                    Surface = "registerTool",
                    Path = reg.Name
                });
                continue;
            }

            _pluginTools.Add(new BridgedPluginTool(bridge, id, reg));
            _logger.LogInformation("  Registered tool '{ToolName}' from plugin '{PluginId}'", reg.Name, id);
            registeredCount++;
        }

        _reports.Add(new PluginLoadReport
        {
            PluginId = id,
            SourcePath = plugin.RootPath,
            EntryPath = plugin.EntryPath,
            Loaded = true,
            ToolCount = registeredCount,
            SkillDirectories = skillDirs,
            Diagnostics = [.. initResult.Diagnostics, .. reportDiagnostics]
        });
    }

    private JsonElement? GetPluginConfig(string pluginId)
        => _config.Entries.TryGetValue(pluginId, out var entry) ? entry.Config : null;

    private IEnumerable<string> ResolveSkillDirectories(DiscoveredPlugin plugin)
    {
        foreach (var skillDir in plugin.Manifest.Skills ?? [])
        {
            if (string.IsNullOrWhiteSpace(skillDir))
                continue;

            var resolved = Path.GetFullPath(Path.Combine(plugin.RootPath, skillDir));
            if (!Directory.Exists(resolved))
            {
                _logger.LogWarning("Plugin '{PluginId}' declared missing skill directory {Path}", plugin.Manifest.Id, resolved);
                continue;
            }

            yield return resolved;
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
        _reports.Clear();
        _skillRoots.Clear();
    }
}
