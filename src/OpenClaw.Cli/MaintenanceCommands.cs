using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Validation;

namespace OpenClaw.Cli;

internal static class MaintenanceCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var subcommand = args[0].Trim().ToLowerInvariant();
        var parsed = CliArgs.Parse(args.Skip(1).ToArray());
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(parsed.GetOption("--config") ?? GatewaySetupPaths.DefaultConfigPath));

        GatewayConfig config;
        try
        {
            config = GatewayConfigFile.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }

        var status = SetupLifecycleCommand.BuildStatus(configPath, config);
        var inputs = new MaintenanceScanInputs
        {
            ConfigPath = configPath,
            SetupStatus = status,
            ModelDoctor = ModelDoctorEvaluator.Build(config)
        };

        if (subcommand == "scan")
        {
            var report = await MaintenanceCoordinator.ScanAsync(config, inputs, CancellationToken.None);
            if (parsed.HasFlag("--json"))
                output.WriteLine(JsonSerializer.Serialize(report, CoreJsonContext.Default.MaintenanceReportResponse));
            else
                WriteReport(output, report);
            return string.Equals(report.OverallStatus, SetupCheckStates.Fail, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        if (subcommand == "fix")
        {
            var response = await MaintenanceCoordinator.FixAsync(config, new MaintenanceFixRequest
            {
                DryRun = parsed.HasFlag("--dry-run"),
                Apply = parsed.GetOption("--apply") ?? "all"
            }, inputs, CancellationToken.None);

            if (parsed.HasFlag("--json"))
                output.WriteLine(JsonSerializer.Serialize(response, CoreJsonContext.Default.MaintenanceFixResponse));
            else
                WriteFixResponse(output, response);

            return response.Success ? 0 : 1;
        }

        output.WriteLine("Usage: openclaw maintenance <scan|fix> [options]");
        return 2;
    }

    private static void WriteReport(TextWriter output, MaintenanceReportResponse report)
    {
        output.WriteLine($"Maintenance status: {report.OverallStatus}");
        output.WriteLine($"Generated at: {report.GeneratedAtUtc:O}");
        output.WriteLine($"Reliability: {report.Reliability.Score}/100 ({report.Reliability.Status})");
        output.WriteLine($"Storage: memory={report.Storage.MemoryBytes:N0}B archive={report.Storage.ArchiveBytes:N0}B orphaned_metadata={report.Storage.OrphanedSessionMetadataEntries} model_eval_artifacts={report.Storage.ModelEvaluationArtifacts} trace_artifacts={report.Storage.PromptCacheTraceArtifacts}");
        output.WriteLine($"Prompt budget: recent_turns={report.PromptBudget.RecentTurnsAnalyzed} p50={report.PromptBudget.P50InputTokens:N0} p95={report.PromptBudget.P95InputTokens:N0} agents_bytes={report.PromptBudget.AgentsFileBytes:N0} soul_bytes={report.PromptBudget.SoulFileBytes:N0} skills={report.PromptBudget.LoadedSkillCount}");
        output.WriteLine($"Drift: retries={report.Drift.ProviderRetries:N0} errors={report.Drift.ProviderErrors:N0} degraded_automations={report.Drift.DegradedAutomations} quarantined_automations={report.Drift.QuarantinedAutomations} retention_failures={report.Drift.RetentionFailures:N0} prompt_p95_delta={report.Drift.PromptP95Delta:N0}");

        if (report.Findings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Findings:");
            foreach (var finding in report.Findings)
            {
                output.WriteLine($"- [{finding.Severity}] {finding.Summary}");
                if (!string.IsNullOrWhiteSpace(finding.RecommendedCommand))
                    output.WriteLine($"  command: {finding.RecommendedCommand}");
            }
        }

        if (report.Reliability.Recommendations.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Top recommendations:");
            foreach (var recommendation in report.Reliability.Recommendations)
                output.WriteLine($"- {recommendation.Summary}: {recommendation.Command}");
        }
    }

    private static void WriteFixResponse(TextWriter output, MaintenanceFixResponse response)
    {
        output.WriteLine(response.DryRun ? "Maintenance dry-run:" : "Maintenance fix:");
        foreach (var action in response.Actions)
            output.WriteLine($"- {action.Id}: {action.Summary}");

        if (response.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Warnings:");
            foreach (var warning in response.Warnings)
                output.WriteLine($"- {warning}");
        }

        output.WriteLine();
        output.WriteLine($"Reliability: {response.Reliability.Score}/100 ({response.Reliability.Status})");
    }
}
