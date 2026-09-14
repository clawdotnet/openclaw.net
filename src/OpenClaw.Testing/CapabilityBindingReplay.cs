using ModelContextProtocol.Client;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Testing;

/// <summary>
/// Recorded binding trajectory for one exported capability slot execution
/// (issue #234). Built from a persisted meta-run record — the same shape
/// `openclaw skills meta-runs --json` emits — and replayed against a real
/// executor with a deterministic router harness.
/// </summary>
public sealed class CapabilityBindingReplayFixture
{
    public int SchemaVersion { get; init; } = 1;
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
            Expected = step.ExecutionEvidence!.CapabilityBinding!
        };
    }
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
/// The router behind the supplied registry must be deterministic (the same
/// candidate set the recording observed); a cache-hit fixture is reproduced
/// by seeding the supplied cache with the recorded binding, mirroring how a
/// first execution would have populated it. No LLM is involved.
/// </summary>
public sealed class CapabilityBindingReplay
{
    private readonly CapabilityBindingReplayFixture _fixture;

    public CapabilityBindingReplay(CapabilityBindingReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported replay fixture schema version: {fixture.SchemaVersion}.");
        if (fixture.Expected.Binding is not ("static" or "dynamic"))
            throw new InvalidDataException("Replay fixture binding must be static or dynamic.");
        if (fixture.Expected.Binding == "static" && (fixture.Expected.Server is null || fixture.Expected.Tool is null))
            throw new InvalidDataException("A static replay fixture must carry the recorded server and tool.");
        if (fixture.Expected.Binding == "dynamic" && string.IsNullOrWhiteSpace(fixture.Expected.TaskDescription))
            throw new InvalidDataException("A dynamic replay fixture must carry the recorded task description.");
        if (fixture.Expected.CacheHit && fixture.Expected.Binding == "static")
            throw new InvalidDataException("Static bindings have no session binding cache; a cache-hit fixture must be dynamic.");
        _fixture = fixture;
    }

    public async ValueTask<CapabilityBindingReplayResult> RunAsync(
        McpServerToolRegistry registry, CapabilityBindingCache cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();

        if (_fixture.Expected.CacheHit)
        {
            // Reproduce the recorded hit: the recording bound in an earlier
            // execution of the same session; seeding reproduces that state.
            cache.Set(_fixture.SessionId,
                CapabilityBindingCache.ComputeIntentKey(
                    _fixture.Expected.TaskDescription ?? "",
                    _fixture.Expected.KeyWords,
                    _fixture.Expected.SelectionPolicy == "exact_name" ? "ExactName" : "First"),
                _fixture.Expected.Server ?? "",
                _fixture.Expected.Tool ?? "");
        }

        var executor = new CapabilitySlotExecutor(registry, cache);
        var result = await executor.ExecuteAsync(BuildCapabilityRef(), "{}", _fixture.SessionId, cancellationToken);
        var reproduced = result.BindingTrajectory;
        if (reproduced is null)
            return new CapabilityBindingReplayResult
            {
                Passed = false,
                Message = "Replayed slot produced no binding trajectory."
            };

        var mismatches = new List<string>();
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

    private MetaCapabilityRefDefinition BuildCapabilityRef()
    {
        var expected = _fixture.Expected;
        if (expected.Binding == "static")
        {
            return new MetaCapabilityRefDefinition
            {
                Binding = "static",
                Static = new MetaCapabilityStaticBinding
                {
                    McpServerName = expected.Server!,
                    ToolName = expected.Tool!
                }
            };
        }

        return new MetaCapabilityRefDefinition
        {
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
