using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Integrations;
using OpenClaw.Gateway.Pipeline;
using OpenClaw.Payments.Core;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayRuntimeLifecycleTests
{
    [Fact]
    public async Task GatewayRuntimeShutdownCoordinator_StopAsync_RunsRegisteredCleanupsInReverseOrderOnce()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig(root);
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
                IsNonLoopbackBind = false
            };
            var runtime = CreateRuntime(config, root);
            var coordinator = new GatewayRuntimeShutdownCoordinator(NullLogger<GatewayRuntimeShutdownCoordinator>.Instance);
            var calls = new List<string>();

            coordinator.AttachRuntime(startup, runtime);
            coordinator.RegisterAsyncCleanup("first", _ =>
            {
                calls.Add("first");
                return ValueTask.CompletedTask;
            });
            coordinator.RegisterAsyncCleanup("second", _ =>
            {
                calls.Add("second");
                return ValueTask.CompletedTask;
            });

            await coordinator.StopAsync(TestContext.Current.CancellationToken);
            await coordinator.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["second", "first"], calls);
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public async Task GatewayRuntimeShutdownCoordinator_StopAsync_RespectsHostCancellationDuringDrain()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig(root);
            config.GracefulShutdownSeconds = 30;
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
                IsNonLoopbackBind = false
            };
            var runtime = CreateRuntime(config, root);
            var heldLock = new SemaphoreSlim(0, 1);
            runtime.SessionLocks["held"] = heldLock;

            var coordinator = new GatewayRuntimeShutdownCoordinator(NullLogger<GatewayRuntimeShutdownCoordinator>.Instance);
            coordinator.AttachRuntime(startup, runtime);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StopAsync(cts.Token));
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public async Task SkillWatcherService_DisposeAsync_WaitsForInFlightReload()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var skillsRoot = Path.Combine(root, "skills");
            Directory.CreateDirectory(skillsRoot);

            var config = new SkillsConfig();
            config.Load.ExtraDirs = [skillsRoot];

            var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReload = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var agentRuntime = Substitute.For<IAgentRuntime>();
            agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
            agentRuntime.LoadedSkillNames.Returns([]);
            agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                reloadStarted.TrySetResult();
                return releaseReload.Task;
            });

            var service = new SkillWatcherService(config, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);
            service.Start(TestContext.Current.CancellationToken);
            service.NotifySkillChanged();

            await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var disposeTask = service.DisposeAsync().AsTask();
            Assert.False(disposeTask.IsCompleted);

            releaseReload.SetResult([]);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public async Task SkillWatcherService_HostStoppingToken_CancelsReloadToken()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(root);
        try
        {
            var skillsRoot = Path.Combine(root, "skills");
            Directory.CreateDirectory(skillsRoot);

            var config = new SkillsConfig();
            config.Load.ExtraDirs = [skillsRoot];

            var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloadCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken capturedToken = default;

            var agentRuntime = Substitute.For<IAgentRuntime>();
            agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
            agentRuntime.LoadedSkillNames.Returns([]);
            agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>()).Returns(callInfo =>
            {
                capturedToken = callInfo.Arg<CancellationToken>();
                reloadStarted.TrySetResult();
                return WaitForCancellationAsync(capturedToken, reloadCanceled);
            });

            using var stoppingCts = new CancellationTokenSource();
            var service = new SkillWatcherService(config, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);
            service.Start(stoppingCts.Token);
            service.NotifySkillChanged();

            await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(capturedToken.CanBeCanceled && capturedToken.IsCancellationRequested);

            stoppingCts.Cancel();
            await reloadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.DisposeAsync();
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public async Task MdnsDiscoveryService_Start_IsIdempotent_AndDisposeStopsInjectedListeners()
    {
        var listenerFactoryCalls = 0;
        var activeListeners = 0;
        var listenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MdnsDiscoveryService(
            new MdnsConfig { Enabled = true },
            gatewayPort: 18789,
            authRequired: false,
            NullLogger<MdnsDiscoveryService>.Instance,
            listenerFactory: (addressFamily, _, _) =>
            {
                Interlocked.Increment(ref listenerFactoryCalls);
                return new System.Net.Sockets.UdpClient(addressFamily);
            },
            listenLoop: async (_, _, ct) =>
            {
                Interlocked.Increment(ref activeListeners);
                listenerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                finally
                {
                    Interlocked.Decrement(ref activeListeners);
                }
            });

        using var cts = new CancellationTokenSource();
        service.Start(cts.Token);
        service.Start(cts.Token);

        await listenerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(2, listenerFactoryCalls);
        Assert.Equal(0, Volatile.Read(ref activeListeners));
    }

    [Fact]
    public async Task TailscaleService_StartAndDispose_AreIdempotent()
    {
        var availabilityChecks = 0;
        var commands = new List<string>();
        var service = new TailscaleService(
            new TailscaleConfig
            {
                Enabled = true,
                Mode = "serve"
            },
            gatewayPort: 18789,
            NullLogger<TailscaleService>.Instance,
            availabilityCheck: _ =>
            {
                Interlocked.Increment(ref availabilityChecks);
                return Task.FromResult(true);
            },
            commandRunner: (arguments, _) =>
            {
                commands.Add(arguments);
                return Task.FromResult((0, string.Empty));
            });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, availabilityChecks);
        Assert.Equal(
            [
                "serve --bg --https=443 https+insecure://localhost:18789",
                "serve off"
            ],
            commands);
    }

    private static GatewayConfig CreateConfig(string storagePath)
        => new()
        {
            GracefulShutdownSeconds = 0,
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            }
        };

    private static GatewayAppRuntime CreateRuntime(GatewayConfig config, string storagePath)
    {
        var memoryStore = new FileMemoryStore(storagePath, 4);
        var runtimeMetrics = new RuntimeMetrics();
        var sessionManager = new SessionManager(
            memoryStore,
            config,
            NullLoggerFactory.Instance.CreateLogger("SessionManager"),
            runtimeMetrics);
        var heartbeatService = new HeartbeatService(
            config,
            memoryStore,
            sessionManager,
            NullLogger<HeartbeatService>.Instance);
        var allowlistSemantics = AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics);
        var allowlists = new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance);
        var recentSenders = new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance);
        var approvalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        var webhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance);
        var actorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance);
        var sessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var pluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance);
        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);
        var retentionCoordinator = Substitute.For<IMemoryRetentionCoordinator>();
        retentionCoordinator.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionRunStatus { Enabled = false, StoreSupportsRetention = false }));
        retentionCoordinator.SweepNowAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionSweepResult()));

        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
        agentRuntime.LoadedSkillNames.Returns(Array.Empty<string>());
        agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var nativeRegistry = new NativePluginRegistry(config.Plugins.Native, NullLogger.Instance, config.Tooling);
        var skillWatcher = new SkillWatcherService(config.Skills, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);

        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = RuntimeOrchestrator.Native,
            Pipeline = pipeline,
            MiddlewarePipeline = middleware,
            WebSocketChannel = wsChannel,
            ChannelAdapters = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal)
            {
                ["websocket"] = wsChannel
            },
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = new PairingManager(storagePath, NullLogger<PairingManager>.Instance),
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            ApprovalAuditStore = approvalAuditStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            PaymentRuntime = new PaymentRuntimeService(
                [new MockPaymentProvider()],
                new InMemoryPaymentSecretVault(),
                new DefaultPaymentPolicy(),
                new InMemoryPaymentAuditSink(),
                defaultProviderId: "mock"),
            Heartbeat = heartbeatService,
            LoadedSkills = Array.Empty<SkillDefinition>(),
            SkillWatcher = skillWatcher,
            PluginReports = Array.Empty<PluginLoadReport>(),
            Operations = new RuntimeOperationsState
            {
                ProviderPolicies = providerPolicies,
                ProviderRegistry = providerRegistry,
                LlmExecution = llmExecution,
                PluginHealth = pluginHealth,
                ApprovalGrants = approvalGrants,
                RuntimeEvents = runtimeEvents,
                OperatorAudit = operatorAudit,
                WebhookDeliveries = webhookDeliveries,
                ActorRateLimits = actorRateLimits,
                SessionMetadata = sessionMetadata
            },
            EffectiveRequireToolApproval = false,
            EffectiveApprovalRequiredTools = Array.Empty<string>(),
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = null,
            DynamicProviderOwners = Array.Empty<string>(),
            EstimatedSkillPromptChars = 0,
            CronTask = null,
            TwilioSmsWebhookHandler = null,
            PluginHost = null,
            NativeDynamicPluginHost = null,
            WhatsAppWorkerHost = null,
            RegisteredToolNames = FrozenSet<string>.Empty,
            ChannelAuthEvents = new ChannelAuthEventStore()
        };
    }

    private static string CreateTempRoot()
        => Path.Combine(Path.GetTempPath(), "openclaw-runtime-lifecycle-tests", Guid.NewGuid().ToString("N"));

    private static async Task<IReadOnlyList<string>> WaitForCancellationAsync(
        CancellationToken ct,
        TaskCompletionSource reloadCanceled)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reloadCanceled.TrySetResult();
        }

        return Array.Empty<string>();
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
