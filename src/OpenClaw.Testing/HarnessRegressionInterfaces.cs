namespace OpenClaw.Testing;

public interface IHarnessRegressionScenario
{
    string Id { get; }
    string Name { get; }
    string Category { get; }
    bool Required { get; }

    ValueTask<HarnessRegressionScenarioResult> RunAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken = default);
}

internal abstract class HarnessRegressionScenarioBase(
    string id,
    string name,
    string category,
    bool required = true) : IHarnessRegressionScenario
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Category { get; } = category;
    public bool Required { get; } = required;

    public async ValueTask<HarnessRegressionScenarioResult> RunAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await EvaluateAsync(context, cancellationToken);
            return Complete(result, startedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Complete(Failed(
                "Scenario threw an exception.",
                error: ex.Message,
                details: ex.ToString()), startedAt);
        }
    }

    protected abstract ValueTask<HarnessRegressionScenarioResult> EvaluateAsync(
        HarnessRegressionContext context,
        CancellationToken cancellationToken);

    protected static HarnessRegressionScenarioResult Passed(
        string summary,
        string? details = null,
        string severity = HarnessRegressionSeverity.Info)
        => Build(HarnessRegressionScenarioStatus.Passed, summary, details, severity);

    protected static HarnessRegressionScenarioResult Failed(
        string summary,
        string? details = null,
        string? error = null,
        string severity = HarnessRegressionSeverity.High)
        => Build(HarnessRegressionScenarioStatus.Failed, summary, details, severity, error);

    protected static HarnessRegressionScenarioResult Skipped(
        string summary,
        string? details = null,
        string severity = HarnessRegressionSeverity.Info)
        => Build(HarnessRegressionScenarioStatus.Skipped, summary, details, severity);

    protected static HarnessRegressionScenarioResult Warning(
        string summary,
        string? details = null,
        string severity = HarnessRegressionSeverity.Medium)
        => Build(HarnessRegressionScenarioStatus.Warning, summary, details, severity);

    protected static HarnessRegressionScenarioResult NotApplicable(
        string summary,
        string? details = null,
        string severity = HarnessRegressionSeverity.Info)
        => Build(HarnessRegressionScenarioStatus.NotApplicable, summary, details, severity);

    private static HarnessRegressionScenarioResult Build(
        string status,
        string summary,
        string? details,
        string severity,
        string? error = null)
        => new()
        {
            Status = status,
            Summary = summary,
            Details = details,
            Severity = severity,
            Error = error
        };

    private HarnessRegressionScenarioResult Complete(
        HarnessRegressionScenarioResult result,
        DateTimeOffset startedAt)
    {
        var completedAt = DateTimeOffset.UtcNow;
        return new HarnessRegressionScenarioResult
        {
            Id = Id,
            Name = Name,
            Category = Normalize(Category),
            Status = string.IsNullOrWhiteSpace(result.Status)
                ? HarnessRegressionScenarioStatus.NotApplicable
                : Normalize(result.Status),
            Severity = string.IsNullOrWhiteSpace(result.Severity)
                ? HarnessRegressionSeverity.Info
                : Normalize(result.Severity),
            Required = Required,
            Summary = result.Summary,
            Details = result.Details,
            Error = result.Error,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            DurationMs = (long)Math.Max(0, (completedAt - startedAt).TotalMilliseconds),
            EvidenceBundleId = result.EvidenceBundleId,
            RelatedContractId = result.RelatedContractId
        };
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();
}
