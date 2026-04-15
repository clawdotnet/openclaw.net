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
                var reviewed = state?.Reviewed ?? false;
                var (trustLevel, trustReason) = DetermineTrust(report, reviewed);
                var (compatibilityStatus, errorCount, warningCount) = SummarizeDiagnostics(report.Diagnostics);
                return new PluginHealthSnapshot
                {
                    PluginId = report.PluginId,
                    Origin = report.Origin,
                    Loaded = report.Loaded,
                    BlockedByRuntimeMode = report.BlockedByRuntimeMode,
                    Disabled = state?.Disabled ?? false,
                    Quarantined = state?.Quarantined ?? false,
                    Reviewed = reviewed,
                    PendingReason = state?.Reason ?? report.BlockedReason,
                    ReviewNotes = state?.ReviewNotes,
                    EffectiveRuntimeMode = report.EffectiveRuntimeMode,
                    TrustLevel = trustLevel,
                    TrustReason = trustReason,
                    CompatibilityStatus = compatibilityStatus,
                    ErrorCount = errorCount,
                    WarningCount = warningCount,
                    DeclaredSurface = BuildDeclaredSurfaceSummary(report),
                    SourcePath = report.SourcePath,
                    EntryPath = report.EntryPath,
                    RequestedCapabilities = report.RequestedCapabilities ?? [],
                    SkillDirectories = report.SkillDirectories,
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
                    Reviewed = state.Reviewed,
                    PendingReason = state.Reason,
                    ReviewNotes = state.ReviewNotes,
                    EffectiveRuntimeMode = null,
                    TrustLevel = state.Reviewed ? "third-party-reviewed" : "untrusted",
                    TrustReason = state.Reviewed
                        ? "Operator marked this plugin as reviewed."
                        : "Plugin has no active runtime report and has not been reviewed.",
                    CompatibilityStatus = "unknown",
                    ErrorCount = 0,
                    WarningCount = 0,
                    DeclaredSurface = "unknown",
                    SourcePath = null,
                    EntryPath = null,
                    RequestedCapabilities = [],
                    SkillDirectories = [],
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
        => UpsertState(
            pluginId,
            disabled: disabled,
            quarantined: null,
            reason: disabled ? reason : string.Empty,
            reviewed: null,
            reviewNotes: null);

    public PluginOperatorState SetQuarantined(string pluginId, bool quarantined, string? reason)
        => UpsertState(
            pluginId,
            disabled: null,
            quarantined: quarantined,
            reason: quarantined ? reason : string.Empty,
            reviewed: null,
            reviewNotes: null);

    public PluginOperatorState SetReviewed(string pluginId, bool reviewed, string? reviewNotes)
        => UpsertState(
            pluginId,
            disabled: null,
            quarantined: null,
            reason: null,
            reviewed: reviewed,
            reviewNotes: reviewed ? reviewNotes : string.Empty);

    private PluginOperatorState UpsertState(string pluginId, bool? disabled, bool? quarantined, string? reason, bool? reviewed, string? reviewNotes)
    {
        lock (_gate)
        {
            var items = LoadStateUnsafe();
            var existing = items.FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
            items.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
            var state = new PluginOperatorState
            {
                PluginId = pluginId,
                Disabled = disabled ?? existing?.Disabled ?? false,
                Quarantined = quarantined ?? existing?.Quarantined ?? false,
                Reviewed = reviewed ?? existing?.Reviewed ?? false,
                Reason = reason is null ? existing?.Reason : NormalizeNote(reason),
                ReviewNotes = reviewNotes is null ? existing?.ReviewNotes : NormalizeNote(reviewNotes),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            items.Add(state);
            SaveStateUnsafe(items);
            return state;
        }
    }

    private static string? NormalizeNote(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static (string TrustLevel, string TrustReason) DetermineTrust(PluginLoadReport report, bool reviewed)
    {
        if (reviewed)
            return ("third-party-reviewed", "Operator marked this plugin as reviewed for deployment.");

        if (!string.Equals(report.Origin, "bridge", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(report.Origin, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ("first-party", "Plugin is loaded through a built-in or native runtime path.");
        }

        var hasStructuredSurface =
            report.RequestedCapabilities.Length > 0 ||
            report.ToolCount > 0 ||
            report.ChannelCount > 0 ||
            report.CommandCount > 0 ||
            report.ProviderCount > 0 ||
            report.SkillDirectories.Length > 0;
        var hasErrors = report.Diagnostics.Any(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));

        if (hasStructuredSurface && !hasErrors)
            return ("upstream-compatible", "Plugin declares structured capabilities and passed compatibility checks.");

        if (hasStructuredSurface)
            return ("untrusted", "Plugin declares capabilities, but compatibility checks reported blocking errors.");

        return ("untrusted", "Plugin does not expose a structured capability declaration.");
    }

    private static (string CompatibilityStatus, int ErrorCount, int WarningCount) SummarizeDiagnostics(IReadOnlyList<PluginCompatibilityDiagnostic> diagnostics)
    {
        var errorCount = diagnostics.Count(static diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var warningCount = diagnostics.Count - errorCount;
        var compatibilityStatus = errorCount > 0
            ? "errors"
            : warningCount > 0
                ? "warnings"
                : "verified";
        return (compatibilityStatus, errorCount, warningCount);
    }

    private static string BuildDeclaredSurfaceSummary(PluginLoadReport report)
    {
        var items = new List<string>();
        if (report.RequestedCapabilities.Length > 0)
            items.Add($"capabilities={string.Join(",", report.RequestedCapabilities)}");
        if (report.ToolCount > 0)
            items.Add($"tools={report.ToolCount}");
        if (report.ChannelCount > 0)
            items.Add($"channels={report.ChannelCount}");
        if (report.CommandCount > 0)
            items.Add($"commands={report.CommandCount}");
        if (report.ProviderCount > 0)
            items.Add($"providers={report.ProviderCount}");
        if (report.SkillDirectories.Length > 0)
            items.Add($"skills={report.SkillDirectories.Length}");

        return items.Count == 0 ? "entry-only" : string.Join(" | ", items);
    }
}
