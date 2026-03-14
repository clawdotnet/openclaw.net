using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayWorkersTests
{
    [Fact]
    public async Task Start_LoopbackApprovalStillRequiresRequesterMatch()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var agentRuntime = Substitute.For<IAgentRuntime>();
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                providerRegistry,
                providerPolicies,
                runtimeEvents,
                runtimeMetrics,
                new OpenClaw.Core.Observability.ProviderUsageTracker(),
                NullLogger<GatewayLlmExecutionService>.Instance),
            PluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance),
            ApprovalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance),
            RuntimeEvents = runtimeEvents,
            OperatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance),
            WebhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance),
            ActorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance),
            SessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance)
        };
        var approval = toolApprovalService.Create("sess1", "telegram", "owner", "shell", """{"cmd":"ls"}""", TimeSpan.FromMinutes(5));

        using var lifetime = new TestApplicationLifetime();
        GatewayWorkers.Start(
            lifetime,
            NullLogger.Instance,
            workerCount: 1,
            isNonLoopbackBind: false,
            sessionManager,
            new ConcurrentDictionary<string, SemaphoreSlim>(),
            new ConcurrentDictionary<string, DateTimeOffset>(),
            pipeline,
            middleware,
            wsChannel,
            agentRuntime,
            new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal),
            config,
            cronScheduler: null,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "telegram",
            SenderId = "attacker",
            Text = $"/approve {approval.ApprovalId} yes",
            MessageId = "msg1"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var outbound = await pipeline.OutboundReader.ReadAsync(timeout.Token);

        Assert.Contains("not valid", outbound.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(toolApprovalService.GetPending(approval.ApprovalId));
        Assert.Contains(
            operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = 10, Component = "approval" }),
            item => item.Action == "decision_rejected"
                && (item.Metadata ?? new Dictionary<string, string>()).GetValueOrDefault("approvalId") == approval.ApprovalId);
    }

    [Fact]
    public async Task Start_ReusableApprovalGrant_BypassesPendingApprovalCreation()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns(async callInfo =>
            {
                var callback = callInfo.ArgAt<ToolApprovalCallback?>(3);
                if (callback is not null)
                    await callback("shell", """{"cmd":"ls"}""", CancellationToken.None);
                return "ok";
            });
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var approvalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        approvalGrants.AddOrUpdate(new ToolApprovalGrant
        {
            Id = "grant_1",
            Scope = "sender_tool_window",
            ChannelId = "telegram",
            SenderId = "owner",
            ToolName = "shell",
            GrantedBy = "tester",
            GrantSource = "test"
        });
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                providerRegistry,
                providerPolicies,
                runtimeEvents,
                runtimeMetrics,
                new OpenClaw.Core.Observability.ProviderUsageTracker(),
                NullLogger<GatewayLlmExecutionService>.Instance),
            PluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance),
            ApprovalGrants = approvalGrants,
            RuntimeEvents = runtimeEvents,
            OperatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance),
            WebhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance),
            ActorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance),
            SessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance)
        };

        using var lifetime = new TestApplicationLifetime();
        GatewayWorkers.Start(
            lifetime,
            NullLogger.Instance,
            workerCount: 1,
            isNonLoopbackBind: false,
            sessionManager,
            new ConcurrentDictionary<string, SemaphoreSlim>(),
            new ConcurrentDictionary<string, DateTimeOffset>(),
            pipeline,
            middleware,
            wsChannel,
            agentRuntime,
            new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal),
            config,
            cronScheduler: null,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "telegram",
            SenderId = "owner",
            Text = "hello",
            MessageId = "msg-grant"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var outbound = await pipeline.OutboundReader.ReadAsync(timeout.Token);
        Assert.Equal("ok", outbound.Text);
        Assert.Empty(toolApprovalService.ListPending());
        Assert.Contains(operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = 10 }), item => item.Action == "grant_consumed");
    }

    [Fact]
    public async Task Start_ApprovalTimeout_RecordsTimedOutAuditAndRuntimeEvent()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                RequireToolApproval = true,
                ApprovalRequiredTools = ["shell"],
                ToolApprovalTimeoutSeconds = 5
            },
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns(async callInfo =>
            {
                var callback = callInfo.ArgAt<ToolApprovalCallback?>(3)
                    ?? throw new InvalidOperationException("Approval callback was not supplied.");
                var approved = await callback("shell", """{"cmd":"ls"}""", CancellationToken.None);
                return approved ? "approved" : "timed-out";
            });

        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                providerRegistry,
                providerPolicies,
                runtimeEvents,
                runtimeMetrics,
                new OpenClaw.Core.Observability.ProviderUsageTracker(),
                NullLogger<GatewayLlmExecutionService>.Instance),
            PluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance),
            ApprovalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance),
            RuntimeEvents = runtimeEvents,
            OperatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance),
            WebhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance),
            ActorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance),
            SessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance)
        };

        using var lifetime = new TestApplicationLifetime();
        GatewayWorkers.Start(
            lifetime,
            NullLogger.Instance,
            workerCount: 1,
            isNonLoopbackBind: false,
            sessionManager,
            new ConcurrentDictionary<string, SemaphoreSlim>(),
            new ConcurrentDictionary<string, DateTimeOffset>(),
            pipeline,
            middleware,
            wsChannel,
            agentRuntime,
            new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal),
            config,
            cronScheduler: null,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "telegram",
            SenderId = "owner",
            Text = "hello",
            MessageId = "msg-timeout"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var approvalPrompt = await pipeline.OutboundReader.ReadAsync(timeout.Token);
        var finalResponse = await pipeline.OutboundReader.ReadAsync(timeout.Token);

        Assert.Contains("Tool approval required", approvalPrompt.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("timed-out", finalResponse.Text);
        Assert.Empty(toolApprovalService.ListPending());

        var history = approvalAuditStore.Query(new ApprovalHistoryQuery { Limit = 10 });
        Assert.Contains(history, item => item.EventType == "created");
        Assert.Contains(history, item => item.EventType == "decision" && item.DecisionSource == "timeout" && item.Approved is false);
        Assert.Contains(
            operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = 10, Component = "approval" }),
            item => item.Action == "timed_out");
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose() => _stopping.Cancel();
    }
}
