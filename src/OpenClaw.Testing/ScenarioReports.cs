using System.Text;
using System.Text.Json;

namespace OpenClaw.Testing;

public static class ScenarioMarkdownReport
{
    public static string Build(ScenarioRunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Agent Scenario Test Report");
        sb.AppendLine();
        sb.AppendLine($"- Run ID: `{report.RunId}`");
        sb.AppendLine($"- Started: `{report.StartedAtUtc:O}`");
        sb.AppendLine($"- Completed: `{report.CompletedAtUtc:O}`");
        sb.AppendLine($"- Summary: {report.Summary.Passed}/{report.Summary.Total} passed, {report.Summary.Failed} failed");
        sb.AppendLine($"- High/Critical failures: {report.Summary.HighOrCriticalFailures}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Risk | Status | Failed Oracles |");
        sb.AppendLine("| --- | --- | --- | --- |");

        foreach (var result in report.Results.OrderBy(static result => result.Scenario.Id, StringComparer.OrdinalIgnoreCase))
        {
            var failed = result.OracleResults
                .Where(static oracle => !oracle.Passed)
                .Select(static oracle => oracle.Name)
                .ToArray();
            sb.AppendLine($"| `{Escape(result.Scenario.Id)}` | {result.Scenario.Risk} | {(result.Passed ? "passed" : "failed")} | {Escape(failed.Length == 0 ? "" : string.Join(", ", failed))} |");
        }

        foreach (var result in report.Results.Where(static result => !result.Passed))
        {
            sb.AppendLine();
            sb.AppendLine($"## {result.Scenario.Id}");
            sb.AppendLine();
            sb.AppendLine(result.Scenario.Title);
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.FailureSummary))
                sb.AppendLine(result.FailureSummary);

            foreach (var oracle in result.OracleResults.Where(static oracle => !oracle.Passed))
            {
                sb.AppendLine();
                sb.AppendLine($"- `{oracle.Name}`: {oracle.Message}");
                foreach (var evidence in oracle.Evidence)
                    sb.AppendLine($"  - `{evidence}`");
            }
        }

        return sb.ToString();
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);
}

public sealed class JsonTraceWriter : ITraceWriter
{
    public async ValueTask<TraceWriteResult> WriteAsync(
        ScenarioRunReport report,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = Path.Combine(Path.GetFullPath(outputRoot), report.RunId);
        var traceDirectory = Path.Combine(runDirectory, "traces");
        Directory.CreateDirectory(traceDirectory);

        var tracePaths = new List<string>();
        foreach (var result in report.Results)
        {
            var tracePath = Path.Combine(traceDirectory, $"{SafeFileName(result.Scenario.Id)}.trace.json");
            await File.WriteAllTextAsync(
                tracePath,
                JsonSerializer.Serialize(result.Trace, ScenarioJsonContext.Default.AgentRunTrace),
                cancellationToken);
            tracePaths.Add(tracePath);
        }

        var reportWithMarkdown = string.IsNullOrWhiteSpace(report.Markdown)
            ? new ScenarioRunReport
            {
                RunId = report.RunId,
                StartedAtUtc = report.StartedAtUtc,
                CompletedAtUtc = report.CompletedAtUtc,
                Summary = report.Summary,
                Results = report.Results,
                Markdown = ScenarioMarkdownReport.Build(report)
            }
            : report;
        var resultsPath = Path.Combine(runDirectory, "results.json");
        var reportPath = Path.Combine(runDirectory, "report.md");

        await File.WriteAllTextAsync(
            resultsPath,
            JsonSerializer.Serialize(reportWithMarkdown, ScenarioJsonContext.Default.ScenarioRunReport),
            cancellationToken);
        await File.WriteAllTextAsync(reportPath, reportWithMarkdown.Markdown ?? "", cancellationToken);

        return new TraceWriteResult
        {
            RunId = report.RunId,
            DirectoryPath = runDirectory,
            ResultsPath = resultsPath,
            ReportPath = reportPath,
            TracePaths = tracePaths
        };
    }

    public static async ValueTask<ScenarioRunReport?> LoadLatestReportAsync(
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var latest = FindLatestRunDirectory(outputRoot);
        if (latest is null)
            return null;

        var resultsPath = Path.Combine(latest, "results.json");
        if (!File.Exists(resultsPath))
            return null;

        await using var stream = File.OpenRead(resultsPath);
        return await JsonSerializer.DeserializeAsync(
            stream,
            ScenarioJsonContext.Default.ScenarioRunReport,
            cancellationToken);
    }

    public static string? FindLatestRunDirectory(string outputRoot)
    {
        if (!Directory.Exists(outputRoot))
            return null;

        return Directory
            .EnumerateDirectories(outputRoot)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string SafeFileName(string value)
    {
        var fileName = string.IsNullOrWhiteSpace(value) ? "scenario" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return fileName;
    }
}
