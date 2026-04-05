using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ProcessToolTests
{
    [Fact]
    public async Task ExecuteAsync_StartWaitAndLog_TracksBackgroundProcess()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var config = new GatewayConfig();
        config.Tooling.WorkspaceRoot = workspace;

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: null,
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());
        await using var processes = new ExecutionProcessService(router, NullLogger<ExecutionProcessService>.Instance);
        var tool = new ProcessTool(processes, config.Tooling);
        var context = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_process",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_process",
                ChannelId = "websocket"
            }
        };

        var start = await tool.ExecuteAsync(
            $$"""{"action":"start","command":"{{CreateCommand()}}","timeout_seconds":30}""",
            context,
            CancellationToken.None);
        var match = Regex.Match(start, @"Started process (?<id>\S+)");
        Assert.True(match.Success);
        var processId = match.Groups["id"].Value;

        var list = await tool.ExecuteAsync("""{"action":"list"}""", context, CancellationToken.None);
        Assert.Contains(processId, list, StringComparison.Ordinal);

        var wait = await tool.ExecuteAsync($$"""{"action":"wait","process_id":"{{processId}}"}""", context, CancellationToken.None);
        Assert.Contains("completed", wait, StringComparison.OrdinalIgnoreCase);

        var log = await tool.ExecuteAsync($$"""{"action":"log","process_id":"{{processId}}"}""", context, CancellationToken.None);
        Assert.Contains("hello", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("done", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DeniesCrossSessionProcessAccess()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var config = new GatewayConfig();
        config.Tooling.WorkspaceRoot = workspace;

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: null,
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());
        await using var processes = new ExecutionProcessService(router, NullLogger<ExecutionProcessService>.Instance);
        var tool = new ProcessTool(processes, config.Tooling);

        var ownerContext = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_owner",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_owner",
                ChannelId = "websocket"
            }
        };

        var attackerContext = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_attacker",
                ChannelId = "websocket",
                SenderId = "user2"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_attacker",
                ChannelId = "websocket"
            }
        };

        var start = await tool.ExecuteAsync(
            $$"""{"action":"start","command":"{{CreateCommand()}}","timeout_seconds":30}""",
            ownerContext,
            CancellationToken.None);
        var processId = Regex.Match(start, @"Started process (?<id>\S+)").Groups["id"].Value;

        var poll = await tool.ExecuteAsync($$"""{"action":"poll","process_id":"{{processId}}"}""", attackerContext, CancellationToken.None);
        Assert.Contains("was not found", poll, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateCommand()
        => OperatingSystem.IsWindows()
            ? "echo hello && ping 127.0.0.1 -n 3 > nul && echo done"
            : "printf 'hello\\n'; sleep 1; printf 'done\\n'";
}
