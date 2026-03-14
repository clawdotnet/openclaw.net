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

    [Fact]
    public async Task ExecuteAsync_SandboxPreferWithoutProvider_FallsBackToLocalExecution()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Prefer, "local-result");
        var executor = CreateExecutor([tool]);

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Equal("local-result", result.ResultText);
        Assert.Equal(1, tool.LocalExecutionCount);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxPreferWithProvider_UsesSandbox()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Prefer, "local-result");
        var sandbox = Substitute.For<IToolSandbox>();
        sandbox.ExecuteAsync(Arg.Any<SandboxExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SandboxResult
            {
                ExitCode = 0,
                Stdout = "sandbox-result",
                Stderr = ""
            });

        var executor = CreateExecutor(
            [tool],
            sandbox,
            new GatewayConfig
            {
                Sandbox = new SandboxConfig
                {
                    Provider = SandboxProviderNames.OpenSandbox,
                    Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                    {
                        ["sandbox_echo"] = new()
                        {
                            Template = "ghcr.io/example/sandbox:latest"
                        }
                    }
                }
            });

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Equal("formatted:sandbox-result", result.ResultText);
        Assert.Equal(0, tool.LocalExecutionCount);
        await sandbox.Received(1).ExecuteAsync(
            Arg.Is<SandboxExecutionRequest>(request =>
                request.Template == "ghcr.io/example/sandbox:latest" &&
                request.LeaseKey == "sess1:sandbox_echo"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SandboxPreferWhenProviderUnavailable_FallsBackToLocalExecution()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Prefer, "local-result");
        var sandbox = Substitute.For<IToolSandbox>();
        sandbox.ExecuteAsync(Arg.Any<SandboxExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<SandboxResult>>(_ => throw new ToolSandboxUnavailableException("sandbox unavailable"));

        var executor = CreateExecutor(
            [tool],
            sandbox,
            CreateSandboxedGatewayConfig("sandbox_echo", ToolSandboxMode.Prefer));

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Equal("local-result", result.ResultText);
        Assert.Equal(1, tool.LocalExecutionCount);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxRequireWithoutProvider_FailsClosed()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Require, "local-result");
        var executor = CreateExecutor(
            [tool],
            toolSandbox: null,
            config: CreateSandboxedGatewayConfig("sandbox_echo", ToolSandboxMode.Require));

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Contains("requires sandboxing", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tool.LocalExecutionCount);
    }

    [Fact]
    public async Task ExecuteAsync_HookDenialPreventsSandboxExecution()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Require, "local-result");
        var sandbox = Substitute.For<IToolSandbox>();
        var hook = Substitute.For<IToolHook>();
        hook.Name.Returns("deny");
        hook.BeforeExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [hook],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            config: CreateSandboxedGatewayConfig("sandbox_echo", ToolSandboxMode.Require),
            toolSandbox: sandbox);

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Contains("denied by hook", result.ResultText, StringComparison.OrdinalIgnoreCase);
        await sandbox.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    private static OpenClawToolExecutor CreateExecutor(
        IReadOnlyList<ITool> tools,
        IToolSandbox? toolSandbox = null,
        GatewayConfig? config = null)
        => new(
            tools,
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            config: config,
            toolSandbox: toolSandbox);

    private static Session CreateSession()
        => new()
        {
            Id = "sess1",
            ChannelId = "websocket",
            SenderId = "user1"
        };

    private static TurnContext CreateTurnContext()
        => new()
        {
            SessionId = "sess1",
            ChannelId = "websocket"
        };

    private static GatewayConfig CreateSandboxedGatewayConfig(string toolName, ToolSandboxMode mode)
        => new()
        {
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                DefaultTTL = 300,
                Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                {
                    [toolName] = new()
                    {
                        Mode = mode.ToString(),
                        Template = "ghcr.io/example/sandbox:latest"
                    }
                }
            }
        };

    private sealed class SandboxCapableEchoTool(ToolSandboxMode defaultMode, string localResult) : ITool, ISandboxCapableTool
    {
        public int LocalExecutionCount { get; private set; }

        public string Name => "sandbox_echo";

        public string Description => "Echo tool for sandbox executor tests.";

        public string ParameterSchema => """{"type":"object","properties":{"value":{"type":"string"}}}""";

        public ToolSandboxMode DefaultSandboxMode => defaultMode;

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            LocalExecutionCount++;
            return ValueTask.FromResult(localResult);
        }

        public SandboxExecutionRequest CreateSandboxRequest(string argumentsJson)
            => new()
            {
                Command = "echo",
                Arguments = ["sandbox"]
            };

        public string FormatSandboxResult(string argumentsJson, SandboxResult result)
            => "formatted:" + result.Stdout;
    }
}
