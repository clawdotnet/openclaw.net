namespace OpenClaw.Testing;

public interface IScenarioRunner
{
    ValueTask<ScenarioRunResult> RunAsync(AgentScenario scenario, CancellationToken cancellationToken = default);
}

public interface IScenarioOracle
{
    string Name { get; }

    ValueTask<OracleResult> EvaluateAsync(
        AgentScenario scenario,
        AgentRunTrace trace,
        CancellationToken cancellationToken = default);
}

public interface IScenarioLoader
{
    ValueTask<IReadOnlyList<AgentScenario>> LoadAsync(string directoryPath, CancellationToken cancellationToken = default);
}

public interface ITraceWriter
{
    ValueTask<TraceWriteResult> WriteAsync(
        ScenarioRunReport report,
        string outputRoot,
        CancellationToken cancellationToken = default);
}
