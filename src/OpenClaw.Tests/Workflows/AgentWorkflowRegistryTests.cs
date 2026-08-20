using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class AgentWorkflowRegistryTests
{
    [Fact]
    public void Registry_Instantiates_MafDurableHttp_Runner_For_Default_Kind()
    {
        var registry = NewRegistry(("default", new WorkflowBackendConfig
        {
            Kind = AgentWorkflowBackendKinds.MafDurableHttp,
            WorkflowName = "default-wf",
            BaseUrl = "http://localhost:9000/",
        }));

        var summary = Assert.Single(registry.List());
        Assert.Equal(AgentWorkflowBackendKinds.MafDurableHttp, summary.Kind);
    }

    [Fact]
    public void Registry_Instantiates_StrategosHttp_Runner_For_StrategosHttp_Kind()
    {
        var registry = NewRegistry(("strategos", new WorkflowBackendConfig
        {
            Kind = AgentWorkflowBackendKinds.StrategosHttp,
            WorkflowName = "durable-agent-review",
            BaseUrl = "http://localhost:8080/",
        }));

        var summary = Assert.Single(registry.List());
        Assert.Equal(AgentWorkflowBackendKinds.StrategosHttp, summary.Kind);
        Assert.Equal("strategos", summary.Id);
    }

    [Fact]
    public void Registry_Rejects_Unknown_Kind()
    {
        var config = NewConfig(("bogus", new WorkflowBackendConfig
        {
            Kind = "weird-thing",
            WorkflowName = "bogus",
            BaseUrl = "http://localhost:9000/",
        }));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AgentWorkflowRegistry(config, NewEvents(), NullLoggerFactory.Instance));
        Assert.Contains("weird-thing", ex.Message);
    }

    private static AgentWorkflowRegistry NewRegistry(params (string id, WorkflowBackendConfig cfg)[] backends)
    {
        var config = NewConfig(backends);
        return new AgentWorkflowRegistry(config, NewEvents(), NullLoggerFactory.Instance);
    }

    private static GatewayConfig NewConfig(params (string id, WorkflowBackendConfig cfg)[] backends)
    {
        var wf = new WorkflowsConfig { Enabled = true };
        foreach (var (id, cfg) in backends)
            wf.Backends[id] = cfg;
        return new GatewayConfig { Workflows = wf };
    }

    private static RuntimeEventStore NewEvents() =>
        new(
            storagePath: Path.Combine(Path.GetTempPath(), $"openclaw-registry-test-{Guid.NewGuid():N}"),
            logger: NullLogger<RuntimeEventStore>.Instance);
}