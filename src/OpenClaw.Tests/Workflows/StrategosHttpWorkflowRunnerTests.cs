using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class StrategosHttpWorkflowRunnerTests
{
    [Fact]
    public void GetSummary_Reports_StrategosHttp_Kind()
    {
        var runner = new StrategosHttpWorkflowRunner(
            backendId: "strategos",
            config: NewConfig(),
            events: NewEvents(),
            logger: NullLogger<StrategosHttpWorkflowRunner>.Instance);

        var summary = runner.GetSummary();

        Assert.Equal(AgentWorkflowBackendKinds.StrategosHttp, summary.Kind);
        Assert.Equal("strategos", summary.Id);
        Assert.Equal("durable-agent-review", summary.WorkflowName);
    }

    [Fact]
    public void BackendId_Exposes_Configured_Id()
    {
        var runner = new StrategosHttpWorkflowRunner(
            backendId: "strategos",
            config: NewConfig(),
            events: NewEvents(),
            logger: NullLogger<StrategosHttpWorkflowRunner>.Instance);

        Assert.Equal("strategos", runner.BackendId);
        Assert.Equal("durable-agent-review", runner.WorkflowId);
    }

    private static RuntimeEventStore NewEvents() =>
        new(
            storagePath: Path.Combine(Path.GetTempPath(), $"openclaw-strategos-test-{Guid.NewGuid():N}"),
            logger: NullLogger<RuntimeEventStore>.Instance);

    private static WorkflowBackendConfig NewConfig() => new()
    {
        Kind = AgentWorkflowBackendKinds.StrategosHttp,
        WorkflowName = "durable-agent-review",
        BaseUrl = "http://localhost:8080/",
        Enabled = true,
    };
}