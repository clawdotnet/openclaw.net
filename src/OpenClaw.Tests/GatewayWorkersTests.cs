using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayWorkersTests
{
    [Fact]
    public void CleanupSessionLocksOnce_DisposesRemovedOrphanedSemaphore()
    {
        var sessionManager = new SessionManager(Substitute.For<IMemoryStore>(), new GatewayConfig());
        var semaphore = new SemaphoreSlim(1, 1);
        var sessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)
        {
            ["session-1"] = semaphore
        };
        var lastUsed = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["session-1"] = DateTimeOffset.UtcNow.AddHours(-3)
        };

        GatewayWorkers.CleanupSessionLocksOnce(
            sessionManager,
            sessionLocks,
            lastUsed,
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(2),
            NullLogger.Instance);

        Assert.Empty(sessionLocks);
        Assert.Empty(lastUsed);
        Assert.Throws<ObjectDisposedException>(() => semaphore.Release());
    }

    [Fact]
    public void DisposeSessionLocks_DisposesAllRemainingSemaphores()
    {
        var first = new SemaphoreSlim(1, 1);
        var second = new SemaphoreSlim(1, 1);
        var sessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)
        {
            ["session-a"] = first,
            ["session-b"] = second
        };

        GatewayWorkers.DisposeSessionLocks(sessionLocks, NullLogger.Instance);

        Assert.Empty(sessionLocks);
        Assert.Throws<ObjectDisposedException>(() => first.Release());
        Assert.Throws<ObjectDisposedException>(() => second.Release());
    }

    [Fact]
    public async Task Start_LoopbackApprovalStillRequiresRequesterMatch()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
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
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("telegram");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry(); 
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy, 
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["telegram"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
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
        var outbound = await adapter.ReadAsync(timeout.Token);

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
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
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
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("telegram");
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
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
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
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["telegram"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
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
        var outbound = await adapter.ReadAsync(timeout.Token);
        Assert.Equal("ok", outbound.Text);
        Assert.Empty(toolApprovalService.ListPending());
        Assert.Contains(operations.RuntimeEvents.Query(new RuntimeEventQuery { Limit = 10 }), item => item.Action == "grant_consumed");
    }

    [Fact]
    public async Task Start_RouteOverrides_AreAppliedToSessionBeforeRuntimeExecution()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Routing = new RoutingConfig
            {
                Enabled = true,
                Routes = new Dictionary<string, AgentRouteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["telegram:analyst"] = new()
                    {
                        ModelOverride = "route-model",
                        SystemPrompt = "Focus on production incidents.",
                        PresetId = "readonly",
                        AllowedTools = ["session_search", "profile_read"]
                    }
                }
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
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("telegram");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        Session? capturedSession = null;
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns(callInfo =>
            {
                capturedSession = callInfo.Arg<Session>();
                return Task.FromResult("ok");
            });
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["telegram"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "telegram",
            SenderId = "analyst",
            Text = "status",
            MessageId = "msg-route"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var outbound = await adapter.ReadAsync(timeout.Token);

        Assert.Equal("ok", outbound.Text);
        Assert.NotNull(capturedSession);
        Assert.Equal("route-model", capturedSession!.ModelOverride);
        Assert.Equal("Focus on production incidents.", capturedSession.SystemPromptOverride);
        Assert.Equal("readonly", capturedSession.RoutePresetId);
        Assert.Equal(["session_search", "profile_read"], capturedSession.RouteAllowedTools);
    }

    [Fact]
    public async Task Start_StreamingVerboseFooter_IsSentBeforeAssistantDone()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var session = await sessionManager.GetOrCreateByIdAsync("sess-stream", "websocket", "ws-user", CancellationToken.None)
            ?? throw new InvalidOperationException("Failed to create session.");
        session.VerboseMode = true;
        await sessionManager.PersistAsync(session, CancellationToken.None);

        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var socket = new TestWebSocket();
        Assert.True(wsChannel.TryAddConnectionForTest("ws-user", socket, IPAddress.Loopback, useJsonEnvelope: true));
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunStreamingAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>())
            .Returns(StreamVerboseTestEvents());
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal),
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "ws-user",
            SessionId = "sess-stream",
            Text = "hello",
            MessageId = "msg-stream"
        });

        await WaitForAsync(
            () => socket.Sent.Count >= 5,
            TimeSpan.FromSeconds(2),
            "Timed out waiting for websocket stream events.");

        var envelopes = socket.Sent
            .Select(static payload => JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString() ?? "")
            .ToArray();
        var footerIndex = Array.FindIndex(envelopes, static type => string.Equals(type, "text_delta", StringComparison.Ordinal));
        var doneIndex = Array.FindIndex(envelopes, static type => string.Equals(type, "assistant_done", StringComparison.Ordinal));

        Assert.Equal("typing_start", envelopes[0]);
        Assert.Equal("assistant_chunk", envelopes[1]);
        Assert.True(footerIndex >= 0, "Expected verbose footer text_delta event.");
        Assert.True(doneIndex >= 0, "Expected assistant_done event.");
        Assert.True(footerIndex < doneIndex, "Verbose footer should be emitted before assistant_done.");
        Assert.Equal("typing_stop", envelopes[^1]);
    }

    [Fact]
    public async Task Start_ApprovalTimeout_RecordsTimedOutAuditAndRuntimeEvent()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
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
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("telegram");
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
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["telegram"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
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
        var approvalPrompt = await adapter.ReadAsync(timeout.Token);
        var finalResponse = await adapter.ReadAsync(timeout.Token);

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

    [Fact]
    public async Task Start_ManagedHeartbeatOk_SuppressesDeliveryAndRecordsStatus()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        heartbeatService.SaveConfig(CreateManagedHeartbeatConfig());
        var job = Assert.IsType<CronJobConfig>(heartbeatService.BuildManagedJob());

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("cron");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns("HEARTBEAT_OK");
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();

        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["cron"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            IsSystem = true,
            SessionId = job.SessionId,
            CronJobName = job.Name,
            ChannelId = job.ChannelId!,
            SenderId = job.RecipientId!,
            Subject = job.Subject,
            Text = job.Prompt
        });

        var status = await WaitForHeartbeatStatusAsync(heartbeatService, TimeSpan.FromSeconds(2), static item => item.Outcome == "ok");

        Assert.True(status.DeliverySuppressed);
        Assert.Equal("ok", status.Outcome);
        Assert.Equal(job.SessionId, status.SessionId);
        Assert.Null(status.LastDeliveredAtUtc);
        Assert.False(adapter.TryRead(out _));
    }

    [Fact]
    public async Task Start_ManagedHeartbeatAlert_DeliversMessageAndRecordsStatus()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        heartbeatService.SaveConfig(CreateManagedHeartbeatConfig());
        var job = Assert.IsType<CronJobConfig>(heartbeatService.BuildManagedJob());

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("cron");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns("Urgent competitor alert");
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile, 
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["cron"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            IsSystem = true,
            SessionId = job.SessionId,
            CronJobName = job.Name,
            ChannelId = job.ChannelId!,
            SenderId = job.RecipientId!,
            Subject = job.Subject,
            Text = job.Prompt
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var outbound = await adapter.ReadAsync(timeout.Token);
        var status = await WaitForHeartbeatStatusAsync(
            heartbeatService,
            TimeSpan.FromSeconds(2),
            static item => item.Outcome == "alert" && item.LastDeliveredAtUtc is not null);

        Assert.Equal("Urgent competitor alert", outbound.Text);
        Assert.Equal(job.RecipientId, outbound.RecipientId);
        Assert.Equal("alert", status.Outcome);
        Assert.False(status.DeliverySuppressed);
        Assert.Equal("Urgent competitor alert", status.MessagePreview);
        Assert.NotNull(status.LastDeliveredAtUtc);
    }

    [Fact]
    public async Task Start_ManagedHeartbeatAlert_DeliveryFailureDoesNotMarkDelivered()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        heartbeatService.SaveConfig(CreateManagedHeartbeatConfig());
        var job = Assert.IsType<CronJobConfig>(heartbeatService.BuildManagedJob());

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new ThrowingChannelAdapter("cron");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns("Urgent competitor alert");
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy, 
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["cron"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            IsSystem = true,
            SessionId = job.SessionId,
            CronJobName = job.Name,
            ChannelId = job.ChannelId!,
            SenderId = job.RecipientId!,
            Subject = job.Subject,
            Text = job.Prompt
        });

        var status = await WaitForHeartbeatStatusAsync(heartbeatService, TimeSpan.FromSeconds(5), static item => item.Outcome == "alert");

        // Allow outbound worker time to attempt delivery (which will throw)
        await Task.Delay(500);

        Assert.False(status.DeliverySuppressed);
        Assert.Null(status.LastDeliveredAtUtc);
        Assert.True(adapter.SendAttempts > 0);
    }

    [Fact]
    public async Task Start_ManagedHeartbeatError_StaysInternalAndRecordsErrorStatus()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        heartbeatService.SaveConfig(CreateManagedHeartbeatConfig());
        var job = Assert.IsType<CronJobConfig>(heartbeatService.BuildManagedJob());

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingChannelAdapter("cron");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["cron"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            IsSystem = true,
            SessionId = job.SessionId,
            CronJobName = job.Name,
            ChannelId = job.ChannelId!,
            SenderId = job.RecipientId!,
            Subject = job.Subject,
            Text = job.Prompt
        });

        var status = await WaitForHeartbeatStatusAsync(heartbeatService, TimeSpan.FromSeconds(2), static item => item.Outcome == "error");
        await Task.Delay(150);

        Assert.Equal("error", status.Outcome);
        Assert.True(status.DeliverySuppressed);
        Assert.Contains("boom", status.MessagePreview, StringComparison.Ordinal);
        Assert.False(adapter.TryRead(out _));
    }

    [Fact]
    public async Task Start_BridgedGroupMessage_UsesGroupIdForSessionReplyAndTyping()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = true,
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingBridgedChannelAdapter("whatsapp");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns("group-response");
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["whatsapp"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "whatsapp",
            SenderId = "user-1",
            Text = "hello group",
            MessageId = "msg-group",
            IsGroup = true,
            GroupId = "group-1",
            GroupName = "Test Group"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var outbound = await adapter.ReadAsync(timeout.Token);
        await WaitForAsync(
            () => adapter.TypingEvents.Count >= 2,
            TimeSpan.FromSeconds(2),
            "Timed out waiting for bridged typing lifecycle.");

        Assert.Equal("group-1", outbound.RecipientId);
        Assert.Equal("msg-group", outbound.ReplyToMessageId);
        Assert.Collection(
            adapter.TypingEvents,
            evt =>
            {
                Assert.Equal("group-1", evt.RecipientId);
                Assert.True(evt.IsTyping);
            },
            evt =>
            {
                Assert.Equal("group-1", evt.RecipientId);
                Assert.False(evt.IsTyping);
            });
        Assert.Contains("msg-group", adapter.ReadReceiptMessageIds);
        Assert.NotNull(sessionManager.TryGetActive("whatsapp", "group-1"));
        Assert.Null(sessionManager.TryGetActive("whatsapp", "user-1"));
    }

    [Fact]
    public async Task Start_BridgedTypingCleanup_OnAgentFailure_SendsTypingStop()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = true,
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingBridgedChannelAdapter("whatsapp");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["whatsapp"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "whatsapp",
            SenderId = "user-1",
            Text = "hello",
            MessageId = "msg-error"
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var outbound = await adapter.ReadAsync(timeout.Token);
        await WaitForAsync(
            () => adapter.TypingEvents.Count >= 2,
            TimeSpan.FromSeconds(2),
            "Timed out waiting for bridged typing cleanup after failure.");

        Assert.Contains("Internal error", outbound.Text, StringComparison.Ordinal);
        Assert.Collection(
            adapter.TypingEvents,
            evt => Assert.True(evt.IsTyping),
            evt => Assert.False(evt.IsTyping));
    }

    [Fact]
    public async Task Start_BridgedTypingCleanup_OnAgentCancellation_SendsTypingStop()
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-worker-tests", Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(storagePath, 4);
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false
            },
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Enabled = true,
                    DmPolicy = "open"
                }
            }
        };

        var sessionManager = new SessionManager(store, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, store, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        await using var adapter = new RecordingBridgedChannelAdapter("whatsapp");
        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.RunAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<ToolApprovalCallback?>(), Arg.Any<System.Text.Json.JsonElement?>())
            .Returns<Task<string>>(_ => throw new OperationCanceledException("simulated cancellation"));
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new OpenClaw.Core.Security.PairingManager(storagePath, NullLogger<OpenClaw.Core.Security.PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new OpenClaw.Core.Observability.RuntimeMetrics();
        var modelProfile = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var modelselectionPolicy = new DefaultModelSelectionPolicy(modelProfile);
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = new GatewayLlmExecutionService(
                config,
                modelProfile,
                modelselectionPolicy,
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
            new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["whatsapp"] = adapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        await pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            ChannelId = "whatsapp",
            SenderId = "user-1",
            Text = "hello",
            MessageId = "msg-cancel"
        });

        await WaitForAsync(
            () => adapter.TypingEvents.Count >= 2,
            TimeSpan.FromSeconds(2),
            "Timed out waiting for bridged typing cleanup after cancellation.");

        Assert.Collection(
            adapter.TypingEvents,
            evt => Assert.True(evt.IsTyping),
            evt => Assert.False(evt.IsTyping));
        Assert.False(adapter.TryRead(out _));
    }

    private static HeartbeatConfigDto CreateManagedHeartbeatConfig()
        => new()
        {
            Enabled = true,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            Tasks =
            [
                new HeartbeatTaskDto
                {
                    Id = "watch-critical-inputs",
                    TemplateKey = "custom",
                    Title = "Watch critical inputs",
                    Instruction = "Only notify on urgent changes."
                }
            ]
        };

    private static async Task<HeartbeatRunStatusDto> WaitForHeartbeatStatusAsync(
        HeartbeatService heartbeatService,
        TimeSpan timeout,
        Func<HeartbeatRunStatusDto, bool>? predicate = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var status = heartbeatService.LoadStatus();
            if (status is not null && (predicate is null || predicate(status)))
                return status;

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for managed heartbeat status.");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException(message);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> StreamVerboseTestEvents()
    {
        yield return AgentStreamEvent.TextDelta("hello");
        await Task.Yield();
        yield return AgentStreamEvent.Complete();
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

    private sealed class RecordingChannelAdapter(string channelId) : IChannelAdapter
    {
        private readonly Channel<OutboundMessage> _messages = Channel.CreateUnbounded<OutboundMessage>();

        public string ChannelId { get; } = channelId;

        public event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
            => _messages.Writer.WriteAsync(message, ct);

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask<OutboundMessage> ReadAsync(CancellationToken ct)
            => _messages.Reader.ReadAsync(ct);

        public bool TryRead(out OutboundMessage? message)
        {
            var success = _messages.Reader.TryRead(out var captured);
            message = captured;
            return success;
        }
    }

    private sealed class ThrowingChannelAdapter(string channelId) : IChannelAdapter
    {
        public string ChannelId { get; } = channelId;
        public int SendAttempts { get; private set; }

        public event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
        {
            SendAttempts++;
            throw new InvalidOperationException("simulated delivery failure");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingBridgedChannelAdapter(string channelId) : IBridgedChannelControl
    {
        private readonly Channel<OutboundMessage> _messages = Channel.CreateUnbounded<OutboundMessage>();

        public string ChannelId { get; } = channelId;
        public string? SelfId { get; init; }
        public IReadOnlyList<string> SelfIds => string.IsNullOrWhiteSpace(SelfId) ? [] : [SelfId];
        public List<(string RecipientId, bool IsTyping)> TypingEvents { get; } = [];
        public List<string> ReadReceiptMessageIds { get; } = [];

        public event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
            => _messages.Writer.WriteAsync(message, ct);

        public ValueTask SendTypingAsync(string recipientId, bool isTyping, CancellationToken ct)
        {
            TypingEvents.Add((recipientId, isTyping));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendReadReceiptAsync(string messageId, string? remoteJid, string? participant, CancellationToken ct)
        {
            ReadReceiptMessageIds.Add(messageId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask<OutboundMessage> ReadAsync(CancellationToken ct)
            => _messages.Reader.ReadAsync(ct);

        public bool TryRead(out OutboundMessage? message)
        {
            var success = _messages.Reader.TryRead(out var captured);
            message = captured;
            return success;
        }
    }
}
