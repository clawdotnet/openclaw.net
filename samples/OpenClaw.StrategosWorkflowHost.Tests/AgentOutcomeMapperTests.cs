using Microsoft.Extensions.Logging.Abstractions;

using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class AgentOutcomeMapperTests
{
    private readonly RunIdAgentSelectionCache _cache = new();
    private readonly AgentOutcomeMapper _sut;

    public AgentOutcomeMapperTests()
    {
        _sut = new AgentOutcomeMapper(_cache, NullLogger<AgentOutcomeMapper>.Instance);
        _cache.Set("run-1", "SecurityReviewer", "mock", "General");
    }

    [Fact]
    public void Maps_Run_Completed_With_Selected_Agent_As_Success()
    {
        var entry = NewEntry(action: "run_completed", runId: "run-1", stepName: "SecurityReviewer");

        var mapped = _sut.Map(entry);

        Assert.NotNull(mapped);
        Assert.Equal("mock", mapped!.Value.AgentId);
        Assert.Equal("General", mapped.Value.TaskCategory);
        Assert.True(mapped.Value.Outcome.Success);
    }

    [Fact]
    public void Maps_Run_Failed_With_Selected_Agent_As_Failure()
    {
        var entry = NewEntry(action: "run_failed", runId: "run-1", stepName: "SecurityReviewer", severity: "warning");

        var mapped = _sut.Map(entry);

        Assert.NotNull(mapped);
        Assert.False(mapped!.Value.Outcome.Success);
    }

    [Fact]
    public void Returns_Null_For_Run_Started_And_Response_Sent()
    {
        Assert.Null(_sut.Map(NewEntry(action: "run_started", runId: "run-1", stepName: "SecurityReviewer")));
        Assert.Null(_sut.Map(NewEntry(action: "response_sent", runId: "run-1", stepName: "SecurityReviewer")));
    }

    [Fact]
    public void Returns_Null_When_Component_Is_Not_Workflow()
    {
        var entry = NewEntry(action: "run_completed", component: "tool", runId: "run-1", stepName: "SecurityReviewer");

        Assert.Null(_sut.Map(entry));
    }

    [Fact]
    public void Returns_Null_When_Metadata_Missing_RunId_Or_StepName()
    {
        var noRunId = NewEntry(action: "run_completed", stepName: "SecurityReviewer");
        noRunId.Metadata!.Remove("runId");
        var noStep = NewEntry(action: "run_completed", runId: "run-1");
        noStep.Metadata!.Remove("stepName");

        Assert.Null(_sut.Map(noRunId));
        Assert.Null(_sut.Map(noStep));
    }

    [Fact]
    public void Returns_Null_When_Cache_Miss_For_RunId_StepName()
    {
        // The cache only has ("run-1","SecurityReviewer"). An event for a
        // different (runId, stepName) must not crash — the webhook receiver
        // relies on a null return to skip silently.
        var entry = NewEntry(action: "run_completed", runId: "run-999", stepName: "ArchitectureReviewer");

        Assert.Null(_sut.Map(entry));
    }

    private static RuntimeEventEntry NewEntry(
        string action,
        string? component = "workflow",
        string runId = "run-1",
        string stepName = "SecurityReviewer",
        string severity = "info")
        => new()
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            Component = component!,
            Action = action,
            Severity = severity,
            Summary = "test",
            Metadata = new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["stepName"] = stepName,
            },
        };
}