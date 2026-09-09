using System.Text;
using OpenClaw.Agent;
using OpenClaw.Core.Models;

namespace OpenClaw.Testing;

/// <summary>
/// Executes a real runtime and evaluates its emitted evidence. Supply a runtime factory
/// configured with deterministic providers and isolated tools for offline regression tests.
/// Unlike ScriptedScenarioRunner, this runner never reads ScriptedTrace as execution evidence.
/// </summary>
public sealed class RuntimeScenarioRunner(
    Func<AgentScenario, IAgentRuntime> runtimeFactory,
    ToolApprovalCallback? approval = null,
    ScenarioOracleRegistry? oracles = null) : IScenarioRunner
{
    public async ValueTask<ScenarioRunResult> RunAsync(AgentScenario scenario, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var runId = "runtime_" + Guid.NewGuid().ToString("N");
        var session = new Session { Id = runId, ChannelId = "test", SenderId = "scenario-runner" };
        foreach (var context in scenario.Input.Context)
            session.History.Add(new ChatTurn { Role = "user", Content = context });
        var steps = new List<TraceStep>();
        var answer = new StringBuilder();
        var failed = false;
        var completed = false;
        async ValueTask<bool> Approve(string tool, string args, CancellationToken ct)
        {
            lock (steps)
                steps.Add(new TraceStep { Kind = TraceStepKinds.ApprovalRequest, TimestampUtc = DateTimeOffset.UtcNow, ToolName = tool, ArgumentsJson = args });
            return approval is not null && await approval(tool, args, ct);
        }
        try
        {
            var runtime = runtimeFactory(scenario);
            await foreach (var item in runtime.RunStreamingAsync(session, scenario.Input.UserMessage, cancellationToken, Approve))
            {
                if (item.Type == AgentStreamEventType.TextDelta) answer.Append(item.Content);
                if (item.Type == AgentStreamEventType.Done) completed = true;
                if (item.Type == AgentStreamEventType.Error) failed = true;
                var kind = item.Type switch
                {
                    AgentStreamEventType.ToolStart => TraceStepKinds.ToolCall,
                    AgentStreamEventType.ToolResult => TraceStepKinds.ToolResult,
                    AgentStreamEventType.Error => TraceStepKinds.Error,
                    _ => null
                };
                if (kind is not null)
                    lock (steps) steps.Add(new TraceStep { Kind = kind, TimestampUtc = DateTimeOffset.UtcNow,
                        ToolName = item.ToolName, ArgumentsJson = item.ToolArguments,
                        Result = item.Type == AgentStreamEventType.ToolResult ? item.Content : null,
                        Error = item.Type == AgentStreamEventType.Error ? item.Content : item.FailureMessage });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failed = true;
            lock (steps) steps.Add(new TraceStep { Kind = TraceStepKinds.Error, TimestampUtc = DateTimeOffset.UtcNow, Error = ex.Message });
        }
        List<TraceStep> capturedSteps;
        lock (steps) capturedSteps = steps.ToList();
        var ended = DateTimeOffset.UtcNow;
        var trace = new AgentRunTrace { RunId = runId, ScenarioId = scenario.Id, StartedAtUtc = started,
            CompletedAtUtc = ended, Steps = capturedSteps, FinalAnswer = answer.ToString(),
            Status = !failed && completed ? ScenarioRunStatuses.Completed : ScenarioRunStatuses.Failed };
        var results = new List<OracleResult>
        {
            new() { Name = "runtime-completed", Passed = !failed && completed,
                Message = !failed && completed ? "Runtime completed." : "Runtime failed or did not emit completion." },
            new() { Name = "oracles-present", Passed = scenario.Oracles.Count > 0,
                Message = "Runtime scenarios must declare outcome assertions." }
        };
        var registry = oracles ?? new ScenarioOracleRegistry();
        foreach (var definition in scenario.Oracles)
            results.Add(await registry.Create(definition).EvaluateAsync(scenario, trace, cancellationToken));
        return new ScenarioRunResult { Scenario = scenario, Trace = trace, OracleResults = results,
            Passed = results.All(result => result.Passed), StartedAtUtc = started, CompletedAtUtc = ended,
            FailureSummary = string.Join("; ", results.Where(result => !result.Passed).Select(result => result.Message)) };
    }
}
