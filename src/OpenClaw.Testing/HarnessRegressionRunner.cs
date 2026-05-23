using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Setup;

namespace OpenClaw.Testing;

public sealed class HarnessRegressionRunner
{
    private readonly IReadOnlyList<IHarnessRegressionScenario> _scenarios;

    public HarnessRegressionRunner(IEnumerable<IHarnessRegressionScenario>? scenarios = null)
    {
        _scenarios = (scenarios ?? HarnessRegressionScenarios.CreateDefault()).ToArray();
    }

    public IReadOnlyList<IHarnessRegressionScenario> Scenarios => _scenarios;

    public async ValueTask<HarnessRegressionReport> RunAsync(
        HarnessRegressionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HarnessRegressionOptions();
        var startedAt = DateTimeOffset.UtcNow;
        var context = BuildContext(options);
        try
        {
            var selected = SelectScenarios(options.Category).ToArray();
            var results = new List<HarnessRegressionScenarioResult>(selected.Length);

            if (selected.Length == 0)
            {
                results.Add(BuildNoScenariosResult(options.Category));
            }
            else
            {
                foreach (var scenario in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await scenario.RunAsync(context, cancellationToken);
                    results.Add(NormalizeResult(scenario, result));
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            var summary = BuildSummary(results);
            var overallStatus = ResolveOverallStatus(results, options.Strict);

            return new HarnessRegressionReport
            {
                Id = CreateReportId(startedAt),
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = (long)Math.Max(0, (completedAt - startedAt).TotalMilliseconds),
                ConfigPath = context.ConfigPath,
                ProposalId = options.ProposalId,
                Offline = context.Offline,
                Strict = options.Strict,
                OverallStatus = overallStatus,
                Results = results,
                Summary = summary,
                Recommendations = BuildRecommendations(results)
            };
        }
        finally
        {
            TryDeleteDirectory(context.TempWorkspacePath);
        }
    }

    public static int GetExitCode(HarnessRegressionReport report)
    {
        if (report.Results.Any(static result =>
                result.Required &&
                string.Equals(result.Status, HarnessRegressionScenarioStatus.Failed, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (report.Strict && report.Results.Any(static result =>
                result.Required &&
                (string.Equals(result.Status, HarnessRegressionScenarioStatus.Warning, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result.Status, HarnessRegressionScenarioStatus.Skipped, StringComparison.OrdinalIgnoreCase))))
        {
            return 1;
        }

        return 0;
    }

    private HarnessRegressionContext BuildContext(HarnessRegressionOptions options)
    {
        var configPathExplicit = !string.IsNullOrWhiteSpace(options.ConfigPath);
        var configPath = Path.GetFullPath(GatewaySetupPaths.ExpandPath(
            options.ConfigPath ?? GatewaySetupPaths.DefaultConfigPath));
        var configExists = File.Exists(configPath);
        GatewayConfig? config = null;
        string? configLoadError = null;

        if (configExists)
        {
            try
            {
                config = GatewayConfigFile.Load(configPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                configLoadError = ex.Message;
            }
        }
        else
        {
            configLoadError = $"Config file not found: {configPath}";
        }

        return new HarnessRegressionContext
        {
            ConfigPath = configPath,
            ConfigPathExplicit = configPathExplicit,
            ConfigExists = configExists,
            Config = config,
            ConfigLoadError = configLoadError,
            Offline = options.Offline,
            Strict = options.Strict,
            ProposalId = options.ProposalId,
            TempWorkspacePath = CreateTempWorkspace()
        };
    }

    private IEnumerable<IHarnessRegressionScenario> SelectScenarios(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return _scenarios;

        var normalized = Normalize(category);
        return _scenarios.Where(scenario =>
            string.Equals(scenario.Category, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static HarnessRegressionScenarioResult BuildNoScenariosResult(string? category)
    {
        var now = DateTimeOffset.UtcNow;
        return new HarnessRegressionScenarioResult
        {
            Id = "harness.no_scenarios",
            Name = "No scenarios selected",
            Category = HarnessRegressionCategory.Harness,
            Status = HarnessRegressionScenarioStatus.Failed,
            Severity = HarnessRegressionSeverity.Medium,
            Required = true,
            Summary = string.IsNullOrWhiteSpace(category)
                ? "No harness regression scenarios are registered."
                : $"No harness regression scenarios matched category '{category}'.",
            StartedAtUtc = now,
            CompletedAtUtc = now
        };
    }

    private static HarnessRegressionScenarioResult NormalizeResult(
        IHarnessRegressionScenario scenario,
        HarnessRegressionScenarioResult result)
    {
        var startedAt = result.StartedAtUtc == default ? DateTimeOffset.UtcNow : result.StartedAtUtc;
        var completedAt = result.CompletedAtUtc == default ? startedAt : result.CompletedAtUtc;
        return new HarnessRegressionScenarioResult
        {
            Id = string.IsNullOrWhiteSpace(result.Id) ? scenario.Id : result.Id,
            Name = string.IsNullOrWhiteSpace(result.Name) ? scenario.Name : result.Name,
            Category = NormalizeOrFallback(result.Category, scenario.Category),
            Status = string.IsNullOrWhiteSpace(result.Status)
                ? HarnessRegressionScenarioStatus.NotApplicable
                : Normalize(result.Status),
            Severity = string.IsNullOrWhiteSpace(result.Severity)
                ? HarnessRegressionSeverity.Info
                : Normalize(result.Severity),
            Required = scenario.Required,
            Summary = result.Summary,
            Details = result.Details,
            Error = result.Error,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            DurationMs = result.DurationMs <= 0
                ? (long)Math.Max(0, (completedAt - startedAt).TotalMilliseconds)
                : result.DurationMs,
            EvidenceBundleId = result.EvidenceBundleId,
            RelatedContractId = result.RelatedContractId
        };
    }

    private static HarnessRegressionSummary BuildSummary(IReadOnlyList<HarnessRegressionScenarioResult> results)
        => new()
        {
            Total = results.Count,
            Passed = Count(results, HarnessRegressionScenarioStatus.Passed),
            Failed = Count(results, HarnessRegressionScenarioStatus.Failed),
            Skipped = Count(results, HarnessRegressionScenarioStatus.Skipped),
            Warning = Count(results, HarnessRegressionScenarioStatus.Warning),
            NotApplicable = Count(results, HarnessRegressionScenarioStatus.NotApplicable)
        };

    private static int Count(IReadOnlyList<HarnessRegressionScenarioResult> results, string status)
        => results.Count(result => string.Equals(result.Status, status, StringComparison.OrdinalIgnoreCase));

    private static string ResolveOverallStatus(
        IReadOnlyList<HarnessRegressionScenarioResult> results,
        bool strict)
    {
        if (results.Any(static result =>
                result.Required &&
                string.Equals(result.Status, HarnessRegressionScenarioStatus.Failed, StringComparison.OrdinalIgnoreCase)))
        {
            return HarnessRegressionScenarioStatus.Failed;
        }

        if (strict && results.Any(static result =>
                result.Required &&
                (string.Equals(result.Status, HarnessRegressionScenarioStatus.Warning, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result.Status, HarnessRegressionScenarioStatus.Skipped, StringComparison.OrdinalIgnoreCase))))
        {
            return HarnessRegressionScenarioStatus.Failed;
        }

        if (results.Any(static result =>
                string.Equals(result.Status, HarnessRegressionScenarioStatus.Warning, StringComparison.OrdinalIgnoreCase)))
        {
            return HarnessRegressionScenarioStatus.Warning;
        }

        return HarnessRegressionScenarioStatus.Passed;
    }

    private static IReadOnlyList<HarnessRegressionRecommendation> BuildRecommendations(
        IReadOnlyList<HarnessRegressionScenarioResult> results)
    {
        var recommendations = new List<HarnessRegressionRecommendation>();

        if (results.Any(static result =>
                result.Id == "onboarding.quickstart_config" &&
                (IsStatus(result, HarnessRegressionScenarioStatus.Skipped) ||
                 IsStatus(result, HarnessRegressionScenarioStatus.Failed))))
        {
            recommendations.Add(new HarnessRegressionRecommendation
            {
                Id = "setup.verify",
                Severity = HarnessRegressionSeverity.Medium,
                Summary = "Create or verify the OpenClaw config before trusting runtime checks.",
                Command = "openclaw setup verify --offline"
            });
        }

        if (results.Any(static result =>
                IsCategory(result, HarnessRegressionCategory.Security) &&
                (IsStatus(result, HarnessRegressionScenarioStatus.Failed) ||
                 IsStatus(result, HarnessRegressionScenarioStatus.Warning))))
        {
            recommendations.Add(new HarnessRegressionRecommendation
            {
                Id = "security.posture",
                Severity = HarnessRegressionSeverity.High,
                Summary = "Review operator security posture before widening the runtime surface.",
                Command = "openclaw admin posture"
            });
        }

        if (results.Any(static result =>
                IsCategory(result, HarnessRegressionCategory.Providers) &&
                (IsStatus(result, HarnessRegressionScenarioStatus.Failed) ||
                 IsStatus(result, HarnessRegressionScenarioStatus.Warning))))
        {
            recommendations.Add(new HarnessRegressionRecommendation
            {
                Id = "models.doctor",
                Severity = HarnessRegressionSeverity.Medium,
                Summary = "Review model profile shape and provider readiness.",
                Command = "openclaw models doctor"
            });
        }

        if (results.Any(static result =>
                IsStatus(result, HarnessRegressionScenarioStatus.Skipped) ||
                IsStatus(result, HarnessRegressionScenarioStatus.NotApplicable)))
        {
            recommendations.Add(new HarnessRegressionRecommendation
            {
                Id = "harness.docs",
                Severity = HarnessRegressionSeverity.Info,
                Summary = "Review skipped and not-applicable checks before using the report as release evidence.",
                Command = "openclaw harness test --strict"
            });
        }

        return recommendations;
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();

    private static string CreateReportId(DateTimeOffset startedAt)
        => $"hreg_{startedAt:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..40];

    private static string CreateTempWorkspace()
    {
        var path = Path.Join(Path.GetTempPath(), $"openclaw-harness-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }

    private static string NormalizeOrFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? Normalize(fallback) : Normalize(value);

    private static bool IsStatus(HarnessRegressionScenarioResult result, string status)
        => string.Equals(result.Status, status, StringComparison.OrdinalIgnoreCase);

    private static bool IsCategory(HarnessRegressionScenarioResult result, string category)
        => string.Equals(result.Category, category, StringComparison.OrdinalIgnoreCase);
}

public static class HarnessRegressionReportFormatter
{
    public static string ToJson(HarnessRegressionReport report)
        => JsonSerializer.Serialize(report, HarnessRegressionJsonContext.Default.HarnessRegressionReport);

    public static string ToText(HarnessRegressionReport report)
    {
        using var writer = new StringWriter();
        writer.WriteLine("OpenClaw Harness Regression");
        writer.WriteLine();

        foreach (var result in report.Results)
        {
            writer.WriteLine($"{Label(result.Status)} {result.Id} - {result.Summary}");
            if (!string.IsNullOrWhiteSpace(result.Error))
                writer.WriteLine($"  error: {result.Error}");
        }

        writer.WriteLine();
        writer.WriteLine("Summary:");
        writer.WriteLine(
            $"{report.Summary.Passed} passed, {report.Summary.Failed} failed, {report.Summary.Skipped} skipped, {report.Summary.Warning} warning, {report.Summary.NotApplicable} not applicable");

        if (report.Recommendations.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Next steps:");
            foreach (var recommendation in report.Recommendations)
            {
                writer.WriteLine(string.IsNullOrWhiteSpace(recommendation.Command)
                    ? $"- {recommendation.Summary}"
                    : $"- {recommendation.Command}");
            }
        }

        return writer.ToString();
    }

    private static string Label(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            HarnessRegressionScenarioStatus.Passed => "PASS",
            HarnessRegressionScenarioStatus.Failed => "FAIL",
            HarnessRegressionScenarioStatus.Skipped => "SKIP",
            HarnessRegressionScenarioStatus.Warning => "WARN",
            HarnessRegressionScenarioStatus.NotApplicable => "N/A ",
            _ => "????"
        };
}
