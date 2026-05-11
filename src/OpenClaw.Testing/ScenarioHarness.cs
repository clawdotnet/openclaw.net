namespace OpenClaw.Testing;

public sealed class ScriptedScenarioRunner(ScenarioOracleRegistry? oracleRegistry = null) : IScenarioRunner
{
    private readonly ScenarioOracleRegistry _oracleRegistry = oracleRegistry ?? new ScenarioOracleRegistry();

    public async ValueTask<ScenarioRunResult> RunAsync(
        AgentScenario scenario,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var trace = BuildTrace(scenario, startedAt);
        var oracleResults = new List<OracleResult>();

        if (scenario.ScriptedTrace is null)
        {
            oracleResults.Add(new OracleResult
            {
                Passed = false,
                Name = "scripted-trace-present",
                Message = "ScriptedScenarioRunner requires scriptedTrace evidence for deterministic scenario execution."
            });
        }

        if (scenario.Oracles.Count == 0)
        {
            oracleResults.Add(new OracleResult
            {
                Passed = false,
                Name = "oracles-present",
                Message = "Scenario declared no oracles. A scenario that only executes is not a meaningful agent test."
            });
        }
        else
        {
            foreach (var definition in scenario.Oracles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oracle = _oracleRegistry.Create(definition);
                oracleResults.Add(await oracle.EvaluateAsync(scenario, trace, cancellationToken));
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        var failed = oracleResults.Where(static result => !result.Passed).Select(static result => result.Message).ToArray();

        return new ScenarioRunResult
        {
            Scenario = scenario,
            Trace = trace,
            OracleResults = oracleResults,
            Passed = failed.Length == 0,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            FailureSummary = failed.Length == 0 ? null : string.Join(" ", failed)
        };
    }

    private static AgentRunTrace BuildTrace(AgentScenario scenario, DateTimeOffset startedAt)
    {
        var runId = $"scenario_{Guid.NewGuid():N}";
        var script = scenario.ScriptedTrace;
        var steps = new List<TraceStep>();
        var offset = 0;

        if (script is not null)
        {
            foreach (var step in script.Steps)
            {
                var timestamp = step.TimestampUtc == default
                    ? startedAt.AddMilliseconds(offset++ * 10d)
                    : step.TimestampUtc;

                steps.Add(new TraceStep
                {
                    Kind = step.Kind,
                    TimestampUtc = timestamp,
                    Message = step.Message,
                    ToolName = step.ToolName,
                    ArgumentsJson = step.ArgumentsJson,
                    Result = step.Result,
                    Error = step.Error,
                    StatePath = step.StatePath,
                    From = step.From,
                    To = step.To,
                    ApprovalId = step.ApprovalId,
                    Metadata = CopyMetadata(step.Metadata)
                });
            }
        }
        else
        {
            steps.Add(new TraceStep
            {
                Kind = TraceStepKinds.Event,
                TimestampUtc = startedAt,
                Message = "No scripted trace was provided."
            });
        }

        var completedAt = DateTimeOffset.UtcNow;
        return new AgentRunTrace
        {
            RunId = runId,
            ScenarioId = scenario.Id,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            Steps = steps,
            FinalAnswer = script?.FinalAnswer,
            Status = script?.Status ?? ScenarioRunStatuses.Failed,
            Metadata = script is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : CopyMetadata(script.Metadata)
        };
    }

    private static Dictionary<string, string> CopyMetadata(Dictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
}

public sealed class ScenarioHarness(IScenarioRunner? runner = null)
{
    private const int MaxRunIdLength = 64;

    private readonly IScenarioRunner _runner = runner ?? new ScriptedScenarioRunner();

    public async ValueTask<ScenarioRunReport> RunAsync(
        IReadOnlyList<AgentScenario> scenarios,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<ScenarioRunResult>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _runner.RunAsync(scenario, cancellationToken));
        }

        var completedAt = DateTimeOffset.UtcNow;
        var runId = CreateRunId(startedAt);
        var highOrCriticalFailures = results.Count(static result =>
            !result.Passed &&
            result.Scenario.Risk is ScenarioRisk.High or ScenarioRisk.Critical);
        var report = new ScenarioRunReport
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            Summary = new ScenarioRunSummary
            {
                Total = results.Count,
                Passed = results.Count(static result => result.Passed),
                Failed = results.Count(static result => !result.Passed),
                HighOrCriticalFailures = highOrCriticalFailures
            },
            Results = results
        };

        return new ScenarioRunReport
        {
            RunId = report.RunId,
            StartedAtUtc = report.StartedAtUtc,
            CompletedAtUtc = report.CompletedAtUtc,
            Summary = report.Summary,
            Results = report.Results,
            Markdown = ScenarioMarkdownReport.Build(report)
        };
    }

    private static string CreateRunId(DateTimeOffset startedAt)
    {
        var candidate = $"agent_scenarios_{startedAt:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        return candidate[..Math.Min(candidate.Length, MaxRunIdLength)];
    }
}
