using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OpenClawToolExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ApprovalRequiredWithoutCallback_DeniesExecution()
    {
        var tool = Substitute.For<ITool>();
        tool.Name.Returns("shell");
        tool.Description.Returns("shell");
        tool.ParameterSchema.Returns("""{"type":"object","properties":{"cmd":{"type":"string"}}}""");

        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: true,
            approvalRequiredTools: ["shell"],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance);

        var result = await executor.ExecuteAsync(
            "shell",
            """{"cmd":"ls"}""",
            callId: null,
            new Session
            {
                Id = "sess1",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            new TurnContext
            {
                SessionId = "sess1",
                ChannelId = "websocket"
            },
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Contains("requires approval", result.ResultText, StringComparison.OrdinalIgnoreCase);
        await tool.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }
}
