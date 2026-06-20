using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Governance;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolGovernanceTests
{
    [Fact]
    public async Task ExecuteAsync_DisabledGovernance_AllowsExecution()
    {
        var tool = new CountingTool("memory_search");
        var executor = CreateExecutor([tool], new NoopToolGovernanceService());

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal("ok", result.ResultText);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(result.Invocation.GovernanceAllowed);
        Assert.Equal(nameof(GovernanceAction.Allow), result.Invocation.GovernanceAction);
    }

    [Fact]
    public async Task ExecuteAsync_SidecarAllow_ExecutesTool()
    {
        var tool = new CountingTool("memory_search");
        var service = CreateSidecarService(new ToolGovernanceSidecarResponse
        {
            Allowed = true,
            Action = "allow",
            Reason = "allowed by test policy",
            PolicyId = "policy-1",
            RuleId = "allow-memory"
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal("ok", result.ResultText);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(result.Invocation.GovernanceAllowed);
        Assert.Equal("policy-1", result.Invocation.GovernancePolicyId);
        Assert.Equal("allow-memory", result.Invocation.GovernanceRuleId);
    }

    [Fact]
    public async Task ExecuteAsync_SidecarDeny_BlocksTool()
    {
        var tool = new CountingTool("memory_search");
        var service = CreateSidecarService(new ToolGovernanceSidecarResponse
        {
            Allowed = false,
            Action = "deny",
            Reason = "blocked by test policy",
            PolicyId = "policy-1"
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.GovernanceDenied, result.FailureCode);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.False(result.Invocation.GovernanceAllowed);
        Assert.Contains("blocked by test policy", result.ResultText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SidecarDenyAction_TakesPrecedenceOverAllowedFlag()
    {
        var tool = new CountingTool("memory_search");
        var service = CreateSidecarService(new ToolGovernanceSidecarResponse
        {
            Allowed = true,
            Action = "deny",
            Reason = "deny action wins"
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.GovernanceDenied, result.FailureCode);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.False(result.Invocation.GovernanceAllowed);
    }

    [Fact]
    public async Task ExecuteAsync_SidecarRequireApproval_UsesExistingApprovalCallback()
    {
        var tool = new CountingTool("memory_search");
        var service = CreateSidecarService(new ToolGovernanceSidecarResponse
        {
            Allowed = false,
            Action = "require_approval",
            Reason = "approval required by governance"
        });
        var executor = CreateExecutor([tool], service);
        var approvalRequested = false;

        var result = await ExecuteAsync(
            executor,
            "memory_search",
            approvalCallback: (_, _, _) =>
            {
                approvalRequested = true;
                return ValueTask.FromResult(true);
            });

        Assert.True(approvalRequested);
        Assert.Equal("ok", result.ResultText);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(nameof(GovernanceAction.RequireApproval), result.Invocation.GovernanceAction);
    }

    [Fact]
    public async Task ExecuteAsync_HighRiskGovernanceTimeout_FailsClosed()
    {
        var tool = new CountingTool("shell");
        var service = CreateTimedOutSidecarService(new ToolGovernanceConfig
        {
            Enabled = true,
            SidecarBaseUrl = "http://127.0.0.1:8088",
            TimeoutMs = 1,
            FailClosed = true,
            RequireGovernanceForHighRiskTools = true
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "shell");

        Assert.Equal(ToolResultStatuses.Blocked, result.ResultStatus);
        Assert.Equal(ToolFailureCodes.GovernanceUnavailable, result.FailureCode);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.False(result.Invocation.GovernanceAllowed);
        Assert.True(result.Invocation.GovernanceUnavailable);
        Assert.Contains("timed out", result.ResultText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LowRiskReadOnlyTimeout_CanFailOpen()
    {
        var tool = new CountingTool("memory_search");
        var service = CreateTimedOutSidecarService(new ToolGovernanceConfig
        {
            Enabled = true,
            SidecarBaseUrl = "http://127.0.0.1:8088",
            TimeoutMs = 1,
            FailClosed = true,
            FailOpenReadOnlyLowRisk = true,
            RequireGovernanceForHighRiskTools = true
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal("ok", result.ResultText);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(result.Invocation.GovernanceAllowed);
        Assert.True(result.Invocation.GovernanceUnavailable);
        Assert.Equal(nameof(GovernanceAction.AuditOnly), result.Invocation.GovernanceAction);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidRedactedArgumentsJson_KeepsExistingArguments()
    {
        var tool = new CapturingTool("memory_search");
        var service = CreateSidecarService(new ToolGovernanceSidecarResponse
        {
            Allowed = true,
            Action = "allow",
            RedactedArgumentsJson = """{"query":"unterminated"""
        });
        var executor = CreateExecutor([tool], service);

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal("ok", result.ResultText);
        Assert.Equal("""{"query":"hello","command":"echo hello"}""", tool.ArgumentsJson);
        Assert.Equal("""{"query":"hello","command":"echo hello"}""", result.Invocation.Arguments);
    }

    [Fact]
    public async Task ExecuteAsync_ResultAuditFailure_DoesNotFailSuccessfulTool()
    {
        var tool = new CountingTool("memory_search");
        var executor = CreateExecutor([tool], new ThrowingResultAuditGovernanceService());

        var result = await ExecuteAsync(executor, "memory_search");

        Assert.Equal("ok", result.ResultText);
        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public void DescriptorCatalog_CoversExpectedBuiltInAndNativeToolNames()
    {
        var expected = new[]
        {
            "shell", "read_file", "write_file", "process", "memory", "memory_search", "project_memory",
            "sessions", "session_search", "profile_read", "todo", "automation", "vision_analyze",
            "text_to_speech", "canvas_present", "canvas_hide", "canvas_navigate", "canvas_snapshot",
            "a2ui_push", "a2ui_reset", "a2ui_eval", "a2ui_create_surface", "a2ui_update_components",
            "a2ui_update_data_model", "a2ui_delete_surface", "a2ui_sync_ui_to_data", "edit_file", "apply_patch", "sessions_history",
            "sessions_send", "sessions_spawn", "session_status", "agents_list", "cron", "gateway",
            "message", "x_search", "memory_get", "profile_write", "sessions_yield", "browser",
            "external_cli", "payment", "stream_echo", "web_search", "web_fetch", "git", "code_exec",
            "image_gen", "pdf_read", "calendar", "email", "database", "inbox_zero", "home_assistant",
            "home_assistant_write", "mqtt", "mqtt_publish", "notion", "notion_write", "delegate_agent"
        };

        var missing = expected
            .Where(static toolName => !ToolGovernanceDescriptorCatalog.Contains(toolName))
            .ToArray();

        Assert.Empty(missing);
        var shell = ToolGovernanceDescriptorCatalog.Resolve("shell", "shell", new ToolActionDescriptor());
        Assert.Equal(ToolGovernanceRiskLevel.Critical, shell.RiskLevel);
        Assert.True(shell.CanExecuteCode);
        var todo = ToolGovernanceDescriptorCatalog.Resolve("todo", "todo", new ToolActionDescriptor());
        var sessions = ToolGovernanceDescriptorCatalog.Resolve("sessions", "sessions", new ToolActionDescriptor());
        var cron = ToolGovernanceDescriptorCatalog.Resolve("cron", "cron", new ToolActionDescriptor());
        Assert.False(todo.ReadOnly);
        Assert.False(sessions.ReadOnly);
        Assert.False(cron.ReadOnly);
    }

    [Theory]
    [InlineData("a2ui_create_surface")]
    [InlineData("a2ui_update_components")]
    [InlineData("a2ui_update_data_model")]
    [InlineData("a2ui_delete_surface")]
    [InlineData("a2ui_sync_ui_to_data")]
    public void DescriptorCatalog_A2UiV09ToolsAreUiWriteWithoutCodeExecution(string toolName)
    {
        var descriptor = ToolGovernanceDescriptorCatalog.Resolve(toolName, toolName, new ToolActionDescriptor());

        Assert.False(descriptor.ReadOnly);
        Assert.Contains("ui.write", descriptor.Capabilities);
        Assert.False(descriptor.CanExecuteCode);
        Assert.DoesNotContain("code.execute", descriptor.Capabilities);
    }

    private static OpenClawToolExecutor CreateExecutor(
        IReadOnlyList<ITool> tools,
        IToolGovernanceService governance)
        => new(
            tools,
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            metrics: new RuntimeMetrics(),
            logger: NullLogger.Instance,
            toolGovernance: governance);

    private static Task<ToolExecutionResult> ExecuteAsync(
        OpenClawToolExecutor executor,
        string toolName,
        ToolApprovalCallback? approvalCallback = null)
        => executor.ExecuteAsync(
            toolName,
            """{"query":"hello","command":"echo hello"}""",
            callId: "call-1",
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
            approvalCallback: approvalCallback,
            TestContext.Current.CancellationToken);

    private static HttpSidecarToolGovernanceService CreateSidecarService(ToolGovernanceSidecarResponse response)
    {
        var json = JsonSerializer.Serialize(response, CoreJsonContext.Default.ToolGovernanceSidecarResponse);
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(CreateOkResponse(json)));
        return CreateSidecarService(handler);
    }

    private static HttpResponseMessage CreateOkResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

    private static HttpSidecarToolGovernanceService CreateTimedOutSidecarService(ToolGovernanceConfig config)
    {
        var handler = new StubHttpMessageHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        return CreateSidecarService(handler, config);
    }

    private static HttpSidecarToolGovernanceService CreateSidecarService(
        HttpMessageHandler handler,
        ToolGovernanceConfig? config = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8088")
        };
        return new HttpSidecarToolGovernanceService(
            httpClient,
            config ?? new ToolGovernanceConfig
            {
                Enabled = true,
                SidecarBaseUrl = "http://127.0.0.1:8088"
            },
            NullLogger<HttpSidecarToolGovernanceService>.Instance);
    }

    private sealed class CountingTool(string name) : ITool
    {
        public int ExecutionCount { get; private set; }
        public string Name => name;
        public string Description => "Counting test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            ExecutionCount++;
            return ValueTask.FromResult("ok");
        }
    }

    private sealed class CapturingTool(string name) : ITool
    {
        public string? ArgumentsJson { get; private set; }
        public string Name => name;
        public string Description => "Capturing test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            ArgumentsJson = argumentsJson;
            return ValueTask.FromResult("ok");
        }
    }

    private sealed class ThrowingResultAuditGovernanceService : IToolGovernanceService
    {
        public ValueTask<GovernanceDecision> AuthorizeAsync(ToolGovernanceContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GovernanceDecision.Allow("allowed"));

        public ValueTask RecordResultAsync(
            ToolGovernanceContext context,
            GovernanceDecision decision,
            ToolGovernanceExecutionResult result,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("audit failed");
    }

    private sealed class StubHttpMessageHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(cancellationToken);
    }
}
