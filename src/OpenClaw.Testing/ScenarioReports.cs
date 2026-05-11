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
        sb.AppendLine($"- Run ID: {InlineCode(report.RunId)}");
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
            sb.AppendLine($"| {EscapeTableCell(InlineCode(result.Scenario.Id))} | {result.Scenario.Risk} | {(result.Passed ? "passed" : "failed")} | {EscapeTableCell(failed.Length == 0 ? "" : string.Join(", ", failed))} |");
        }

        foreach (var result in report.Results.Where(static result => !result.Passed))
        {
            sb.AppendLine();
            sb.AppendLine($"## {EscapeHeading(result.Scenario.Id)}");
            sb.AppendLine();
            sb.AppendLine(EscapeParagraph(result.Scenario.Title));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.FailureSummary))
                sb.AppendLine(EscapeParagraph(result.FailureSummary));

            foreach (var oracle in result.OracleResults.Where(static oracle => !oracle.Passed))
            {
                sb.AppendLine();
                sb.AppendLine($"- {InlineCode(oracle.Name)}: {EscapeParagraph(oracle.Message)}");
                foreach (var evidence in oracle.Evidence)
                    sb.AppendLine($"  - {InlineCode(evidence)}");
            }
        }

        return sb.ToString();
    }

    private static string InlineCode(string value)
    {
        var normalized = NormalizeInline(value);
        var delimiter = new string('`', MaxBacktickRun(normalized) + 1);
        var padding = normalized.StartsWith('`') || normalized.EndsWith('`') ? " " : "";
        return $"{delimiter}{padding}{normalized}{padding}{delimiter}";
    }

    private static string EscapeTableCell(string value)
        => NormalizeInline(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string EscapeHeading(string value)
        => NormalizeInline(value)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeParagraph(string value)
        => NormalizeInline(value);

    private static string NormalizeInline(string value)
        => value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');

    private static int MaxBacktickRun(string value)
    {
        var max = 0;
        var current = 0;
        foreach (var ch in value)
        {
            if (ch == '`')
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }

        return max;
    }
}

public sealed class JsonTraceWriter : ITraceWriter
{
    public async ValueTask<TraceWriteResult> WriteAsync(
        ScenarioRunReport report,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        ValidateRunId(report.RunId);
        var outputRootFullPath = Path.GetFullPath(outputRoot);
        var runDirectory = Path.Join(outputRootFullPath, report.RunId);
        var traceDirectory = Path.Join(runDirectory, "traces");
        Directory.CreateDirectory(traceDirectory);

        var tracePaths = new List<string>();
        foreach (var result in report.Results)
        {
            var traceFileName = GetTraceFileName(result.Scenario.Id);
            var tracePath = Path.Join(traceDirectory, traceFileName);
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
        var resultsPath = Path.Join(runDirectory, "results.json");
        var reportPath = Path.Join(runDirectory, "report.md");

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
        => (await LoadLatestReportWithDirectoryAsync(outputRoot, cancellationToken))?.Report;

    public static async ValueTask<LatestScenarioRunReport?> LoadLatestReportWithDirectoryAsync(
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var latest = FindLatestRunDirectory(outputRoot);
        if (latest is null)
            return null;

        var resultsPath = Path.Join(latest, "results.json");
        if (!File.Exists(resultsPath))
            return null;

        await using var stream = File.OpenRead(resultsPath);
        var report = await JsonSerializer.DeserializeAsync(
            stream,
            ScenarioJsonContext.Default.ScenarioRunReport,
            cancellationToken);
        return report is null
            ? null
            : new LatestScenarioRunReport(report, latest);
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

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) ||
            Path.IsPathRooted(runId) ||
            !string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal))
        {
            throw new ArgumentException("Run id must be a relative file-system segment.", nameof(runId));
        }
    }

    private static string GetTraceFileName(string scenarioId)
    {
        var traceFileName = Path.GetFileName($"{SafeFileName(scenarioId)}.trace.json");
        if (string.IsNullOrWhiteSpace(traceFileName) || Path.IsPathRooted(traceFileName))
            throw new ArgumentException("Trace file name must be a file-system leaf name.", nameof(scenarioId));
        return traceFileName;
    }

    private static string SafeFileName(string value)
    {
        var fileName = string.IsNullOrWhiteSpace(value) ? "scenario" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return fileName;
    }
}

public sealed record LatestScenarioRunReport(ScenarioRunReport Report, string DirectoryPath);
