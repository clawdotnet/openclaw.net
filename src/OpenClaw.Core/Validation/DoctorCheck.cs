using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

/// <summary>
/// Provides a self-diagnostic `--doctor` CLI mode that runs pre-flight checks on the
/// current configuration and environment, enabling rapid troubleshooting.
/// </summary>
public static class DoctorCheck
{
    public static async Task<bool> RunAsync(
        GatewayConfig config,
        GatewayRuntimeState runtimeState,
        ConfigSourceDiagnostics? configSources = null)
    {
        var localState = LocalSetupStateLoader.Load(config.Memory.StoragePath);
        var report = await SetupVerificationService.BuildDoctorReportAsync(new DoctorReportRequest
        {
            Config = config,
            RuntimeState = runtimeState,
            Policy = localState.Policy,
            OperatorAccountCount = localState.OperatorAccountCount,
            Offline = false,
            RequireProvider = false,
            CheckPortAvailability = true,
            WorkspacePath = config.Tooling.WorkspaceRoot,
            ModelDoctor = ModelDoctorEvaluator.Build(config),
            ConfigSources = configSources
        }, CancellationToken.None);

        Console.WriteLine(SetupVerificationService.RenderDoctorText(report));
        Console.WriteLine();
        Console.WriteLine(report.HasFailures
            ? "Doctor result: blocking issues found."
            : "Doctor result: no blocking issues found.");
        return !report.HasFailures;
    }
}
