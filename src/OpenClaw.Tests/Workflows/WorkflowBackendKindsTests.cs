using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests.Workflows;

public sealed class WorkflowBackendKindsTests
{
    [Fact]
    public void StrategosHttp_Is_StrategosHttp_Literal()
        => Assert.Equal("strategos-http", AgentWorkflowBackendKinds.StrategosHttp);
}