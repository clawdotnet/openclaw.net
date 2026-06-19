using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

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
            TestContext.Current.CancellationToken);

        Assert.Contains("requires sandboxing", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tool.LocalExecutionCount);
    }

    [Theory]
    [InlineData("websocket", "Provide a summary of AI news")]
    [InlineData("cli", "Summarize this local repository")]
    public async Task ExecuteAsync_SandboxRequireWithProviderNone_RunsLocallyAndLogsResolution(
        string channelId,
        string prompt)
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Require, "local-result");
        var logger = new ListLogger();
        var executor = CreateExecutor(
            [tool],
            toolSandbox: null,
            config: new GatewayConfig
            {
                Sandbox = new SandboxConfig
                {
                    Provider = SandboxProviderNames.None,
                    Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                    {
                        ["sandbox_echo"] = new()
                        {
                            Mode = nameof(ToolSandboxMode.Require),
                            Template = "ghcr.io/example/sandbox:latest"
                        }
                    }
                }
            },
            logger);

        var result = await executor.ExecuteAsync(
            "sandbox_echo",
            """{"value":"hi"}""",
            callId: null,
            CreateSession(channelId, prompt),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("local-result", result.ResultText);
        Assert.Equal(1, tool.LocalExecutionCount);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("Sandbox mode resolved for tool sandbox_echo", StringComparison.Ordinal) &&
                       message.Contains("provider=None", StringComparison.OrdinalIgnoreCase) &&
                       message.Contains("configured=Require", StringComparison.Ordinal) &&
                       message.Contains("effective=None", StringComparison.Ordinal) &&
                       message.Contains("global sandbox off switch", StringComparison.OrdinalIgnoreCase));
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
            TestContext.Current.CancellationToken);

        Assert.Contains("denied by hook", result.ResultText, StringComparison.OrdinalIgnoreCase);
        await sandbox.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_OperatorAuthFailure_IsClassifiedAndPersisted()
    {
        var tool = new ThrowingTool("This action requires operator authentication on the current surface.");
        var executor = CreateExecutor([tool]);

        var result = await executor.ExecuteAsync(
            "auth_bound",
            """{"action":"restricted"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.OperatorAuthRequired, result.FailureCode);
        Assert.Contains("operator authentication", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolFailureCodes.OperatorAuthRequired, result.Invocation.FailureCode);
        Assert.Equal(ToolResultStatuses.Blocked, result.Invocation.ResultStatus);
        Assert.Contains("browser session or operator token", result.NextStep ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeCapabilityFailure_PreservesStructuredClassification()
    {
        var tool = new ThrowingTool("Execution backend 'docker' is not configured.");
        var executor = CreateExecutor([tool]);

        var result = await executor.ExecuteAsync(
            "auth_bound",
            """{"action":"restricted"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.RuntimeCapabilityUnavailable, result.FailureCode);
        Assert.Equal(ToolFailureCodes.RuntimeCapabilityUnavailable, result.Invocation.FailureCode);
        Assert.Contains("execution backend", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Configure the required execution backend or sandbox", result.NextStep ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LocalExecutionPolicyFailure_UsesStructuredFailureCode()
    {
        var tool = new LocalExecutionDisabledTool();
        var executor = CreateExecutor([tool]);

        var result = await executor.ExecuteAsync(
            tool.Name,
            """{"action":"restricted"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.RuntimeCapabilityUnavailable, result.FailureCode);
        Assert.Equal(ToolFailureCodes.RuntimeCapabilityUnavailable, result.Invocation.FailureCode);
        Assert.Equal(tool.LocalExecutionUnavailableMessage, result.ResultText);
        Assert.Contains("Configure the required execution backend or sandbox", result.NextStep ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolFails_PassesFailureContextToInterceptors()
    {
        var tool = new ThrowingTool("Execution backend 'docker' is not configured.");
        var interceptor = new RecordingInterceptor();
        var executor = CreateExecutor([tool], interceptors: [interceptor]);

        var result = await executor.ExecuteAsync(
            "auth_bound",
            """{"action":"restricted"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.NotNull(interceptor.Context);
        Assert.True(interceptor.Context.Value.IsError);
        Assert.Equal(1, interceptor.Context.Value.ExitCode);
        Assert.Contains("execution backend", interceptor.Context.Value.RawOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_RouteToolsDisabled_BlocksDirectToolExecution()
    {
        var tool = new SandboxCapableEchoTool(ToolSandboxMode.Prefer, "local-result");
        var executor = CreateExecutor([tool]);
        var session = CreateSession();
        session.RouteToolsDisabled = true;

        var result = await executor.ExecuteAsync(
            tool.Name,
            """{"value":"hi"}""",
            callId: null,
            session,
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.PresetBlocked, result.FailureCode);
        Assert.Contains("disabled for this routed turn", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tool.LocalExecutionCount);
    }

    [Fact]
    public async Task ExecuteAsync_MetaInvokeWithRuntimeExecutor_UsesRuntimeCallback()
    {
        var tool = new RecordingTool("meta_invoke", "tool-fallback");
        var callbackCalls = 0;

        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            metaInvokeExecutor: (_, skill, input, _) =>
            {
                callbackCalls++;
                return Task.FromResult($"meta:{skill}:{input}");
            });

        var result = await executor.ExecuteAsync(
            "meta_invoke",
            """{"skill":"meta-research","input":"hello"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("meta:meta-research:hello", result.ResultText);
        Assert.Equal(1, callbackCalls);
        Assert.Equal(0, tool.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MetaInvokeWithInvalidArguments_FallsBackToToolImplementation()
    {
        var tool = new RecordingTool("meta_invoke", "tool-fallback");
        var callbackCalls = 0;

        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            metaInvokeExecutor: (_, _, _, _) =>
            {
                callbackCalls++;
                return Task.FromResult("meta-callback");
            });

        var result = await executor.ExecuteAsync(
            "meta_invoke",
            """{"input":"hello"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("tool-fallback", result.ResultText);
        Assert.Equal(0, callbackCalls);
        Assert.Equal(1, tool.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MetaInvokeWhenRuntimePolicyDisablesMeta_BlocksInvocation()
    {
        var tool = new RecordingTool("meta_invoke", "tool-fallback");
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            metaInvokeExecutor: (_, _, _, _) =>
                Task.FromResult("Error: Meta skill invocation is disabled by runtime policy."));

        var result = await executor.ExecuteAsync(
            "meta_invoke",
            """{"skill":"meta-research","input":"hello"}""",
            callId: null,
            CreateSession(),
            CreateTurnContext(),
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.RuntimeCapabilityUnavailable, result.FailureCode);
        Assert.Contains("disabled by runtime policy", result.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tool.CallCount);
    }

    private static OpenClawToolExecutor CreateExecutor(
        IReadOnlyList<ITool> tools,
        IToolSandbox? toolSandbox = null,
        GatewayConfig? config = null,
        ILogger? logger = null,
        IReadOnlyList<IToolResultInterceptor>? interceptors = null)
        => new(
            tools,
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: logger ?? NullLogger.Instance,
            config: config,
            toolSandbox: toolSandbox,
            interceptors: interceptors);

    private static Session CreateSession(string channelId = "websocket", string? prompt = null)
        => new()
        {
            Id = "sess1",
            ChannelId = channelId,
            SenderId = "user1",
            History = string.IsNullOrWhiteSpace(prompt)
                ? []
                : [new ChatTurn { Role = "user", Content = prompt }]
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

    private sealed class ThrowingTool(string message) : ITool
    {
        public string Name => "auth_bound";
        public string Description => "Throws a classified operator auth error.";
        public string ParameterSchema => """{"type":"object","properties":{"action":{"type":"string"}}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
            => throw new InvalidOperationException(message);
    }

    private sealed class LocalExecutionDisabledTool : ITool, IToolLocalExecutionPolicy
    {
        public string Name => "policy_blocked";
        public string Description => "Tool that cannot run locally.";
        public string ParameterSchema => """{"type":"object","properties":{"action":{"type":"string"}}}""";
        public bool LocalExecutionSupported => false;
        public string LocalExecutionUnavailableFailureCode => ToolFailureCodes.RuntimeCapabilityUnavailable;
        public string LocalExecutionUnavailableMessage => "Error: Local execution is unavailable for this policy tool.";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
            => throw new InvalidOperationException("Local execution should not be invoked.");
    }

    private sealed class RecordingInterceptor : IToolResultInterceptor
    {
        public int Order => 0;
        public string Name => "recording";
        public ReductionContext? Context { get; private set; }

        public ValueTask<string> InterceptAsync(ReductionContext context, CancellationToken ct)
        {
            Context = context;
            return new ValueTask<string>(context.RawOutput);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingTool(string name, string result) : ITool
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public string Name => name;
        public string Description => "Recording tool";
        public string ParameterSchema => """{"type":"object","properties":{"value":{"type":"string"}}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(result);
        }
    }
}
