using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;

using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Testing;

/// <summary>
/// Recorded binding trajectory for one exported capability slot execution
/// (issue #234). Built from a persisted meta-run record — the same shape
/// `openclaw skills meta-runs --json` emits — and replayed against a real
/// executor with recorded provider observations. This verifies binding selection, not remote tool output.
/// </summary>
public sealed class CapabilityBindingReplayFixture
{
    public int SchemaVersion { get; init; } = 2;
    /// <summary>Independent snapshot of observed provider responses and the original request.</summary>
    public CapabilityBindingTrajectory Recorded { get; init; } = new();
    public string SessionId { get; init; } = "";
    public CapabilityBindingTrajectory Expected { get; init; } = new();

    /// <summary>
    /// Extracts the replay fixture from an exported meta-run record. Throws
    /// <see cref="InvalidDataException"/> when the run carries no binding trajectory.
    /// </summary>
    public static CapabilityBindingReplayFixture FromMetaRun(SessionMetaRunRecord run, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(run);
        var step = run.StepResults.FirstOrDefault(r => r.ExecutionEvidence?.CapabilityBinding is not null)
            ?? throw new InvalidDataException("The exported meta-run carries no capability binding trajectory.");
        return new CapabilityBindingReplayFixture
        {
            SessionId = sessionId,
            Expected = Clone(step.ExecutionEvidence!.CapabilityBinding!),
            Recorded = Clone(step.ExecutionEvidence!.CapabilityBinding!)
        };
    }
    private static CapabilityBindingTrajectory Clone(CapabilityBindingTrajectory value)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, CoreJsonContext.Default.CapabilityBindingTrajectory),
            CoreJsonContext.Default.CapabilityBindingTrajectory)!;

}

public sealed class CapabilityBindingReplayResult
{
    public bool Passed { get; init; }
    public string Message { get; init; } = "";
    public CapabilityBindingTrajectory? Reproduced { get; init; }
}

/// <summary>
/// Replays one recorded capability binding against a real
/// <see cref="CapabilitySlotExecutor"/> and asserts the binding is reproduced.
/// Provider data is frozen separately from expectations. No external registry,
/// network tool execution, or LLM is allowed; cache hits are warmed from the snapshot.
/// </summary>
public sealed class CapabilityBindingReplay
{
    private readonly CapabilityBindingReplayFixture _fixture;

    public CapabilityBindingReplay(CapabilityBindingReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported replay fixture schema version: {fixture.SchemaVersion}.");
        if (fixture.Expected.Binding is not ("static" or "dynamic"))
            throw new InvalidDataException("Replay fixture binding must be static or dynamic.");
        if (fixture.Expected.Binding == "static" && (fixture.Expected.Server is null || fixture.Expected.Tool is null))
            throw new InvalidDataException("A static replay fixture must carry the recorded server and tool.");
        if (fixture.Expected.Binding == "dynamic" && string.IsNullOrWhiteSpace(fixture.Expected.TaskDescription))
            throw new InvalidDataException("A dynamic replay fixture must carry the recorded task description.");
        _fixture = fixture;
    }

    public async ValueTask<CapabilityBindingReplayResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registry = new CapabilityProviderRegistry([new RecordedProvider(_fixture.Recorded)], _fixture.Recorded.Provider);
        var executor = new CapabilitySlotExecutor(registry, new CapabilityBindingCache());
        Task<OpenClaw.Agent.ToolExecutionResult> NoExecution(ITool tool, string args, CancellationToken ct)
            => Task.FromResult(new OpenClaw.Agent.ToolExecutionResult { Invocation = new() { ToolName = tool.Name, Arguments = args, Result = "replay" }, ResultText = "replay" });
        if (_fixture.Recorded.CacheHit)
            await executor.ExecuteAsync(BuildCapabilityRef(), "{}", _fixture.SessionId, cancellationToken, NoExecution);
        var result = await executor.ExecuteAsync(BuildCapabilityRef(), "{}", _fixture.SessionId, cancellationToken, NoExecution);
        var reproduced = result.BindingTrajectory;
        if (reproduced is null)
            return new CapabilityBindingReplayResult
            {
                Passed = false,
                Message = "Replayed slot produced no binding trajectory."
            };

