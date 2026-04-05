#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MafGatewayIntegrationTests
{
    [Fact]
    public Task GatewayWorkers_MafRuntime_Jit_PreservesToolContextAcrossTurns()
        => ExecuteGatewayWorkerFlowAsync(GatewayRuntimeMode.Jit, dynamicCodeSupported: true);

    [Fact]
    public Task GatewayWorkers_MafRuntime_Aot_PreservesToolContextAcrossTurns()
        => ExecuteGatewayWorkerFlowAsync(GatewayRuntimeMode.Aot, dynamicCodeSupported: false);

    [Fact]
    public Task MafRuntime_RunStreamingAsync_YieldsTextDeltasAndPersistsAssistantTurn()
        => ExecuteStreamingFlowAsync(GatewayRuntimeMode.Jit, dynamicCodeSupported: true);

    [Fact]
    public Task MafRuntime_RunStreamingAsync_Aot_YieldsTextDeltasAndPersistsAssistantTurn()
        => ExecuteStreamingFlowAsync(GatewayRuntimeMode.Aot, dynamicCodeSupported: false);

    [Fact]
    public Task MafRuntime_RunStreamingAsync_Jit_EmitsToolEvents()
        => ExecuteStreamingToolFlowAsync(GatewayRuntimeMode.Jit, dynamicCodeSupported: true);

    [Fact]
    public Task MafRuntime_RunStreamingAsync_Aot_EmitsToolEvents()
        => ExecuteStreamingToolFlowAsync(GatewayRuntimeMode.Aot, dynamicCodeSupported: false);

    private static async Task ExecuteGatewayWorkerFlowAsync(
        GatewayRuntimeMode effectiveMode,
        bool dynamicCodeSupported)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        await using var channelAdapter = new CapturingChannelAdapter("telegram");
        await using var services = BuildServices(CreateConfig(storagePath, effectiveMode));

        var config = services.GetRequiredService<GatewayConfig>();
        var memoryStore = new FileMemoryStore(storagePath, 8);
        var sessionManager = new SessionManager(memoryStore, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, memoryStore, sessionManager, NullLogger<HeartbeatService>.Instance);
        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new OpenClaw.Channels.WebSocketChannel(config.WebSocket);
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var pairingManager = new PairingManager(storagePath, NullLogger<PairingManager>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var fakeChatClient = new MafToolFlowChatClient();
        providerRegistry.RegisterDefault(config.Llm, fakeChatClient);

        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);

        var operations = new RuntimeOperationsState
        {
            ProviderPolicies = providerPolicies,
            ProviderRegistry = providerRegistry,
            LlmExecution = llmExecution,
            PluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance),
            ApprovalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance),
            RuntimeEvents = runtimeEvents,
            OperatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance),
            WebhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance),
            ActorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance),
            SessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance)
        };

        var agentRuntime = CreateMafRuntime(
            services,
            config,
            memoryStore,
            effectiveMode,
            dynamicCodeSupported,
            runtimeMetrics,
            providerUsage,
            llmExecution,
            fakeChatClient,
            [new EchoTool()]);

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
                ["telegram"] = channelAdapter
            },
            config,
            cronScheduler: null,
            heartbeatService,
            toolApprovalService,
            approvalAuditStore,
            pairingManager,
            commandProcessor,
            operations);

        try
        {
            await pipeline.InboundWriter.WriteAsync(new InboundMessage
            {
                ChannelId = "telegram",
                SenderId = "user-maf",
                Text = "Use the echo tool on first turn.",
                MessageId = "msg-1"
            });

            var firstOutbound = await channelAdapter.ReadAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Tool result: echo:first turn", firstOutbound.Text);

            await pipeline.InboundWriter.WriteAsync(new InboundMessage
            {
                ChannelId = "telegram",
                SenderId = "user-maf",
                Text = "What did the tool return earlier?",
                MessageId = "msg-2"
            });

            var secondOutbound = await channelAdapter.ReadAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Earlier tool result was echo:first turn.", secondOutbound.Text);

            var session = await sessionManager.LoadAsync("telegram:user-maf", CancellationToken.None);
            Assert.NotNull(session);
            Assert.Contains(session!.History, turn => turn.Content == "[tool_use]");
            Assert.Contains(
                session.History.SelectMany(static turn => turn.ToolCalls ?? []),
                call => call.ToolName == "echo_tool" && call.Result == "echo:first turn");

            Assert.True(fakeChatClient.SawPriorToolSummaryOnFollowUp);
            Assert.True(fakeChatClient.CallCount >= 3);
        }
        finally
        {
            lifetime.StopApplication();
            fakeChatClient.Dispose();
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static async Task ExecuteStreamingFlowAsync(
        GatewayRuntimeMode effectiveMode,
        bool dynamicCodeSupported)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-stream-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        await using var services = BuildServices(CreateConfig(storagePath, effectiveMode));
        var config = services.GetRequiredService<GatewayConfig>();
        var memoryStore = new FileMemoryStore(storagePath, 8);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var fakeChatClient = new MafStreamingChatClient();
        providerRegistry.RegisterDefault(config.Llm, fakeChatClient);

        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);

        var runtime = CreateMafRuntime(
            services,
            config,
            memoryStore,
            effectiveMode,
            dynamicCodeSupported,
            runtimeMetrics,
            providerUsage,
            llmExecution,
            fakeChatClient,
            []);

        var session = new Session
        {
            Id = "stream-session",
            ChannelId = "openai-http",
            SenderId = "stream-user"
        };

        var events = new List<AgentStreamEvent>();

        try
        {
            await foreach (var evt in runtime.RunStreamingAsync(session, "Stream a reply.", CancellationToken.None))
                events.Add(evt);

            Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "Hello ");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "streaming");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.Done);

            Assert.Contains(session.History, turn => turn.Role == "user" && turn.Content == "Stream a reply.");
            Assert.Contains(session.History, turn => turn.Role == "assistant" && turn.Content == "Hello streaming");
            Assert.Contains(providerUsage.Snapshot(), snapshot => snapshot.Requests == 1);
            Assert.True(fakeChatClient.StreamCallCount >= 1);
        }
        finally
        {
            fakeChatClient.Dispose();
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static async Task ExecuteStreamingToolFlowAsync(
        GatewayRuntimeMode effectiveMode,
        bool dynamicCodeSupported)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-stream-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        await using var services = BuildServices(CreateConfig(storagePath, effectiveMode));
        var config = services.GetRequiredService<GatewayConfig>();
        var memoryStore = new FileMemoryStore(storagePath, 8);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var fakeChatClient = new MafStreamingToolChatClient();
        providerRegistry.RegisterDefault(config.Llm, fakeChatClient);

        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);

        var runtime = CreateMafRuntime(
            services,
            config,
            memoryStore,
            effectiveMode,
            dynamicCodeSupported,
            runtimeMetrics,
            providerUsage,
            llmExecution,
            fakeChatClient,
            [new StreamingSmokeEchoTool()]);

        var session = new Session
        {
            Id = "stream-tool-session",
            ChannelId = "openai-http",
            SenderId = "stream-tool-user"
        };

        var events = new List<AgentStreamEvent>();

        try
        {
            await foreach (var evt in runtime.RunStreamingAsync(session, "Stream a tool response.", CancellationToken.None))
                events.Add(evt);

            Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolStart && e.ToolName == "stream_echo");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolDelta && e.ToolName == "stream_echo" && e.Content == "a");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolDelta && e.ToolName == "stream_echo" && e.Content == "b");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolDelta && e.ToolName == "stream_echo" && e.Content == "c");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolResult && e.ToolName == "stream_echo" && e.Content == "abc");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "done");
            Assert.Contains(events, e => e.Type == AgentStreamEventType.Done);

            Assert.Contains(session.History, turn => turn.Content == "[tool_use]");
            Assert.Contains(
                session.History.SelectMany(static turn => turn.ToolCalls ?? []),
                call => call.ToolName == "stream_echo" && call.Result == "abc");
            Assert.Contains(session.History, turn => turn.Role == "assistant" && turn.Content == "done");
            Assert.Contains(providerUsage.Snapshot(), snapshot => snapshot.Requests >= 2);
            Assert.True(fakeChatClient.StreamCallCount >= 2);
        }
        finally
        {
            fakeChatClient.Dispose();
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static GatewayConfig CreateConfig(string storagePath, GatewayRuntimeMode effectiveMode)
        => new()
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath,
                MaxHistoryTurns = 12
            },
            Llm = new LlmProviderConfig
            {
                Provider = "test-maf",
                Model = "maf-test-model",
                ApiKey = "test-key"
            },
            Runtime = new RuntimeConfig
            {
                Mode = effectiveMode == GatewayRuntimeMode.Aot ? "aot" : "jit",
                Orchestrator = RuntimeOrchestrator.Maf
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                ToolTimeoutSeconds = 10
            },
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    DmPolicy = "open"
                }
            }
        };

    private static ServiceProvider BuildServices(GatewayConfig config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.AddOptions();
        services.Configure<MafOptions>(_ => { });
        services.AddSingleton<MafTelemetryAdapter>();
        services.AddSingleton<MafSessionStateStore>();
        services.AddSingleton<MafAgentFactory>();
        services.AddSingleton<IAgentRuntimeFactory, MafAgentRuntimeFactory>();
        return services.BuildServiceProvider();
    }

    private static IAgentRuntime CreateMafRuntime(
        ServiceProvider services,
        GatewayConfig config,
        IMemoryStore memoryStore,
        GatewayRuntimeMode effectiveMode,
        bool dynamicCodeSupported,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILlmExecutionService llmExecutionService,
        IChatClient chatClient,
        IReadOnlyList<ITool> tools)
    {
        var factory = services.GetRequiredService<IAgentRuntimeFactory>();
        return factory.Create(new AgentRuntimeFactoryContext
        {
            Services = services,
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = config.Runtime.Mode,
                EffectiveMode = effectiveMode,
                DynamicCodeSupported = dynamicCodeSupported
            },
            ChatClient = chatClient,
            Tools = tools,
            MemoryStore = memoryStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            LlmExecutionService = llmExecutionService,
            Skills = [],
            SkillsConfig = config.Skills,
            WorkspacePath = null,
            PluginSkillDirs = [],
            Logger = NullLogger.Instance,
            Hooks = [],
            RequireToolApproval = false,
            ApprovalRequiredTools = [],
            IsContractTokenBudgetExceeded = null,
            IsContractRuntimeBudgetExceeded = null,
            RecordContractTurnUsage = null,
            AppendContractSnapshot = null
        });
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo_tool";

        public string Description => "Echo a provided input string.";

        public string ParameterSchema => """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""";

        public ValueTask<string> ExecuteAsync(string arguments, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(arguments);
            var input = doc.RootElement.GetProperty("input").GetString() ?? string.Empty;
            return ValueTask.FromResult("echo:" + input);
        }
    }

    private sealed class MafToolFlowChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public bool SawPriorToolSummaryOnFollowUp { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            CallCount++;

            var allText = string.Join(
                "\n",
                messageList.SelectMany(static message => message.Contents)
                    .Select(static content => content.ToString()));
            var lastUser = messageList.LastOrDefault(static message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            var hasToolResultMessage = messageList.Any(static message => message.Role == ChatRole.Tool);

            if (lastUser.Contains("What did the tool return earlier", StringComparison.OrdinalIgnoreCase))
            {
                SawPriorToolSummaryOnFollowUp =
                    allText.Contains("[Previous tool calls:", StringComparison.Ordinal)
                    && allText.Contains("echo:first turn", StringComparison.Ordinal);

                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, "Earlier tool result was echo:first turn.")
                ]));
            }

            if (hasToolResultMessage)
            {
                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, "Tool result: echo:first turn")
                ]));
            }

            if (lastUser.Contains("Use the echo tool", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, new AIContent[]
                    {
                        new FunctionCallContent(
                            "call_echo_1",
                            "echo_tool",
                            new Dictionary<string, object?> { ["input"] = "first turn" })
                    })
                ]));
            }

            return Task.FromResult(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, "Unexpected request.")
            ]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await GetResponseAsync(messages, options, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class MafStreamingChatClient : IChatClient
    {
        public int StreamCallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "fallback")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            StreamCallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "streaming");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class MafStreamingToolChatClient : IChatClient
    {
        public int StreamCallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = options;
            StreamCallCount++;

            var hasToolResult = messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any();

            await Task.Yield();

            if (!hasToolResult)
            {
                var call = new FunctionCallContent(
                    "call_stream_1",
                    "stream_echo",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["chunks"] = new[] { "a", "b", "c" }
                    });
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { call });
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingChannelAdapter(string channelId) : IChannelAdapter
    {
        private readonly Channel<OutboundMessage> _messages = Channel.CreateUnbounded<OutboundMessage>();

        public string ChannelId => channelId;

        public event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
            => new(_messages.Writer.WriteAsync(message, ct).AsTask());

        public async Task<OutboundMessage> ReadAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _messages.Reader.ReadAsync(cts.Token);
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
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
#endif
