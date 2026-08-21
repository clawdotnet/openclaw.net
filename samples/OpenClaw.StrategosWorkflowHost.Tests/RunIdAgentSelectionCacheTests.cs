using OpenClaw.StrategosWorkflowHost.Adapters;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class RunIdAgentSelectionCacheTests
{
    [Fact]
    public void Set_Then_TryGet_Returns_Same_Selection()
    {
        var cache = new RunIdAgentSelectionCache();
        var stored = cache.Set("run-1", "SecurityReviewer", "mock", "General");

        var retrieved = cache.TryGet("run-1", "SecurityReviewer");

        Assert.NotNull(retrieved);
        Assert.Equal("mock", retrieved!.Value.AgentId);
        Assert.Equal("General", retrieved.Value.TaskCategory);
        Assert.Equal(stored.SelectedAt, retrieved.Value.SelectedAt);
    }

    [Fact]
    public void TryGet_Returns_Null_When_Key_Missing()
    {
        var cache = new RunIdAgentSelectionCache();

        Assert.Null(cache.TryGet("nonexistent", "AnyStep"));
    }

    [Fact]
    public void Different_StepName_For_Same_RunId_Returns_Independent_Selections()
    {
        var cache = new RunIdAgentSelectionCache();
        cache.Set("run-1", "SecurityReviewer", "mock", "General");
        cache.Set("run-1", "ArchitectureReviewer", "mock-fast", "General");

        var sec = cache.TryGet("run-1", "SecurityReviewer");
        var arc = cache.TryGet("run-1", "ArchitectureReviewer");

        Assert.Equal("mock", sec!.Value.AgentId);
        Assert.Equal("mock-fast", arc!.Value.AgentId);
    }

    [Fact]
    public void Capacity_Exceeded_Evicts_Oldest_Entry_FIFO()
    {
        var cache = new RunIdAgentSelectionCache(capacity: 2);
        cache.Set("run-1", "StepA", "agent-a", "General");
        cache.Set("run-1", "StepB", "agent-b", "General");
        cache.Set("run-1", "StepC", "agent-c", "General"); // 驱逐 StepA

        Assert.Null(cache.TryGet("run-1", "StepA"));
        Assert.NotNull(cache.TryGet("run-1", "StepB"));
        Assert.NotNull(cache.TryGet("run-1", "StepC"));
        Assert.Equal(2, cache.Count);
    }
}
