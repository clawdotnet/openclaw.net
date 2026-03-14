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
    private readonly GatewayRuntimeState _runtimeState;
    private readonly HashSet<string> _blockedPluginIds;
    private readonly List<PluginBridgeProcess> _bridges = [];
    private readonly Dictionary<string, PluginBridgeProcess> _bridgesByPluginId = new(StringComparer.Ordinal);
    private readonly List<ITool> _pluginTools = [];
    private readonly List<PluginLoadReport> _reports = [];
    private readonly List<string> _skillRoots = [];
    private readonly List<IChannelAdapter> _pluginChannels = [];
    private readonly List<(string PluginId, string ChannelId, IChannelAdapter Adapter)> _pluginChannelRegistrations = [];
    private readonly List<IToolHook> _pluginHooks = [];
    private readonly List<(string PluginId, string CommandName, string Description, PluginBridgeProcess Bridge)> _pluginCommands = [];
    private readonly List<(string ProviderId, string[] Models, PluginBridgeProcess Bridge)> _pluginProviders = [];
    private readonly List<(string PluginId, string ProviderId, string[] Models, PluginBridgeProcess Bridge)> _pluginProviderRegistrations = [];

    public PluginHost(
        PluginsConfig config,
        string bridgeScriptPath,
        ILogger logger,
        GatewayRuntimeState? runtimeState = null,
        IReadOnlyCollection<string>? blockedPluginIds = null)
    {
        _config = config;
        _bridgeScriptPath = bridgeScriptPath;
        _logger = logger;
        _runtimeState = runtimeState ?? RuntimeModeResolver.Resolve(new RuntimeConfig());
        _blockedPluginIds = blockedPluginIds is { Count: > 0 }
            ? new HashSet<string>(blockedPluginIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
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
    /// Channel adapters registered by plugins. Available after <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlyList<IChannelAdapter> ChannelAdapters => _pluginChannels;

    public IReadOnlyList<(string PluginId, string ChannelId, IChannelAdapter Adapter)> ChannelRegistrations => _pluginChannelRegistrations;

    /// <summary>
    /// Tool hooks registered by plugins. Available after <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlyList<IToolHook> ToolHooks => _pluginHooks;

    /// <summary>
    /// Plugin-registered provider info. Available after <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlyList<(string ProviderId, string[] Models, PluginBridgeProcess Bridge)> ProviderRegistrations => _pluginProviders;

    public IReadOnlyList<(string PluginId, string CommandName, string Description, PluginBridgeProcess Bridge)> CommandRegistrations => _pluginCommands;

    public IReadOnlyList<(string PluginId, string ProviderId, string[] Models, PluginBridgeProcess Bridge)> ProviderRegistrationsDetailed => _pluginProviderRegistrations;

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
        _bridges.Clear();
        _bridgesByPluginId.Clear();
        _pluginTools.Clear();
        _pluginChannels.Clear();
        _pluginChannelRegistrations.Clear();
        _pluginHooks.Clear();
        _pluginCommands.Clear();
        _pluginProviders.Clear();
        _pluginProviderRegistrations.Clear();
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
            if (_blockedPluginIds.Contains(plugin.Manifest.Id))
            {
                var message = $"Plugin '{plugin.Manifest.Id}' is disabled or quarantined by operator state.";
                _reports.Add(new PluginLoadReport
                {
                    PluginId = plugin.Manifest.Id,
                    SourcePath = plugin.RootPath,
                    EntryPath = plugin.EntryPath,
                    Origin = "bridge",
                    EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                    Loaded = false,
                    BlockedReason = message,
                    Error = message,
                    Diagnostics =
                    [
                        new PluginCompatibilityDiagnostic
                        {
                            Severity = "warning",
                            Code = "operator_blocked",
                            Message = message,
                            Surface = "operator_state",
                            Path = plugin.Manifest.Id
                        }
                    ]
                });
                continue;
            }

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
                    Origin = "bridge",
                    EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
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
                Origin = "bridge",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                Loaded = false,
                Diagnostics = configDiagnostics.ToArray(),
                Error = "Plugin config validation failed."
            });
            _logger.LogError("Plugin '{PluginId}' failed config validation: {Errors}",
                id, string.Join(" | ", configDiagnostics.Select(d => d.Message)));
            return;
        }

        var bridge = new PluginBridgeProcess(_bridgeScriptPath, _logger, _config.Transport);
        var pluginConfig = GetPluginConfig(id);

        var initResult = await bridge.StartAsync(plugin.EntryPath, id, pluginConfig, ct);
        var skillDirs = ResolveSkillDirectories(plugin).ToArray();
        var requestedCapabilities = DetermineRequestedCapabilities(initResult, skillDirs);
        if (!initResult.Compatible)
        {
            _reports.Add(new PluginLoadReport
            {
                PluginId = id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.EntryPath,
                Origin = "bridge",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                RequestedCapabilities = requestedCapabilities,
                Loaded = false,
                Diagnostics = initResult.Diagnostics,
                Error = "Plugin uses unsupported OpenClaw extension APIs."
            });
            _logger.LogError("Plugin '{PluginId}' is incompatible: {Errors}",
                id, string.Join(" | ", initResult.Diagnostics.Select(d => d.Message)));
            await bridge.DisposeAsync();
            return;
        }

        var blockedCapabilities = PluginCapabilityPolicy.GetBlockedCapabilities(_runtimeState.EffectiveMode, requestedCapabilities);
        if (blockedCapabilities.Length > 0)
        {
            var message =
                $"Plugin '{id}' requires JIT runtime mode for capabilities: {string.Join(", ", blockedCapabilities)}.";
            var diagnostics = initResult.Diagnostics
                .Concat(new[]
                {
                    new PluginCompatibilityDiagnostic
                    {
                        Severity = "error",
                        Code = "jit_mode_required",
                        Message = message,
                        Surface = "runtime_mode",
                        Path = id
                    }
                })
                .ToArray();

            _reports.Add(new PluginLoadReport
            {
                PluginId = id,
                SourcePath = plugin.RootPath,
                EntryPath = plugin.EntryPath,
                Origin = "bridge",
                EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
                RequestedCapabilities = requestedCapabilities,
                Loaded = false,
                BlockedByRuntimeMode = true,
                BlockedReason = message,
                Diagnostics = diagnostics,
                Error = message
            });
            _logger.LogError("{Message}", message);
            await bridge.DisposeAsync();
            return;
        }

        _bridges.Add(bridge);
        _bridgesByPluginId[id] = bridge;
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

        // Register channels
        var channelAdapters = new List<BridgedChannelAdapter>();
        foreach (var ch in initResult.Channels)
        {
            var adapter = new BridgedChannelAdapter(bridge, ch.Id, _logger);
            channelAdapters.Add(adapter);
            _pluginChannels.Add(adapter);
            _pluginChannelRegistrations.Add((id, ch.Id, adapter));
            _logger.LogInformation("  Registered channel '{ChannelId}' from plugin '{PluginId}'", ch.Id, id);
        }

        // Wire notification handler to dispatch channel messages to the correct adapter
        if (channelAdapters.Count > 0)
        {
            bridge.SetNotificationHandler(notification =>
            {
                if (notification.Notification == "channel_message" && notification.Params is { } p)
                {
                    var channelId = p.TryGetProperty("channelId", out var cid) ? cid.GetString() : null;
                    if (channelId is not null)
                    {
                        var target = channelAdapters.Find(a => a.ChannelId == channelId);
                        if (target is not null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await target.HandleInboundAsync(p, CancellationToken.None); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to handle inbound channel message for '{ChannelId}'", channelId); }
                            });
                        }
                    }
                }
            });
        }

        // Register commands
        foreach (var cmd in initResult.Commands)
        {
            _pluginCommands.Add((id, cmd.Name, cmd.Description, bridge));
            _logger.LogInformation("  Registered command '/{CommandName}' from plugin '{PluginId}'", cmd.Name, id);
        }

        // Register event hooks
        if (initResult.EventSubscriptions.Length > 0)
        {
            var hook = new BridgedToolHook(bridge, id, initResult.EventSubscriptions, _logger);
            _pluginHooks.Add(hook);
            _logger.LogInformation("  Registered {Count} event subscription(s) from plugin '{PluginId}'",
                initResult.EventSubscriptions.Length, id);
        }

        // Register providers
        foreach (var prov in initResult.Providers)
        {
            _pluginProviders.Add((prov.Id, prov.Models, bridge));
            _pluginProviderRegistrations.Add((id, prov.Id, prov.Models, bridge));
            _logger.LogInformation("  Registered provider '{ProviderId}' from plugin '{PluginId}'", prov.Id, id);
        }

        _reports.Add(new PluginLoadReport
        {
            PluginId = id,
            SourcePath = plugin.RootPath,
            EntryPath = plugin.EntryPath,
            Origin = "bridge",
            EffectiveRuntimeMode = _runtimeState.EffectiveModeName,
            RequestedCapabilities = requestedCapabilities,
            Loaded = true,
            ToolCount = registeredCount,
            ChannelCount = initResult.Channels.Length,
            CommandCount = initResult.Commands.Length,
            EventSubscriptionCount = initResult.EventSubscriptions.Length,
            ProviderCount = initResult.Providers.Length,
            SkillDirectories = skillDirs,
            Diagnostics = [.. initResult.Diagnostics, .. reportDiagnostics]
        });
    }

    private static string[] DetermineRequestedCapabilities(BridgeInitResult initResult, IReadOnlyCollection<string> skillDirs)
    {
        var capabilities = new List<string>(initResult.Capabilities);
        if (skillDirs.Count > 0)
            capabilities.Add(PluginCapabilityPolicy.Skills);

        if (capabilities.Count == 0)
        {
            if (initResult.Tools.Length > 0)
                capabilities.Add(PluginCapabilityPolicy.Tools);
            if (initResult.Channels.Length > 0)
                capabilities.Add(PluginCapabilityPolicy.Channels);
            if (initResult.Commands.Length > 0)
                capabilities.Add(PluginCapabilityPolicy.Commands);
            if (initResult.EventSubscriptions.Length > 0)
                capabilities.Add(PluginCapabilityPolicy.Hooks);
            if (initResult.Providers.Length > 0)
                capabilities.Add(PluginCapabilityPolicy.Providers);
        }

        return PluginCapabilityPolicy.Normalize(capabilities);
    }

    private JsonElement? GetPluginConfig(string pluginId)
    {
        if (!_config.Entries.TryGetValue(pluginId, out var entry))
            return null;

        return NormalizePluginConfig(entry.Config);
    }

    private static JsonElement? NormalizePluginConfig(JsonElement? config)
    {
        if (!config.HasValue)
            return null;

        return config.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : config;
    }

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

    /// <summary>
    /// Registers plugin commands with a <see cref="OpenClaw.Core.Pipeline.ChatCommandProcessor"/>.
    /// </summary>
    public void RegisterCommandsWith(OpenClaw.Core.Pipeline.ChatCommandProcessor processor)
    {
        foreach (var (pluginId, name, description, bridge) in _pluginCommands)
        {
            processor.RegisterDynamic(name, async (args, ct) =>
            {
                var response = await bridge.SendAndWaitAsync(
                    "command_execute",
                    new BridgeCommandExecuteRequest
                    {
                        Name = name,
                        Args = args,
                    },
                    CoreJsonContext.Default.BridgeCommandExecuteRequest,
                    ct);

                if (response.Error is not null)
                    return $"Command error: {response.Error.Message}";

                if (response.Result is { } result && result.TryGetProperty("result", out var r))
                    return r.GetString() ?? "";

                return response.Result?.GetRawText() ?? "";
            });
        }
    }

    public bool TryGetRestartCount(string pluginId, out int restartCount)
    {
        if (_bridgesByPluginId.TryGetValue(pluginId, out var bridge))
        {
            restartCount = bridge.RestartCount;
            return true;
        }

        restartCount = 0;
        return false;
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
        _bridgesByPluginId.Clear();
        _pluginTools.Clear();
        _pluginChannels.Clear();
        _pluginChannelRegistrations.Clear();
        _pluginHooks.Clear();
        _pluginCommands.Clear();
        _pluginProviders.Clear();
        _pluginProviderRegistrations.Clear();
        _reports.Clear();
        _skillRoots.Clear();
    }
}
