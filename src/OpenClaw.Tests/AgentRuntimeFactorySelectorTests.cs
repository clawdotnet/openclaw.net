using OpenClaw.Agent;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentRuntimeFactorySelectorTests
{
    [Fact]
    public void Select_DefaultsToNative()
    {
        var factory = AgentRuntimeFactorySelector.Select([new StubFactory(RuntimeOrchestrator.Native)], orchestratorId: null);
        Assert.Equal(RuntimeOrchestrator.Native, factory.OrchestratorId);
    }

    [Fact]
    public void Select_MafWithoutAdapterFactory_ReturnsHelpfulError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AgentRuntimeFactorySelector.Select([new StubFactory(RuntimeOrchestrator.Native)], RuntimeOrchestrator.Maf));

        Assert.Contains("OpenClawEnableMafExperiment=true", ex.Message, StringComparison.Ordinal);
    }

    private sealed class StubFactory(string orchestratorId) : IAgentRuntimeFactory
    {
        public string OrchestratorId => orchestratorId;

        public IAgentRuntime Create(AgentRuntimeFactoryContext context)
            => throw new NotSupportedException();
    }
}
