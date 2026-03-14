using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

internal sealed class PluginHealthService
{
    private const string DirectoryName = "admin";
    private const string FileName = "plugin-state.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<PluginHealthService> _logger;
    private List<PluginOperatorState>? _cachedState;
    private IReadOnlyList<PluginLoadReport> _reports = [];
    private PluginHost? _pluginHost;
    private NativeDynamicPluginHost? _nativeDynamicPluginHost;

    public PluginHealthService(string storagePath, ILogger<PluginHealthService> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public IReadOnlyCollection<string> GetBlockedPluginIds()
    {
        lock (_gate)
        {
            return LoadStateUnsafe()
                .Where(static item => item.Disabled || item.Quarantined)
                .Select(static item => item.PluginId)
                .ToArray();
        }
    }

    public void SetRuntimeReports(
        IReadOnlyList<PluginLoadReport> reports,
        PluginHost? pluginHost,
        NativeDynamicPluginHost? nativeDynamicPluginHost)
    {
        lock (_gate)
        {
            _reports = reports;
            _pluginHost = pluginHost;
            _nativeDynamicPluginHost = nativeDynamicPluginHost;
        }
    }

    public IReadOnlyList<PluginHealthSnapshot> ListSnapshots()
    {
        lock (_gate)
        {
            var stateById = LoadStateUnsafe().ToDictionary(static item => item.PluginId, StringComparer.Ordinal);
            var snapshots = _reports.Select(report =>
            {
                stateById.TryGetValue(report.PluginId, out var state);
                return new PluginHealthSnapshot
                {
                    PluginId = report.PluginId,
                    Origin = report.Origin,
                    Loaded = report.Loaded,
                    BlockedByRuntimeMode = report.BlockedByRuntimeMode,
                    Disabled = state?.Disabled ?? false,
                    Quarantined = state?.Quarantined ?? false,
                    PendingReason = state?.Reason ?? report.BlockedReason,
                    EffectiveRuntimeMode = report.EffectiveRuntimeMode,
                    RequestedCapabilities = report.RequestedCapabilities ?? [],
                    LastError = report.Error,
                    LastActivityAtUtc = state?.UpdatedAtUtc,
                    RestartCount = GetRestartCount(report.PluginId),
                    ToolCount = report.ToolCount,
                    ChannelCount = report.ChannelCount,
                    CommandCount = report.CommandCount,
                    ProviderCount = report.ProviderCount,
                    Diagnostics = report.Diagnostics
                };
            }).ToList();

            foreach (var state in stateById.Values)
            {
                if (snapshots.Any(item => string.Equals(item.PluginId, state.PluginId, StringComparison.Ordinal)))
                    continue;

                snapshots.Add(new PluginHealthSnapshot
                {
                    PluginId = state.PluginId,
                    Origin = "unknown",
                    Loaded = false,
                    BlockedByRuntimeMode = false,
                    Disabled = state.Disabled,
                    Quarantined = state.Quarantined,
                    PendingReason = state.Reason,
                    EffectiveRuntimeMode = null,
                    RequestedCapabilities = [],
                    LastError = null,
                    LastActivityAtUtc = state.UpdatedAtUtc,
                    RestartCount = 0,
                    ToolCount = 0,
                    ChannelCount = 0,
                    CommandCount = 0,
                    ProviderCount = 0,
                    Diagnostics = []
                });
            }

            return snapshots
                .OrderBy(item => item.PluginId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public PluginOperatorState SetDisabled(string pluginId, bool disabled, string? reason)
        => UpsertState(pluginId, disabled, quarantined: false, reason);

    public PluginOperatorState SetQuarantined(string pluginId, bool quarantined, string? reason)
        => UpsertState(pluginId, disabled: false, quarantined, reason);

    private PluginOperatorState UpsertState(string pluginId, bool disabled, bool quarantined, string? reason)
    {
        lock (_gate)
        {
            var items = LoadStateUnsafe();
            items.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
            var state = new PluginOperatorState
            {
                PluginId = pluginId,
                Disabled = disabled,
                Quarantined = quarantined,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            items.Add(state);
            SaveStateUnsafe(items);
            return state;
        }
    }

    private int GetRestartCount(string pluginId)
    {
        if (_pluginHost is not null && _pluginHost.TryGetRestartCount(pluginId, out var restartCount))
            return restartCount;
        if (_nativeDynamicPluginHost is not null && _nativeDynamicPluginHost.TryGetRestartCount(pluginId, out restartCount))
            return restartCount;
        return 0;
    }

    private List<PluginOperatorState> LoadStateUnsafe()
    {
        if (_cachedState is not null)
            return _cachedState;

        try
        {
            if (!File.Exists(_path))
            {
                _cachedState = [];
                return _cachedState;
            }

            var json = File.ReadAllText(_path);
            _cachedState = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListPluginOperatorState) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin state from {Path}", _path);
            _cachedState = [];
        }

        return _cachedState;
    }

    private void SaveStateUnsafe(List<PluginOperatorState> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items, CoreJsonContext.Default.ListPluginOperatorState);
            File.WriteAllText(_path, json);
            _cachedState = items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save plugin state to {Path}", _path);
        }
    }
}
