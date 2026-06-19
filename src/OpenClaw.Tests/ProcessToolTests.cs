using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
            TestContext.Current.CancellationToken);
        var match = Regex.Match(start, @"Started process (?<id>\S+)");
        Assert.True(match.Success);
        var processId = match.Groups["id"].Value;

        var list = await tool.ExecuteAsync("""{"action":"list"}""", context, TestContext.Current.CancellationToken);
        Assert.Contains(processId, list, StringComparison.Ordinal);

        var wait = await tool.ExecuteAsync($$"""{"action":"wait","process_id":"{{processId}}"}""", context, TestContext.Current.CancellationToken);
        Assert.Contains("completed", wait, StringComparison.OrdinalIgnoreCase);

        var log = await tool.ExecuteAsync($$"""{"action":"log","process_id":"{{processId}}"}""", context, TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
        var processId = Regex.Match(start, @"Started process (?<id>\S+)").Groups["id"].Value;

        var poll = await tool.ExecuteAsync($$"""{"action":"poll","process_id":"{{processId}}"}""", attackerContext, TestContext.Current.CancellationToken);
        Assert.Contains("was not found", poll, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBackendForProcess_UsesSandboxPolicyWhenConfigured()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                AllowShell = true
            },
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                Endpoint = "http://sandbox.example",
                Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                {
                    ["process"] = new()
                    {
                        Mode = nameof(ToolSandboxMode.Require),
                        Template = "ghcr.io/example/process:latest"
                    }
                }
            }
        };

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: Substitute.For<IToolSandbox>(),
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());

        var route = router.ResolveBackendForProcess();

        Assert.Equal("opensandbox", route.BackendName);
        Assert.Equal(ToolSandboxMode.Require, route.SandboxMode);
        Assert.Equal("ghcr.io/example/process:latest", route.Template);
    }

    [Fact]
    public void IsIsolatedProcessBackend_AllowsDockerButRejectsUnsafeBackends()
    {
        var config = new GatewayConfig
        {
            Execution = new ExecutionConfig
            {
                DefaultBackend = "local",
                Profiles = new Dictionary<string, ExecutionBackendProfileConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["local"] = new()
                    {
                        Type = ExecutionBackendType.Local
                    },
                    ["docker-safe"] = new()
                    {
                        Type = ExecutionBackendType.Docker,
                        Image = "alpine:latest"
                    }
                }
            },
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                Endpoint = "http://sandbox.example"
            }
        };

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: Substitute.For<IToolSandbox>(),
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());

        Assert.True(router.IsIsolatedProcessBackend("docker-safe"));
        Assert.False(router.IsIsolatedProcessBackend("local"));
        Assert.False(router.IsIsolatedProcessBackend("opensandbox"));
    }

    [Fact]
    public async Task ExecutionProcessService_StartAsync_RejectsOpenSandboxBackgroundProcesses()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                WorkspaceRoot = workspace
            },
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                Endpoint = "http://sandbox.example",
                Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                {
                    ["process"] = new()
                    {
                        Mode = nameof(ToolSandboxMode.Prefer)
                    }
                }
            }
        };

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: Substitute.For<IToolSandbox>(),
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());
        await using var processes = new ExecutionProcessService(router, NullLogger<ExecutionProcessService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => processes.StartAsync(new ExecutionProcessStartRequest
        {
            ToolName = "process",
            BackendName = "",
            OwnerSessionId = "sess_owner",
            OwnerChannelId = "websocket",
            OwnerSenderId = "user1",
            Command = "echo",
            Arguments = ["hello"],
            WorkingDirectory = workspace,
            TimeoutSeconds = 10,
            RequireWorkspace = true
        }, TestContext.Current.CancellationToken));

        Assert.Contains("does not support long-running background processes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutionProcessService_RetainsBoundedCompletedHistory()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var config = new GatewayConfig();
        config.Tooling.WorkspaceRoot = workspace;

        var router = new ToolExecutionRouter(
            config,
            toolSandbox: null,
            NullLoggerFactory.Instance.CreateLogger<ToolExecutionRouter>());
        var metrics = new RuntimeMetrics();
        await using var processes = new ExecutionProcessService(router, NullLogger<ExecutionProcessService>.Instance, metrics);

        for (var i = 0; i < 70; i++)
        {
            var handle = await processes.StartAsync(new ExecutionProcessStartRequest
            {
                ToolName = "process",
                BackendName = "local",
                OwnerSessionId = "sess_owner",
                OwnerChannelId = "websocket",
                OwnerSenderId = "user1",
                Command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? ["/c", "echo done"] : ["-lc", "printf 'done\\n'"],
                WorkingDirectory = workspace,
                TimeoutSeconds = 10,
                RequireWorkspace = true
            }, TestContext.Current.CancellationToken);
            await processes.WaitAsync(handle.ProcessId, "sess_owner", TestContext.Current.CancellationToken);
        }

        var retained = processes.List("sess_owner");

        Assert.True(retained.Count <= 64, $"Expected retained process history to be capped, but found {retained.Count} entries.");
        Assert.True(metrics.ProcessHistoryEvictions > 0);
        Assert.True(metrics.RetainedProcesses <= 64);
    }

    private static string CreateCommand()
        => OperatingSystem.IsWindows()
            ? "echo hello && ping 127.0.0.1 -n 3 > nul && echo done"
            : "printf 'hello\\n'; sleep 1; printf 'done\\n'";
}
