using Strategos.Abstractions;
using Strategos.Primitives;
using Strategos.Selection;

namespace OpenClaw.StrategosWorkflowHost.Tests.Stubs;

/// <summary>
/// Test double for <see cref="IAgentSelector"/>. Returns a fixed agentId from
/// SelectAgentAsync and records every RecordOutcomeAsync call for assertions.
/// </summary>
public sealed class StubAgentSelector : IAgentSelector
{
    private readonly object _gate = new();
    private readonly List<RecordedOutcome> _outcomes = new();

    public string AgentId { get; set; } = "mock";
    public bool SelectShouldFail { get; set; }

    public IReadOnlyList<RecordedOutcome> Outcomes
    {
        get { lock (_gate) { return _outcomes.ToList(); } }
    }

    public Task<Result<AgentSelection>> SelectAgentAsync(AgentSelectionContext context, CancellationToken cancellationToken = default)
    {
        if (SelectShouldFail)
        {
            return Task.FromResult(Result<AgentSelection>.Failure(Error.Create("stub-failure", "stub selection failure")));
        }

        return Task.FromResult(Result<AgentSelection>.Success(new AgentSelection
        {
            SelectedAgentId = AgentId,
            TaskCategory = TaskCategory.General,
        }));
    }

    public Task<Result<Unit>> RecordOutcomeAsync(string agentId, string taskCategory, AgentOutcome outcome, CancellationToken cancellationToken = default)
    {
        lock (_gate) { _outcomes.Add(new RecordedOutcome(agentId, taskCategory, outcome.Success)); }
        return Task.FromResult(Result<Unit>.Success(Unit.Value));
    }
}

public sealed record RecordedOutcome(string AgentId, string TaskCategory, bool Success);