        var mismatches = new List<string>();
        if (!string.Equals(reproduced.Provider, _fixture.Expected.Provider, StringComparison.Ordinal)) mismatches.Add("provider");
        if (!string.Equals(reproduced.SchemaFingerprint, _fixture.Expected.SchemaFingerprint, StringComparison.Ordinal)) mismatches.Add("schemaFingerprint");
        if (!string.Equals(reproduced.Binding, _fixture.Expected.Binding, StringComparison.Ordinal)) mismatches.Add("binding");
        if (!string.Equals(reproduced.Server, _fixture.Expected.Server, StringComparison.Ordinal)) mismatches.Add("server");
        if (!string.Equals(reproduced.Tool, _fixture.Expected.Tool, StringComparison.Ordinal)) mismatches.Add("tool");
        if (reproduced.CacheHit != _fixture.Expected.CacheHit) mismatches.Add("cacheHit");
        if (!CandidatesEqual(reproduced.Candidates, _fixture.Expected.Candidates)) mismatches.Add("candidates");
        if (!CandidatesEqual(reproduced.Attempted, _fixture.Expected.Attempted)) mismatches.Add("attempted");

        return mismatches.Count == 0
            ? new CapabilityBindingReplayResult { Passed = true, Message = "Recorded binding reproduced.", Reproduced = reproduced }
            : new CapabilityBindingReplayResult
            {
                Passed = false,
                Reproduced = reproduced,
                Message = $"Binding diverged from the recorded trajectory: {string.Join(", ", mismatches)}."
            };
    }

    private sealed class RecordedProvider(CapabilityBindingTrajectory recorded) : ICapabilityProvider
    {
        public string Id => recorded.Provider;
        public Task<IReadOnlyList<CapabilityCandidate>> DiscoverAsync(ResolveCapabilityRequest request, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CapabilityCandidate>>(recorded.Candidates.Select(c => new CapabilityCandidate(c.Name, "recorded", c.Rank)).ToArray());
        public Task<CapabilityTarget?> BindAsync(string target, string? tool, CancellationToken ct)
            => Task.FromResult(target == recorded.Server ? new CapabilityTarget(target, new RecordedTool(recorded.Tool!, recorded.Schema ?? "{}")) : null);
    }
    private sealed class RecordedTool(string name, string schema) : ITool
    {
        public string Name => name;
        public string Description => "Recorded capability; execution is prohibited";
        public string ParameterSchema => schema;
        public ValueTask<string> ExecuteAsync(string args, CancellationToken ct) => throw new InvalidOperationException("Replay cannot execute live tools.");
    }

    private MetaCapabilityRefDefinition BuildCapabilityRef()
    {
        var expected = _fixture.Recorded;
        if (expected.Binding == "static")
        {
            return new MetaCapabilityRefDefinition
            {
                Provider = expected.Provider,
                Binding = "static",
                Static = new MetaCapabilityStaticBinding
                {
                    Target = expected.Server!,
                    ToolName = expected.Tool!
                }
            };
        }

        return new MetaCapabilityRefDefinition
        {
            Provider = expected.Provider,
            Binding = "dynamic",
            Intent = new MetaCapabilityIntent
            {
                TaskDescription = expected.TaskDescription!,
                Keywords = (expected.KeyWords ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            },
            SelectionPolicy = expected.SelectionPolicy == "exact_name" ? "exact_name" : "first"
        };
    }

    private static bool CandidatesEqual(
        IReadOnlyList<CapabilityBindingCandidate> actual, IReadOnlyList<CapabilityBindingCandidate> expected)
        => actual.Count == expected.Count &&
           actual.Zip(expected).All(pair =>
               string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
               pair.First.Rank == pair.Second.Rank);
}
