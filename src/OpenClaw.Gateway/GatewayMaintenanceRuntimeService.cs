using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway;

internal sealed class GatewayMaintenanceRuntimeService
{
    private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(10);

    private readonly GatewayStartupContext _startup;
    private readonly GatewayAppRuntime _runtime;
    private readonly GatewayAutomationService _automationService;
    private readonly SemaphoreSlim _scanCacheGate = new(1, 1);
    private MaintenanceCacheEntry? _scanCache;

    public GatewayMaintenanceRuntimeService(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        GatewayAutomationService automationService)
    {
        _startup = startup;
        _runtime = runtime;
        _automationService = automationService;
    }

    public async Task<MaintenanceReportResponse> ScanAsync(SetupStatusResponse? setupStatus, CancellationToken ct)
    {
        var cached = _scanCache;
        if (cached is not null &&
            DateTimeOffset.UtcNow - cached.CreatedAtUtc <= ScanCacheTtl &&
            (cached.IncludesSetupStatus || setupStatus is null))
        {
            return cached.Report;
        }

        await _scanCacheGate.WaitAsync(ct);
        try
        {
            cached = _scanCache;
            if (cached is not null &&
                DateTimeOffset.UtcNow - cached.CreatedAtUtc <= ScanCacheTtl &&
                (cached.IncludesSetupStatus || setupStatus is null))
            {
                return cached.Report;
            }

            var report = await MaintenanceCoordinator.ScanAsync(_startup.Config, await BuildInputsAsync(setupStatus, ct), ct);
            _scanCache = new MaintenanceCacheEntry(report, DateTimeOffset.UtcNow, setupStatus is not null);
            return report;
        }
        finally
        {
            _scanCacheGate.Release();
        }
    }

    public async Task<MaintenanceFixResponse> FixAsync(MaintenanceFixRequest request, SetupStatusResponse? setupStatus, CancellationToken ct)
    {
        _scanCache = null;
        var result = await MaintenanceCoordinator.FixAsync(_startup.Config, request, await BuildInputsAsync(setupStatus, ct), ct);
        _scanCache = null;
        return result;
    }

    private async Task<MaintenanceScanInputs> BuildInputsAsync(SetupStatusResponse? setupStatus, CancellationToken ct)
    {
        var automations = await _automationService.ListAsync(ct);
        var runStates = new List<AutomationRunState>(automations.Count);
        foreach (var automation in automations)
        {
            var state = await _automationService.GetRunStateAsync(automation.Id, ct);
            if (state is not null)
                runStates.Add(state);
        }

        var pluginWarningCount = _runtime.PluginReports.Sum(static report =>
            report.Diagnostics.Count(static item => string.Equals(item.Severity, "warning", StringComparison.OrdinalIgnoreCase)));
        var pluginErrorCount = _runtime.PluginReports.Count(static report => !string.IsNullOrWhiteSpace(report.Error))
            + _runtime.PluginReports.Sum(static report =>
                report.Diagnostics.Count(static item => string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase)));
        var recentTurns = _runtime.ProviderUsage.RecentTurns(limit: 200);
        var modelDoctor = ModelDoctorEvaluator.Build(_startup.Config, _runtime.Operations.ModelProfiles, recentTurns);

        return new MaintenanceScanInputs
        {
            SetupStatus = setupStatus,
            ModelDoctor = modelDoctor,
            RecentTurns = recentTurns,
            ProviderRoutes = _runtime.Operations.LlmExecution.SnapshotRoutes(),
            AutomationRunStates = runStates,
            RuntimeMetrics = _runtime.RuntimeMetrics.Snapshot(),
            LoadedSkills = _runtime.LoadedSkills,
            ChannelDriftCount = ChannelReadinessEvaluator.Evaluate(_startup.Config, _startup.IsNonLoopbackBind)
                .Count(static item => !item.Ready || !string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase)),
            PluginWarningCount = pluginWarningCount,
            PluginErrorCount = pluginErrorCount
        };
    }

    private sealed record MaintenanceCacheEntry(
        MaintenanceReportResponse Report,
        DateTimeOffset CreatedAtUtc,
        bool IncludesSetupStatus);
}
