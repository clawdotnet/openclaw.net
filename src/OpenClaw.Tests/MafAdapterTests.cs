using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.Routing.Onnx;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MafAdapterTests
{
    [Fact]
    public void MafCapabilities_JitMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "jit",
            EffectiveMode = GatewayRuntimeMode.Jit,
            DynamicCodeSupported = true
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public void MafCapabilities_AotMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "aot",
            EffectiveMode = GatewayRuntimeMode.Aot,
            DynamicCodeSupported = false
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public async Task MafSessionStateStore_RoundTripsSerializedSession_WhenHistoryMatches()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-roundtrip");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);
            var savedState = NormalizeJson(await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions: null, TestContext.Current.CancellationToken));

            await store.SaveAsync(agent, session, agentSession, TestContext.Current.CancellationToken);
            var loadedSession = await store.LoadAsync(agent, session, TestContext.Current.CancellationToken);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, TestContext.Current.CancellationToken));

            Assert.Equal(savedState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void MafAgentRuntimeFactory_WithDelegationEnabled_AddsDelegateTool()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new MafAgentRuntimeFactory(
            new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
            new MafSessionStateStore(
                new GatewayConfig(),
                Options.Create(new MafOptions()),
                NullLogger<MafSessionStateStore>.Instance),
            new MafTelemetryAdapter(),
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance);

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-delegation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = Assert.IsType<MafAgentRuntime>(factory.Create(new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = new GatewayConfig
                {
                    Memory = new MemoryConfig
                    {
                        StoragePath = storagePath
                    },
                    Llm = new LlmProviderConfig
                    {
                        Provider = "test-maf",
                        Model = "maf-test-model"
                    },
                    Delegation = new DelegationConfig
                    {
                        Enabled = true,
                        Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
                        {
                            ["reviewer"] = new()
                            {
                                Name = "reviewer",
                                SystemPrompt = "Review code changes.",
                                MaxIterations = 2,
                                MaxHistoryTurns = 4
                            }
                        }
                    }
                },
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                ChatClient = new MafTestChatClient(),
                Tools = [new TestTool()],
                MemoryStore = new FileMemoryStore(storagePath, 4),
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = new TestLlmExecutionService(),
                Skills = [],
                SkillsConfig = new SkillsConfig(),
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
            }));

            var mafToolsField = typeof(MafAgentRuntime).GetField("_mafTools", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(mafToolsField);

            var mafTools = Assert.IsAssignableFrom<IReadOnlyList<AITool>>(mafToolsField!.GetValue(runtime));
            Assert.Contains(mafTools, tool => tool is AIFunction function && function.Name == "delegate_agent");
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }


    [Fact]
    public async Task MafAgentRuntime_RunAsync_FollowUpRestoresSidecarAgainstPriorCompletedHistory()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-runtime-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var logs = new List<string>();
            var runtime = CreateRuntime(storagePath, new TestLlmExecutionService(), new MafOptions(), logs);
            var session = CreateSession("maf-runtime-sidecar-runasync");

            await runtime.RunAsync(session, "first turn", TestContext.Current.CancellationToken);
            await runtime.RunAsync(session, "follow-up turn", TestContext.Current.CancellationToken);

            Assert.Contains(logs, message => message.Contains("Restored MAF session sidecar", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, message => message.Contains("history hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_RestoresSidecarWhenTransientRouteStateChangesBetweenTurns()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<TurnRoutingRequest>();
                return new TurnRoutingDecision
                {
                    Tier = "T1",
                    AllowedTools = request.UserMessage.Contains("first", StringComparison.OrdinalIgnoreCase)
                        ? ["echo_tool"]
                        : ["shell"],
                    SystemPromptSuffix = request.UserMessage.Contains("first", StringComparison.OrdinalIgnoreCase)
                        ? "First turn route suffix."
                        : "Follow-up route suffix.",
                    Reason = request.UserMessage.Contains("first", StringComparison.OrdinalIgnoreCase)
                        ? "first_route"
                        : "followup_route"
                };
            });

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-runtime-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var logs = new List<string>();
            var runtime = CreateRuntime(storagePath, new TestLlmExecutionService(), new MafOptions(), logs, routing);
            var session = CreateSession("maf-runtime-sidecar-transient-routing");

            await runtime.RunAsync(session, "first turn", TestContext.Current.CancellationToken);
            await runtime.RunAsync(session, "follow-up turn", TestContext.Current.CancellationToken);

            Assert.Contains(logs, message => message.Contains("Restored MAF session sidecar", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, message => message.Contains("history hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunStreamingAsync_FollowUpRestoresSidecarAgainstPriorCompletedHistory()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-runtime-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var logs = new List<string>();
            var runtime = CreateRuntime(storagePath, new StreamingTestLlmExecutionService(), new MafOptions { EnableStreaming = true }, logs);
            var session = CreateSession("maf-runtime-sidecar-streaming");

            await foreach (var _ in runtime.RunStreamingAsync(session, "first turn", TestContext.Current.CancellationToken))
            {
                // Intentionally drain the stream to completion; assertions are on side effects/logs.
            }

            await foreach (var _ in runtime.RunStreamingAsync(session, "follow-up turn", TestContext.Current.CancellationToken))
            {
                // Intentionally drain the stream to completion; assertions are on side effects/logs.
            }

            Assert.Contains(logs, message => message.Contains("Restored MAF session sidecar", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, message => message.Contains("history hash mismatch", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunStreamingAsync_EarlyConsumerBreakKeepsTurnRoutingStateUntilProducerStops()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                AllowedTools = ["echo_tool"],
                SystemPromptSuffix = "Transient streaming route prompt.",
                Reason = "streaming_route"
            });

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-routing-early-break-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var executionService = new BlockingStreamingLlmExecutionService();
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions { EnableStreaming = true },
                routingPolicy: routing,
                tools: [new TestTool("echo_tool")]);
            var session = CreateSession("maf-routing-early-break-streaming");
            session.RouteAllowedTools = ["shell"];
            session.SystemPromptOverride = "Original route prompt";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var enumerator = runtime.RunStreamingAsync(session, "Open README.md", cts.Token).GetAsyncEnumerator();
            Task? disposeTask = null;

            try
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal(AgentStreamEventType.TextDelta, enumerator.Current.Type);
                Assert.Equal("first", enumerator.Current.Content);
                await executionService.ProducerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

                disposeTask = enumerator.DisposeAsync().AsTask();
                await executionService.ProducerCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.False(disposeTask.IsCompleted);
                Assert.Equal(["echo_tool"], session.RouteAllowedTools);
                Assert.Equal("Original route prompt\nTransient streaming route prompt.", session.SystemPromptOverride);
                Assert.Equal("T1", session.RouteModelTier);
                Assert.Equal("streaming_route", session.RouteReason);

                executionService.AllowCancelledProducerToComplete.SetResult();
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

                Assert.Equal(["shell"], session.RouteAllowedTools);
                Assert.Equal("Original route prompt", session.SystemPromptOverride);
                Assert.Equal("T1", session.RouteModelTier);
                Assert.Null(session.RouteReason);
            }
            finally
            {
                cts.Cancel();
                executionService.AllowCancelledProducerToComplete.TrySetResult();
                if (disposeTask is null)
                    await enumerator.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_FiltersToolsByRouteAllowedTools()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-tool-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = new MafAgentRuntime(
                new AgentRuntimeFactoryContext
                {
                    Services = services,
                    Config = new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        },
                        Llm = new LlmProviderConfig
                        {
                            Provider = "test-maf",
                            Model = "maf-test-model"
                        }
                    },
                    RuntimeState = new GatewayRuntimeState
                    {
                        RequestedMode = "jit",
                        EffectiveMode = GatewayRuntimeMode.Jit,
                        DynamicCodeSupported = true
                    },
                    ChatClient = new MafTestChatClient(),
                    Tools = [new TestTool("echo_tool"), new TestTool("shell")],
                    MemoryStore = new FileMemoryStore(storagePath, 4),
                    RuntimeMetrics = new RuntimeMetrics(),
                    ProviderUsage = new ProviderUsageTracker(),
                    LlmExecutionService = executionService,
                    Skills = [],
                    SkillsConfig = new SkillsConfig(),
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
                },
                new MafOptions(),
                new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(
                    new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        }
                    },
                    Options.Create(new MafOptions()),
                    NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(),
                NullLogger<MafAgentRuntime>.Instance);
            var session = CreateSession("maf-tool-filter");
            session.RouteAllowedTools = ["echo_tool"];

            await runtime.RunAsync(session, "use tools", TestContext.Current.CancellationToken);

            var toolNames = executionService.LastToolNames;
            Assert.Equal(["echo_tool"], toolNames);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_FiltersToolsByPresetResolver()
    {
        var services = new ServiceCollection()
            .AddSingleton<IToolPresetResolver>(new TestToolPresetResolver(["echo_tool"]))
            .BuildServiceProvider();
        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-preset-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = new MafAgentRuntime(
                new AgentRuntimeFactoryContext
                {
                    Services = services,
                    Config = new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        },
                        Llm = new LlmProviderConfig
                        {
                            Provider = "test-maf",
                            Model = "maf-test-model"
                        }
                    },
                    RuntimeState = new GatewayRuntimeState
                    {
                        RequestedMode = "jit",
                        EffectiveMode = GatewayRuntimeMode.Jit,
                        DynamicCodeSupported = true
                    },
                    ChatClient = new MafTestChatClient(),
                    Tools = [new TestTool("echo_tool"), new TestTool("shell")],
                    MemoryStore = new FileMemoryStore(storagePath, 4),
                    RuntimeMetrics = new RuntimeMetrics(),
                    ProviderUsage = new ProviderUsageTracker(),
                    LlmExecutionService = executionService,
                    Skills = [],
                    SkillsConfig = new SkillsConfig(),
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
                },
                new MafOptions(),
                new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(
                    new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        }
                    },
                    Options.Create(new MafOptions()),
                    NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(),
                NullLogger<MafAgentRuntime>.Instance);
            var session = CreateSession("maf-preset-filter");
            session.RoutePresetId = "test-preset";

            await runtime.RunAsync(session, "use tools", TestContext.Current.CancellationToken);

            var toolNames = executionService.LastToolNames;
            Assert.Equal(["echo_tool"], toolNames);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_AppliesTurnRoutingPolicy_AndRestoresSessionRouteState()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                ModelProfileId = "mini-readonly",
                DirectModelFallbackProfileId = "mini-readonly-fallback",
                ReasoningLevel = "high",
                ResponsePolicy = "detailed",
                AllowedTools = ["echo_tool"],
                SystemPromptSuffix = "Keep the reply short and skip planning.",
                Reason = "simple_read_only"
            });

        var services = new ServiceCollection()
            .AddSingleton(routing)
            .BuildServiceProvider();
        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = new MafAgentRuntime(
                new AgentRuntimeFactoryContext
                {
                    Services = services,
                    Config = new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        },
                        Llm = new LlmProviderConfig
                        {
                            Provider = "test-maf",
                            Model = "maf-test-model"
                        }
                    },
                    RuntimeState = new GatewayRuntimeState
                    {
                        RequestedMode = "jit",
                        EffectiveMode = GatewayRuntimeMode.Jit,
                        DynamicCodeSupported = true
                    },
                    ChatClient = new MafTestChatClient(),
                    Tools = [new TestTool("echo_tool"), new TestTool("shell")],
                    MemoryStore = new FileMemoryStore(storagePath, 4),
                    RuntimeMetrics = new RuntimeMetrics(),
                    ProviderUsage = new ProviderUsageTracker(),
                    LlmExecutionService = executionService,
                    Skills = [],
                    SkillsConfig = new SkillsConfig(),
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
                },
                new MafOptions(),
                new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(
                    new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        }
                    },
                    Options.Create(new MafOptions()),
                    NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(),
                NullLogger<MafAgentRuntime>.Instance);
            var session = CreateSession("maf-turn-routing");
            session.RouteAllowedTools = ["shell"];
            session.SystemPromptOverride = "Original route prompt";
            session.FallbackModelProfileIds = ["legacy-fallback"];
            session.ReasoningEffort = "low";
            session.ResponseMode = "concise";

            await runtime.RunAsync(session, "Open README.md", TestContext.Current.CancellationToken);

            Assert.Equal(["echo_tool"], executionService.LastToolNames);
            Assert.Equal(["mini-readonly-fallback", "legacy-fallback"], executionService.LastFallbackModelProfileIds);
            Assert.Equal("high", executionService.LastReasoningEffort);
            Assert.Equal("detailed", executionService.LastResponseMode);
            await routing.Received(1).ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>());
            Assert.Equal(["shell"], session.RouteAllowedTools);
            Assert.Equal("Original route prompt", session.SystemPromptOverride);
            Assert.Equal(["legacy-fallback"], session.FallbackModelProfileIds);
            Assert.Equal("low", session.ReasoningEffort);
            Assert.Equal("concise", session.ResponseMode);
            Assert.Equal("T1", session.RouteModelTier);
            Assert.Null(session.RouteReason);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_OnnxPolicyWithMissingAssets_FallsBackToT2_AndKeepsServiceAvailable()
    {
        var routing = new OnnxTurnRoutingPolicy(
            new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "missing-classifier.onnx",
                    EmbeddingModelPath = "missing-embedding.onnx",
                    TokenizerPath = "missing-tokenizer.json"
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "local-freeform", DisableTools = true },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "mini-readonly", AllowedTools = ["echo_tool"] },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-deep" }
                    }
                }
            },
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-onnx-fallback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions(),
                routingPolicy: routing,
                tools: [new TestTool("echo_tool")]);
            var session = CreateSession("maf-onnx-fallback");

            var result = await runtime.RunAsync(session, "route this turn", TestContext.Current.CancellationToken);

            Assert.Equal("ok", result);
            Assert.Equal("T2", session.RouteModelTier);
            Assert.Equal(["echo_tool"], executionService.LastToolNames);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_OnnxPolicyDecision_AppliesAndRestoresFallbackReasoningAndResponsePolicy()
    {
        var routing = new OnnxTurnRoutingPolicy(
            new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "classifier.onnx",
                    EmbeddingModelPath = "embeddings.onnx",
                    TokenizerPath = "tokenizer.json"
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "local-freeform", DisableTools = true },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "mini-readonly", AllowedTools = ["echo_tool"] },
                        T2 = new DynamicTurnRoutingTierTarget
                        {
                            ModelProfileId = "frontier-tools",
                            DirectModelFallbackProfileId = "frontier-tools-fallback",
                            ReasoningLevel = "high",
                            ResponsePolicy = "detailed",
                            AllowedTools = ["echo_tool"]
                        },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-deep" }
                    }
                }
            },
            predictedTier: 2,
            NullLogger<OnnxTurnRoutingPolicy>.Instance);

        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-onnx-parity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions(),
                routingPolicy: routing,
                tools: [new TestTool("echo_tool")]);
            var session = CreateSession("maf-onnx-parity");
            session.FallbackModelProfileIds = ["legacy-fallback"];
            session.ReasoningEffort = "low";
            session.ResponseMode = "concise";

            var result = await runtime.RunAsync(session, "route this turn", TestContext.Current.CancellationToken);

            Assert.Equal("ok", result);
            Assert.Equal("T2", session.RouteModelTier);
            Assert.Equal(["echo_tool"], executionService.LastToolNames);
            Assert.Equal(["frontier-tools-fallback", "legacy-fallback"], executionService.LastFallbackModelProfileIds);
            Assert.Equal("high", executionService.LastReasoningEffort);
            Assert.Equal("detailed", executionService.LastResponseMode);
            Assert.Equal(["legacy-fallback"], session.FallbackModelProfileIds);
            Assert.Equal("low", session.ReasoningEffort);
            Assert.Equal("concise", session.ResponseMode);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_RestoresTurnRoutingState_WhenCreateAgentFailsAfterRouting()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                AllowedTools = ["echo_tool"],
                SystemPromptSuffix = "Transient route prompt.",
                Reason = "setup_failed_after_routing"
            });

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-routing-setup-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(storagePath, new TestLlmExecutionService(), new MafOptions(), routingPolicy: routing, tools: [new TestTool("echo_tool")]);
            RemoveMafToolMapping(runtime, "echo_tool");
            var session = CreateSession("maf-routing-create-agent-failure-runasync");
            session.RouteAllowedTools = ["shell"];
            session.SystemPromptOverride = "Original route prompt";

            var result = await runtime.RunAsync(session, "Open README.md", TestContext.Current.CancellationToken);

            Assert.Equal("Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.", result);
            Assert.Equal(["shell"], session.RouteAllowedTools);
            Assert.Equal("Original route prompt", session.SystemPromptOverride);
            Assert.Equal("T1", session.RouteModelTier);
            Assert.Null(session.RouteReason);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunStreamingAsync_RestoresTurnRoutingState_WhenCreateAgentFailsAfterRouting()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                AllowedTools = ["echo_tool"],
                SystemPromptSuffix = "Transient route prompt.",
                Reason = "setup_failed_after_routing"
            });

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-routing-setup-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(storagePath, new StreamingTestLlmExecutionService(), new MafOptions { EnableStreaming = true }, routingPolicy: routing, tools: [new TestTool("echo_tool")]);
            RemoveMafToolMapping(runtime, "echo_tool");
            var session = CreateSession("maf-routing-create-agent-failure-streaming");
            session.RouteAllowedTools = ["shell"];
            session.SystemPromptOverride = "Original route prompt";

            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            {
                await foreach (var _ in runtime.RunStreamingAsync(session, "Open README.md", TestContext.Current.CancellationToken))
                {
                    // Intentionally consume the stream to trigger agent creation and surface the expected exception.
                }
            });

            Assert.Equal(["shell"], session.RouteAllowedTools);
            Assert.Equal("Original route prompt", session.SystemPromptOverride);
            Assert.Equal("T1", session.RouteModelTier);
            Assert.Null(session.RouteReason);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_DisableToolsRoutingDecision_ExposesNoToolsToLlm()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T0",
                DisableTools = true,
                Reason = "disable_tools"
            });

        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-disable-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions(),
                routingPolicy: routing,
                tools: [new TestTool("echo_tool"), new TestTool("shell")]);
            var session = CreateSession("maf-disable-tools");

            await runtime.RunAsync(session, "answer directly", TestContext.Current.CancellationToken);

            Assert.Empty(executionService.LastToolNames);
            Assert.False(session.RouteToolsDisabled);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_AllowedToolsRoutingDecision_ClearsPriorDisableToolsStateForActiveCall()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                AllowedTools = ["echo_tool"],
                Reason = "allow_tools"
            });

        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-allow-tools-after-disable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions(),
                routingPolicy: routing,
                tools: [new TestTool("echo_tool"), new TestTool("shell")]);
            var session = CreateSession("maf-allow-tools-after-disable");
            session.RouteToolsDisabled = true;

            await runtime.RunAsync(session, "read safely", TestContext.Current.CancellationToken);

            Assert.Equal(["echo_tool"], executionService.LastToolNames);
            Assert.True(session.RouteToolsDisabled);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_DefaultRoutingDecision_DoesNotClearManualRouteStateForActiveCall()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision());

        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-default-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                executionService,
                new MafOptions(),
                routingPolicy: routing,
                tools: [new TestTool("echo_tool"), new TestTool("shell")]);
            var session = CreateSession("maf-default-routing-manual-state");
            session.RouteAllowedTools = ["shell"];
            session.PreferredModelTags = ["manual-tag"];

            await runtime.RunAsync(session, "use existing manual route", TestContext.Current.CancellationToken);

            Assert.Equal(["shell"], executionService.LastToolNames);
            Assert.Equal(["manual-tag"], executionService.LastPreferredModelTags);
            Assert.Equal(["shell"], session.RouteAllowedTools);
            Assert.Equal(["manual-tag"], session.PreferredModelTags);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_StructuredMode_MissingDependency_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "second",
                                    Kind = "tool_call",
                                    Tool = "noop",
                                    DependsOn = ["first"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-missing-dependency");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("meta-flow", doc.RootElement.GetProperty("skill").GetString());
            Assert.Contains("missing dependency", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("invalid_dag", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("steps").EnumerateArray());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_StructuredMode_UserInputWithoutDefault_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    WithJson = "{}"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-user-input");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("meta-flow", doc.RootElement.GetProperty("skill").GetString());
            Assert.Contains("requires user input", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("user_input_required", doc.RootElement.GetProperty("error_code").GetString());
            var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Single(steps);
            Assert.Equal("ask", steps[0].GetProperty("id").GetString());
            Assert.Equal("failed", steps[0].GetProperty("status").GetString());
            var checkpoint = Assert.IsType<SessionMetaExecutionCheckpoint>(session.MetaExecutionCheckpoint);
            var checkpointStep = Assert.Single(checkpoint.StepResults);
            Assert.Equal("ask", checkpointStep.Id);
            Assert.Equal("user_input_required", checkpointStep.FailureCode);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_UserInputPauseResume_ContinuesWithoutReRunningCompletedSteps()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var preTool = new CountingMafTool("pre_tool", "prefetched");
            var postTool = new CountingMafTool("post_tool", "completed");

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [preTool, postTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:finish",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "pre",
                                    Kind = "tool_call",
                                    Tool = "pre_tool"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    DependsOn = ["pre"],
                                    WithJson = "{\"prompt\":\"Please confirm\"}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "finish",
                                    Kind = "tool_call",
                                    Tool = "post_tool",
                                    DependsOn = ["ask"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-pause-resume");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(session.MetaExecutionCheckpoint);
            Assert.Equal(1, preTool.CallCount);
            Assert.Equal(0, postTool.CallCount);

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);
            Assert.Equal("completed", resumed);
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(1, preTool.CallCount);
            Assert.Equal(1, postTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_Success_PersistsMetaRunRecord()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-run-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var successTool = new CountingMafTool("success_tool", "ok");

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [successTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:first",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "success_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-run-history");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("ok", result);
            Assert.NotNull(session.MetaRunHistory);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Equal("meta-flow", run.SkillName);
            Assert.Equal("completed", run.Status);
            Assert.Equal("ok", run.FinalText);
            Assert.Null(run.Error);
            var step = Assert.Single(run.StepResults);
            Assert.Equal("first", step.Id);
            Assert.Equal("completed", step.Status);
            Assert.Null(run.GovernanceHarnessContractId);
            Assert.Null(run.GovernancePevId);
            Assert.Null(run.GovernanceEvidenceBundleId);
            Assert.Null(run.GovernanceSessionMetaRunRecordRef);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ActionExecuteResult_PersistsGovernanceMappingIntoMetaRunRecord()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-run-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var actionExecuteTool = new ActionExecuteTool();
            var proposal = "{" +
                           "\"actionName\":\"sync_customer_tier\"," +
                           "\"source\":{\"metaSkill\":\"customer-risk-assistant\",\"runId\":\"run_1\",\"stepId\":\"step_1\"}," +
                           "\"trigger\":{\"condition\":\"riskLevel == medium\",\"evidenceRefs\":[\"ev_001\"]}," +
                           "\"target\":{\"system\":\"crm\",\"operation\":\"updateCustomerTier\"}," +
                           "\"execution\":[{\"call\":\"crm.updateTier\",\"args\":{\"customerId\":\"C123\",\"tier\":\"B\"}}]," +
                           "\"idempotencyKey\":\"proposal-C123-20260715-01\"," +
                           "\"metadata\":{\"policyDecision\":\"proposal_only\",\"harnessContractId\":\"hctr_123\",\"pevId\":\"pev_456\",\"evidenceBundleId\":\"evb_789\"}" +
                           "}";

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [actionExecuteTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:first",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "action_execute",
                                    ToolArgsJson = "{\"proposal\":" + proposal + "}"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-run-history-governance");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Contains("proposal_only", result, StringComparison.OrdinalIgnoreCase);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Equal("hctr_123", run.GovernanceHarnessContractId);
            Assert.Equal("pev_456", run.GovernancePevId);
            Assert.Equal("evb_789", run.GovernanceEvidenceBundleId);
            Assert.Equal("session_meta_run_record_pending", run.GovernanceSessionMetaRunRecordRef);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_WhenMetaPolicyDisabled_ReturnsPolicyError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "user_input",
                                    WithJson = "{}"
                                }
                            ]
                        }
                    }
                ],
                skillsConfig: new SkillsConfig
                {
                    MetaSkill = new MetaSkillPolicyConfig
                    {
                        Enabled = false
                    }
                });
            var session = CreateSession("maf-meta-policy");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Contains("disabled by runtime policy", result, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(session.MetaRunHistory);
            Assert.Null(session.MetaExecutionCheckpoint);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_InvalidPlanAfterPause_ClearsCheckpoint()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-invalid-plan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var metaSkill = new SkillDefinition
            {
                Name = "meta-flow",
                Description = "meta flow",
                Instructions = "...",
                Location = "/skills/meta-flow",
                Kind = SkillKind.Meta,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "ask",
                            Kind = "user_input",
                            WithJson = "{\"prompt\":\"Please confirm\"}"
                        },
                        new MetaSkillStepDefinition
                        {
                            Id = "finish",
                            Kind = "llm_chat",
                            DependsOn = ["ask"]
                        }
                    ]
                }
            };

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills: [metaSkill]);
            var session = CreateSession("maf-meta-invalid-plan-after-pause");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(session.MetaExecutionCheckpoint);

            var invalidSkill = new SkillDefinition
            {
                Name = "meta-flow",
                Description = "meta flow",
                Instructions = "...",
                Location = "/skills/meta-flow",
                Kind = SkillKind.Meta,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "finish",
                            Kind = "llm_chat",
                            DependsOn = ["missing"]
                        }
                    ]
                }
            };

            var field = typeof(MafAgentRuntime).GetField("_loadedSkills", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(runtime, new[] { invalidSkill });

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);

            Assert.Contains("missing dependency", resumed, StringComparison.OrdinalIgnoreCase);
            Assert.Null(session.MetaExecutionCheckpoint);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_RejectsNestedMetaDelegation()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-nested-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var nestedMeta = new SkillDefinition
            {
                Name = "nested-meta",
                Description = "nested meta",
                Instructions = "...",
                Location = "/skills/nested-meta",
                Kind = SkillKind.Meta,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "ask",
                            Kind = "user_input",
                            WithJson = "{}"
                        }
                    ]
                }
            };

            var outerMeta = new SkillDefinition
            {
                Name = "outer-meta",
                Description = "outer meta",
                Instructions = "...",
                Location = "/skills/outer-meta",
                Kind = SkillKind.Meta,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "delegate",
                            Kind = "skill_exec",
                            Skill = "nested-meta",
                            SkillExecEntrypoint = "noop.sh"
                        }
                    ]
                }
            };

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills: [nestedMeta, outerMeta]);
            var session = CreateSession("maf-meta-nested");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "outer-meta", "hello", TestContext.Current.CancellationToken);

            Assert.Contains("cannot compose meta skill 'nested-meta'", result, StringComparison.OrdinalIgnoreCase);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Equal("failed", run.Status);
            Assert.Equal("meta_step_error", run.ErrorCode);
            Assert.Contains("cannot compose meta skill 'nested-meta'", run.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(session.MetaExecutionCheckpoint);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_PlanChangedAfterPause_DropsStaleCompletedStepState()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-stale-checkpoint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var finalTool = new CountingMafTool("final_tool", "final");
            var metaSkill = new SkillDefinition
            {
                Name = "meta-flow",
                Description = "meta flow",
                Instructions = "...",
                Location = "/skills/meta-flow",
                Kind = SkillKind.Meta,
                FinalTextMode = "step:finish",
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "prepare",
                            Kind = "user_input",
                            WithJson = "{\"default\":\"seed\"}"
                        },
                        new MetaSkillStepDefinition
                        {
                            Id = "ask",
                            Kind = "user_input",
                            DependsOn = ["prepare"],
                            WithJson = "{\"prompt\":\"Please confirm\"}"
                        },
                        new MetaSkillStepDefinition
                        {
                            Id = "finish",
                            Kind = "tool_call",
                            Tool = "final_tool",
                            DependsOn = ["ask"]
                        }
                    ]
                }
            };

            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [finalTool],
                skills: [metaSkill]);
            var session = CreateSession("maf-meta-plan-changed-after-pause");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(session.MetaExecutionCheckpoint);

            var changedSkill = new SkillDefinition
            {
                Name = "meta-flow",
                Description = "meta flow",
                Instructions = "...",
                Location = "/skills/meta-flow",
                Kind = SkillKind.Meta,
                FinalTextMode = "step:finish",
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition
                        {
                            Id = "ask",
                            Kind = "user_input",
                            WithJson = "{\"default\":\"approved\"}"
                        },
                        new MetaSkillStepDefinition
                        {
                            Id = "finish",
                            Kind = "tool_call",
                            Tool = "final_tool",
                            DependsOn = ["ask"]
                        }
                    ]
                }
            };

            var field = typeof(MafAgentRuntime).GetField("_loadedSkills", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(runtime, new[] { changedSkill });

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);

            Assert.Equal("final", resumed);
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(1, finalTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_WhenFalse_SkipsStepAndBlocksDependents()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-when-false-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "chosen");
            var afterTool = new CountingMafTool("after_tool", "after");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool, afterTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "prepare",
                                    Kind = "user_input",
                                    WithJson = "{\"default\":\"skip\"}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "branch",
                                    Kind = "tool_call",
                                    Tool = "chosen_tool",
                                    When = "outputs.prepare == 'run'",
                                    DependsOn = ["prepare"]
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "after",
                                    Kind = "tool_call",
                                    Tool = "after_tool",
                                    DependsOn = ["branch"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-when-false");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);

            Assert.Equal("skip", result);
            Assert.Equal(0, chosenTool.CallCount);
            Assert.Equal(0, afterTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_RouteArray_SelectsFirstMatchingBranch()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-route-array-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "chosen");
            var skippedTool = new CountingMafTool("skipped_tool", "skipped");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool, skippedTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:chosen",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    WithJson = "{\"default\":\"bug\"}",
                                    Routes =
                                    [
                                        new MetaRouteDefinition { When = "outputs.ask == 'bug'", To = "chosen" },
                                        new MetaRouteDefinition { To = "skipped" }
                                    ]
                                },
                                new MetaSkillStepDefinition { Id = "chosen", Kind = "tool_call", Tool = "chosen_tool" },
                                new MetaSkillStepDefinition { Id = "skipped", Kind = "tool_call", Tool = "skipped_tool" }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-route-array");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);

            Assert.Equal("chosen", result);
            Assert.Equal(1, chosenTool.CallCount);
            Assert.Equal(0, skippedTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ToolArgs_MergesCompositionAndStepArgsBeforeToolExecution()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-tool-args-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var echoTool = new EchoArgumentsMafTool("echo_tool");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [echoTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:call",
                        Composition = new MetaSkillComposition
                        {
                            ToolArgsJson = "{\"trace\":\"{{ input }}\",\"mode\":\"composition\"}",
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "prepare",
                                    Kind = "user_input",
                                    WithJson = "{\"default\":\"ready\"}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "call",
                                    Kind = "tool_call",
                                    Tool = "echo_tool",
                                    WithJson = "{\"mode\":\"with\",\"state\":\"{{ outputs.prepare }}\"}",
                                    ToolArgsJson = "{\"mode\":\"step\"}",
                                    DependsOn = ["prepare"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-tool-args");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "incident-42", TestContext.Current.CancellationToken);

            Assert.Equal("{\"trace\":\"incident-42\",\"mode\":\"step\",\"state\":\"ready\"}", result);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_SkillExecStep_RunsScriptResourceEntrypoint()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-skill-exec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var skillRoot = Path.Combine(storagePath, "worker-skill");
        var scriptsDir = Path.Combine(skillRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var scriptPath = Path.Combine(scriptsDir, "echo.ps1");
        await File.WriteAllTextAsync(scriptPath, "param([string]$value)\nWrite-Output \"skill:$value\"\n");

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "worker-skill",
                        Description = "worker",
                        Instructions = "worker instructions",
                        Location = skillRoot,
                        Resources =
                        [
                            new SkillResource
                            {
                                Name = "echo.ps1",
                                RelativePath = "scripts/echo.ps1",
                                AbsolutePath = scriptPath,
                                Kind = SkillResourceKind.Script
                            }
                        ]
                    },
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = skillRoot,
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:exec",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "exec",
                                    Kind = "skill_exec",
                                    Skill = "worker-skill",
                                    SkillExecEntrypoint = "echo.ps1",
                                    SkillExecArgs = ["{{ input }}"],
                                    SkillExecParseMode = "text"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-skill-exec");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "incident-42", TestContext.Current.CancellationToken);

            Assert.Equal("skill:incident-42", result.Trim());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_SkillExecStep_WithStdin_ExecutesSuccessfully()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-skill-exec-stdin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var skillRoot = Path.Combine(storagePath, "worker-skill");
        var scriptsDir = Path.Combine(skillRoot, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var scriptPath = Path.Combine(scriptsDir, "echo-stdin.ps1");
        await File.WriteAllTextAsync(scriptPath, "$inputText = [System.IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd()\nWrite-Output \"stdin:$inputText\"\n");

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "worker-skill",
                        Description = "worker",
                        Instructions = "worker instructions",
                        Location = skillRoot,
                        Resources =
                        [
                            new SkillResource
                            {
                                Name = "echo-stdin.ps1",
                                RelativePath = "scripts/echo-stdin.ps1",
                                AbsolutePath = scriptPath,
                                Kind = SkillResourceKind.Script
                            }
                        ]
                    },
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = skillRoot,
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:exec",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "exec",
                                    Kind = "skill_exec",
                                    Skill = "worker-skill",
                                    SkillExecEntrypoint = "echo-stdin.ps1",
                                    SkillExecStdin = "{{ input }}",
                                    SkillExecParseMode = "text"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-skill-exec-stdin");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "incident-stdin", TestContext.Current.CancellationToken);

            Assert.Equal("stdin:incident-stdin", result.Trim());

            var run = Assert.Single(session.MetaRunHistory);
            var step = Assert.Single(run.StepResults);
            Assert.Equal("skill_exec", step.Kind);
            Assert.NotNull(step.ExecutionEvidence);
            Assert.Equal("stdin", step.ExecutionEvidence!.InputMode);
            Assert.True(step.ExecutionEvidence.StdinBytes > 0);
            Assert.Contains("echo-stdin.ps1", step.ExecutionEvidence.CommandPreview, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClarifyForm_NormalizesInputBeforePublishingOutput()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-clarify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:ask",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    Clarify = new MetaClarifySchema
                                    {
                                        Mode = "form",
                                        Fields =
                                        [
                                            new MetaClarifyField { Name = "topic", Type = "string", Required = true, MinLength = 3 },
                                            new MetaClarifyField { Name = "priority", Type = "enum", Options = ["low", "medium", "high"], DefaultValue = JsonSerializer.SerializeToElement("medium") }
                                        ]
                                    }
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-clarify-form");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "{\"topic\":\"OpenSquilla\"}", TestContext.Current.CancellationToken);

            Assert.Equal("{\"topic\":\"OpenSquilla\",\"priority\":\"medium\"}", result);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClarifySkipIf_BypassesPromptAndCompletes()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-skip-if-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:ask",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    Clarify = new MetaClarifySchema
                                    {
                                        SkipIf = "inputs.user_message == ''"
                                    }
                                }
                            ]
                        }
                    }
                ]);

            var session = CreateSession("maf-meta-skip-if");
            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);

            Assert.True(string.IsNullOrEmpty(result));
            Assert.Null(session.MetaExecutionCheckpoint);
            var run = Assert.Single(session.MetaRunHistory);
            var step = Assert.Single(run.StepResults);
            Assert.Equal("completed", step.Status);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClarifyCancel_ReturnsStructuredCancelError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-clarify-cancel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    Clarify = new MetaClarifySchema
                                    {
                                        Mode = "chat",
                                        CancelWords = ["cancel"],
                                        Fields = [new MetaClarifyField { Name = "topic", Type = "string", Required = true }]
                                    }
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-clarify-cancel");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "cancel", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(result);

            Assert.Equal("user_input_cancelled", doc.RootElement.GetProperty("error_code").GetString());
            var step = doc.RootElement.GetProperty("steps").EnumerateArray().Single();
            Assert.Equal("failed", step.GetProperty("status").GetString());
            Assert.Equal("user_input_cancelled", step.GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClarifyTimeout_ReturnsStructuredTimeoutError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-clarify-timeout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    Clarify = new MetaClarifySchema
                                    {
                                        Mode = "chat",
                                        TimeoutSeconds = 1,
                                        Fields = [new MetaClarifyField { Name = "topic", Type = "string", Required = true }]
                                    }
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-clarify-timeout");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);

            var checkpoint = Assert.IsType<SessionMetaExecutionCheckpoint>(session.MetaExecutionCheckpoint);
            session.MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
            {
                SkillName = checkpoint.SkillName,
                PendingStepId = checkpoint.PendingStepId,
                Prompt = checkpoint.Prompt,
                CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5),
                LastUpdatedAtUtc = checkpoint.LastUpdatedAtUtc,
                PendingStepIds = [.. checkpoint.PendingStepIds],
                BlockedStepIds = [.. checkpoint.BlockedStepIds],
                Outputs = new Dictionary<string, string>(checkpoint.Outputs, StringComparer.OrdinalIgnoreCase),
                FailureAliases = new Dictionary<string, string>(checkpoint.FailureAliases, StringComparer.OrdinalIgnoreCase),
                StepResults = [.. checkpoint.StepResults.Select(static result => new SessionMetaStepResult
                {
                    Id = result.Id,
                    Kind = result.Kind,
                    Status = result.Status,
                    FailureCode = result.FailureCode,
                    DurationMs = result.DurationMs,
                    Continued = result.Continued
                })]
            };

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(resumed);

            Assert.Equal("user_input_timeout", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Null(session.MetaExecutionCheckpoint);
            var step = doc.RootElement.GetProperty("steps").EnumerateArray()
                .Single(element => string.Equals(element.GetProperty("failure_code").GetString(), "user_input_timeout", StringComparison.Ordinal));
            Assert.Equal("failed", step.GetProperty("status").GetString());
            Assert.Equal("user_input_timeout", step.GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_OutputChoicesViolation_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-output-choice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "unexpected");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "call",
                                    Kind = "tool_call",
                                    Tool = "chosen_tool",
                                    OutputChoices = ["accepted"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-output-choice");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(result);

            Assert.Equal("invalid_output_choice", doc.RootElement.GetProperty("error_code").GetString());
            var step = doc.RootElement.GetProperty("steps").EnumerateArray().Single();
            Assert.Equal("failed", step.GetProperty("status").GetString());
            Assert.Equal("invalid_output_choice", step.GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_TemplateRenderFailure_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-template-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    WithJson = "{\"input\":\"{{\"}"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-template-failure");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(result);

            Assert.Equal("template_render_failed", doc.RootElement.GetProperty("error_code").GetString());
            var step = doc.RootElement.GetProperty("steps").EnumerateArray().Single();
            Assert.Equal("failed", step.GetProperty("status").GetString());
            Assert.Equal("template_render_failed", step.GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ToolAllowlist_DeniesToolOutsideStepAllowlist()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-tool-allowlist-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "chosen");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "call",
                                    Kind = "tool_call",
                                    Tool = "chosen_tool",
                                    ToolAllowlist = ["other_tool"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-tool-allowlist");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(result);

            Assert.Equal("tool_not_allowlisted", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(0, chosenTool.CallCount);
            var step = doc.RootElement.GetProperty("steps").EnumerateArray().Single();
            Assert.Equal("blocked", step.GetProperty("status").GetString());
            Assert.Equal("tool_not_allowlisted", step.GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_UserInputResume_ToolAllowlistDenial_ClearsCheckpoint()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-tool-allowlist-resume-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "chosen");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    WithJson = "{\"prompt\":\"Please confirm\"}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "call",
                                    Kind = "tool_call",
                                    Tool = "chosen_tool",
                                    ToolAllowlist = ["other_tool"],
                                    DependsOn = ["ask"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-tool-allowlist-resume");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(session.MetaExecutionCheckpoint);

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(resumed);

            Assert.Equal("tool_not_allowlisted", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(0, chosenTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_MetaSkillCreator_PreviewOnly_Completes()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-creator-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: BuildCreatorToolSetForMafTests(),
                skills: [CreateMetaSkillCreatorTestDefinition(fullGated: false)]);
            var session = CreateSession("maf-meta-creator-preview");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-skill-creator", "create a meta-skill preview", TestContext.Current.CancellationToken);

            Assert.Contains("proposal preview ready", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("not found", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_MetaSkillCreator_FullGated_Completes()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-creator-full-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: BuildCreatorToolSetForMafTests(),
                skills: [CreateMetaSkillCreatorTestDefinition(fullGated: true)]);
            var session = CreateSession("maf-meta-creator-full");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-skill-creator", "create production-ready fully gated meta-skill", TestContext.Current.CancellationToken);

            Assert.Contains("proposal preview ready", result, StringComparison.OrdinalIgnoreCase);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "lint", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "smoke", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "persist", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "runtime_e2e", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_MetaSkillCreator_FullGated_LintFailure_CompletesAllSteps()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-creator-lint-fail-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: BuildCreatorToolSetForMafTests(),
                skills: [CreateMetaSkillCreatorLintFailureTestDefinition()]);
            var session = CreateSession("maf-meta-creator-lint-fail");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-skill-creator", "create fully gated meta-skill with broken deps", TestContext.Current.CancellationToken);

            Assert.Contains("proposal preview ready", result, StringComparison.OrdinalIgnoreCase);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "lint", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "smoke", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "runtime_e2e", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "persist", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_MetaSkillCreator_FullGated_PersistFailure_CompletesAllSteps()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-creator-persist-fail-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: BuildCreatorToolSetForMafTests(),
                skills: [CreateMetaSkillCreatorPersistFailureTestDefinition()]);
            var session = CreateSession("maf-meta-creator-persist-fail");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-skill-creator", "create fully gated meta-skill with persist broken args", TestContext.Current.CancellationToken);

            Assert.Contains("proposal preview ready", result, StringComparison.OrdinalIgnoreCase);
            var run = Assert.Single(session.MetaRunHistory);
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "lint", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "smoke", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "runtime_e2e", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
            Assert.Contains(run.StepResults, step => string.Equals(step.Id, "persist", StringComparison.Ordinal) && string.Equals(step.Status, "completed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ToolStepFailure_StopsWhenContinueOnErrorIsFalse()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-tool-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var failingTool = new ThrowingMafTool("failing_tool", "boom");
            var successTool = new CountingMafTool("success_tool", "ok");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [failingTool, successTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "failing_tool"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "second",
                                    Kind = "tool_call",
                                    Tool = "success_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-tool-failure-stop");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Contains("Meta step 'first' failed", result, StringComparison.Ordinal);
            Assert.Equal(0, successTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ToolStepFailure_ContinuesWhenContinueOnErrorIsTrue()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-tool-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var failingTool = new ThrowingMafTool("failing_tool", "boom");
            var successTool = new CountingMafTool("success_tool", "ok");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [failingTool, successTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "failing_tool",
                                    WithJson = "{\"continue_on_error\":true}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "second",
                                    Kind = "tool_call",
                                    Tool = "success_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-tool-failure-continue");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("ok", result);
            Assert.Equal(1, successTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_IndependentToolSteps_RunConcurrently()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-parallel-wave-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var tracker = new MafConcurrencyTracker();
            var firstTool = new ConcurrentProbeMafTool("first_tool", "first", tracker, 120);
            var secondTool = new ConcurrentProbeMafTool("second_tool", "second", tracker, 120);
            var joinTool = new CountingMafTool("join_tool", "joined");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [firstTool, secondTool, joinTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:join",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "first_tool",
                                    WithJson = "{\"continue_on_error\":true}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "second",
                                    Kind = "tool_call",
                                    Tool = "second_tool",
                                    WithJson = "{\"continue_on_error\":true}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "join",
                                    Kind = "tool_call",
                                    Tool = "join_tool",
                                    DependsOn = ["first", "second"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-parallel-wave");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("joined", result);
            Assert.Equal(1, firstTool.CallCount);
            Assert.Equal(1, secondTool.CallCount);
            Assert.True(tracker.MaxConcurrent >= 2, $"Expected MaxConcurrent >= 2, actual: {tracker.MaxConcurrent}");
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_LlmChatContinueOnError_AppliesRouteCompletion()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-continue-route-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var routedTool = new CountingMafTool("routed_tool", "routed");
            var skippedTool = new CountingMafTool("skipped_tool", "skipped");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [routedTool, skippedTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:routed",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "chat",
                                    Kind = "llm_chat",
                                    WithJson = "{\"continue_on_error\":true}",
                                    OutputChoices = ["accepted"],
                                    Routes =
                                    [
                                        new MetaRouteDefinition { When = "outputs.chat != ''", To = "routed" },
                                        new MetaRouteDefinition { To = "skipped" }
                                    ]
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "routed",
                                    Kind = "tool_call",
                                    Tool = "routed_tool"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "skipped",
                                    Kind = "tool_call",
                                    Tool = "skipped_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-continue-route");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("routed", result);
            Assert.Equal(1, routedTool.CallCount);
            Assert.Equal(0, skippedTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_OnFailure_ExecutesSubstituteAndMirrorsOutputToPrimaryStep()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-on-failure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var failingTool = new ThrowingMafTool("failing_tool", "boom");
            var fallbackTool = new CountingMafTool("fallback_tool", "fallback ok");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [failingTool, fallbackTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:first",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "failing_tool",
                                    OnFailure = "fallback"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Tool = "fallback_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-on-failure");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("fallback ok", result);
            Assert.Equal(1, fallbackTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_OnFailure_HandlesLlmFailureAsStructuredStepFailure()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-on-failure-llm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var fallbackTool = new CountingMafTool("fallback_tool", "fallback ok");
            var runtime = CreateRuntime(
                storagePath,
                new ThrowingTestLlmExecutionService(),
                new MafOptions(),
                tools: [fallbackTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:first",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "llm_chat",
                                    OnFailure = "fallback"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Tool = "fallback_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-on-failure-llm");

            var result = await InvokeMafMetaSkillWithExecutionContextAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("fallback ok", result);
            Assert.Equal(1, fallbackTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_OnFailureFallbackUserInputResume_MirrorsOutputToPrimaryStep()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-on-failure-resume-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var failingTool = new ThrowingMafTool("failing_tool", "boom");
            var postTool = new CountingMafTool("post_tool", "completed");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [failingTool, postTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:finish",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "failing_tool",
                                    OnFailure = "ask"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    WithJson = "{\"prompt\":\"Please recover\"}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "finish",
                                    Kind = "tool_call",
                                    Tool = "post_tool",
                                    DependsOn = ["first"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-on-failure-resume");

            var paused = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);
            Assert.Contains("requires user input", paused, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(session.MetaExecutionCheckpoint);
            Assert.Equal(0, postTool.CallCount);

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "approved", TestContext.Current.CancellationToken);

            Assert.Equal("completed", resumed);
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(1, postTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClarifyTimeout_OnFailureEmptyResume_ExecutesFallback()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-clarify-timeout-fallback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var fallbackTool = new CountingMafTool("fallback_tool", "fallback complete");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [fallbackTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:finish",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "ask",
                                    Kind = "user_input",
                                    OnFailure = "finish",
                                    Clarify = new MetaClarifySchema
                                    {
                                        Mode = "chat",
                                        TimeoutSeconds = 1,
                                        Fields = [new MetaClarifyField { Name = "topic", Type = "string", Required = true }]
                                    }
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "finish",
                                    Kind = "tool_call",
                                    Tool = "fallback_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-clarify-timeout-fallback");
            session.MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
            {
                SkillName = "meta-flow",
                PendingStepId = "ask",
                Prompt = "Please provide input for step 'ask'.",
                CreatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5),
                LastUpdatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5),
                PendingStepIds = ["ask", "finish"],
                BlockedStepIds = [],
                Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                StepResults = []
            };

            var resumed = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", string.Empty, TestContext.Current.CancellationToken);

            Assert.Equal("fallback complete", resumed);
            Assert.Null(session.MetaExecutionCheckpoint);
            Assert.Equal(1, fallbackTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_RetryPolicy_RetriesToolStepUntilItSucceeds()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-retry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var flakyTool = new FlakyMafTool("flaky_tool", failAttempts: 2, result: "ok after retry");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [flakyTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "flaky_tool",
                                    Retry = new MetaStepRetryPolicy { MaxAttempts = 3 }
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-retry");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("ok after retry", result);
            Assert.Equal(3, flakyTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_TimeoutPolicy_PassesStepScopedCancellationToken()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-timeout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var cancellationTool = new CancellationAwareMafTool("cancellable_tool");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [cancellationTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "cancellable_tool",
                                    TimeoutSeconds = 1
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-timeout");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("timed out", result);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_OutputContractFailure_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-output-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    OutputContract = new MetaStepOutputContract
                                    {
                                        Format = "json",
                                        RequiredProperties = ["answer"]
                                    }
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-output-contract");

            var result = await InvokeMafMetaSkillWithExecutionContextAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("output_contract_failed", doc.RootElement.GetProperty("error_code").GetString());
            var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Single(steps);
            Assert.Equal("draft", steps[0].GetProperty("id").GetString());
            Assert.Equal("failed", steps[0].GetProperty("status").GetString());
            Assert.Equal("output_contract_failed", steps[0].GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_ClassifyRoute_BlocksUnmatchedBranch()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-classify-route-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var chosenTool = new CountingMafTool("chosen_tool", "chosen");
            var skippedTool = new CountingMafTool("skipped_tool", "skipped");
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [chosenTool, skippedTool],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "step:chosen",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "classify",
                                    Kind = "llm_classify",
                                    WithJson = "{\"options\":[\"ok\",\"other\"],\"route\":{\"ok\":\"chosen\",\"other\":\"skipped\"}}"
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "chosen",
                                    Kind = "tool_call",
                                    Tool = "chosen_tool",
                                    DependsOn = ["classify"]
                                },
                                new MetaSkillStepDefinition
                                {
                                    Id = "skipped",
                                    Kind = "tool_call",
                                    Tool = "skipped_tool",
                                    DependsOn = ["classify"]
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-classify-route");

            var result = await InvokeMafMetaSkillWithExecutionContextAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            Assert.Equal("chosen", result);
            Assert.Equal(1, chosenTool.CallCount);
            Assert.Equal(0, skippedTool.CallCount);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_StructuredMode_UnsupportedKind_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "weird",
                                    Kind = "unknown_step"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-unsupported-kind");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("meta-flow", doc.RootElement.GetProperty("skill").GetString());
            Assert.Contains("unsupported kind", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("unsupported_step_kind", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("steps").EnumerateArray());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_StructuredMode_MissingToolDeclaration_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-missing-tool-declaration");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("meta-flow", doc.RootElement.GetProperty("skill").GetString());
            Assert.Contains("does not declare a tool", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("invalid_tool_step", doc.RootElement.GetProperty("error_code").GetString());
            Assert.Empty(doc.RootElement.GetProperty("steps").EnumerateArray());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_ExecuteMetaSkillAsync_StructuredMode_CapabilityDenied_ReturnsStructuredError()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-meta-structured-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(
                storagePath,
                new TestLlmExecutionService(),
                new MafOptions(),
                tools: [new TestTool("blocked_tool")],
                skills:
                [
                    new SkillDefinition
                    {
                        Name = "meta-flow",
                        Description = "meta flow",
                        Instructions = "...",
                        Location = "/skills/meta-flow",
                        Kind = SkillKind.Meta,
                        FinalTextMode = "structured",
                        Metadata = new SkillMetadata
                        {
                            Capabilities = ["tool:allowed_tool"],
                            Risk = "high"
                        },
                        Composition = new MetaSkillComposition
                        {
                            Steps =
                            [
                                new MetaSkillStepDefinition
                                {
                                    Id = "first",
                                    Kind = "tool_call",
                                    Tool = "blocked_tool"
                                }
                            ]
                        }
                    }
                ]);
            var session = CreateSession("maf-meta-structured-capability-denied");

            var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-flow", "hello", TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(result);
            Assert.Contains("not permitted by metadata capabilities", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("metadata_capability_denied", doc.RootElement.GetProperty("error_code").GetString());
            var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToArray();
            Assert.Single(steps);
            Assert.Equal("blocked", steps[0].GetProperty("status").GetString());
            Assert.Equal("metadata_capability_denied", steps[0].GetProperty("failure_code").GetString());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_PersistsRouteModelTierForNextTurn()
    {
        var observedPreviousTiers = new List<string?>();
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<TurnRoutingRequest>();
                observedPreviousTiers.Add(request.Session.RouteModelTier);
                return new TurnRoutingDecision
                {
                    Tier = observedPreviousTiers.Count == 1 ? "T3" : "T1",
                    Reason = observedPreviousTiers.Count == 1 ? "first_route" : "second_route"
                };
            });

        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sticky-tier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = CreateRuntime(storagePath, new TestLlmExecutionService(), new MafOptions(), routingPolicy: routing);
            var session = CreateSession("maf-sticky-tier");

            await runtime.RunAsync(session, "first", TestContext.Current.CancellationToken);
            await runtime.RunAsync(session, "second", TestContext.Current.CancellationToken);

            Assert.Equal([null, "T3"], observedPreviousTiers);
            Assert.Equal("T1", session.RouteModelTier);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_EmptyRoutingAllowlist_DoesNotOverrideManualSessionAllowlist()
    {
        var routing = Substitute.For<ITurnRoutingPolicy>();
        routing.ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnRoutingDecision
            {
                Tier = "T1",
                ModelProfileId = "mini-readonly",
                AllowedTools = [],
                SystemPromptSuffix = "Keep the reply short and skip planning.",
                Reason = "simple_read_only"
            });

        var services = new ServiceCollection()
            .AddSingleton(routing)
            .BuildServiceProvider();
        var executionService = new CapturingLlmExecutionService();
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-routing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = new MafAgentRuntime(
                new AgentRuntimeFactoryContext
                {
                    Services = services,
                    Config = new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        },
                        Llm = new LlmProviderConfig
                        {
                            Provider = "test-maf",
                            Model = "maf-test-model"
                        }
                    },
                    RuntimeState = new GatewayRuntimeState
                    {
                        RequestedMode = "jit",
                        EffectiveMode = GatewayRuntimeMode.Jit,
                        DynamicCodeSupported = true
                    },
                    ChatClient = new MafTestChatClient(),
                    Tools = [new TestTool("echo_tool"), new TestTool("shell")],
                    MemoryStore = new FileMemoryStore(storagePath, 4),
                    RuntimeMetrics = new RuntimeMetrics(),
                    ProviderUsage = new ProviderUsageTracker(),
                    LlmExecutionService = executionService,
                    Skills = [],
                    SkillsConfig = new SkillsConfig(),
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
                },
                new MafOptions(),
                new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(
                    new GatewayConfig
                    {
                        Memory = new MemoryConfig
                        {
                            StoragePath = storagePath
                        }
                    },
                    Options.Create(new MafOptions()),
                    NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(),
                NullLogger<MafAgentRuntime>.Instance);
            var session = CreateSession("maf-empty-routing-allowlist");
            session.RouteAllowedTools = ["shell"];
            session.SystemPromptOverride = "Original route prompt";

            await runtime.RunAsync(session, "Open README.md", TestContext.Current.CancellationToken);

            Assert.Equal(["shell"], executionService.LastToolNames);
            await routing.Received(1).ResolveAsync(Arg.Any<TurnRoutingRequest>(), Arg.Any<CancellationToken>());
            Assert.Equal(["shell"], session.RouteAllowedTools);
            Assert.Equal("Original route prompt", session.SystemPromptOverride);
            Assert.Equal("T1", session.RouteModelTier);
            Assert.Null(session.RouteReason);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHashMismatch_RebuildsFreshSession()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-mismatch");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, TestContext.Current.CancellationToken);

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "history changed"
            });

            var loadedSession = await store.LoadAsync(agent, session, TestContext.Current.CancellationToken);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, TestContext.Current.CancellationToken));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(TestContext.Current.CancellationToken), jsonSerializerOptions: null, TestContext.Current.CancellationToken));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_CorruptedSidecar_RebuildsFreshSession()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-corrupt");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(store.GetSessionPath(session.Id), "{not-json", TestContext.Current.CancellationToken);

            var loadedSession = await store.LoadAsync(agent, session, TestContext.Current.CancellationToken);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, TestContext.Current.CancellationToken));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(TestContext.Current.CancellationToken), jsonSerializerOptions: null, TestContext.Current.CancellationToken));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_SaveFailure_CleansUpTempFile()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-save-cleanup");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);
            var path = store.GetSessionPath(session.Id);

            Directory.CreateDirectory(path);

            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(agent, session, agentSession, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void MafSessionStateStore_HistoryHash_ChangesWhenModelOverrideChanges()
    {
        var session = CreateSession("maf-hash");
        var baseline = MafSessionStateStore.ComputeHistoryHash(session);
        session.ModelOverride = "gpt-maf";

        var updated = MafSessionStateStore.ComputeHistoryHash(session);

        Assert.NotEqual(baseline, updated);
    }

    [Fact]
    public void MafSessionStateStore_HistoryHash_ChangesWhenModelRoutingFieldsChange()
    {
        var session = CreateSession("maf-route-hash");
        var baseline = MafSessionStateStore.ComputeHistoryHash(session);

        session.ModelProfileId = "mini-readonly";
        var withProfile = MafSessionStateStore.ComputeHistoryHash(session);
        session.ModelProfileId = null;
        session.PreferredModelTags = ["local", "fast"];
        var withTags = MafSessionStateStore.ComputeHistoryHash(session);

        Assert.NotEqual(baseline, withProfile);
        Assert.NotEqual(baseline, withTags);
    }

    [Fact]
    public void MafSessionStateStore_HistoryHash_NormalizesPreferredModelTagOrder()
    {
        var session = CreateSession("maf-route-tag-hash");
        session.PreferredModelTags = ["local", "fast"];
        var baseline = MafSessionStateStore.ComputeHistoryHash(session);

        session.PreferredModelTags = ["FAST", "local"];
        var reordered = MafSessionStateStore.ComputeHistoryHash(session);

        Assert.Equal(baseline, reordered);
    }

    [Fact]
    public void MafSessionStateStore_NormalizeSidecarPath_StripsRootedPath()
    {
        var normalized = MafSessionStateStore.NormalizeSidecarPath(Path.DirectorySeparatorChar + Path.Join("maf", "sessions"));

        Assert.Equal(Path.Join("maf", "sessions"), normalized);
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHash_RemainsStableAcrossFileSessionReload()
    {
        var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var session = CreateSession("maf-file-hash");
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls =
                [
                    new ToolInvocation
                    {
                        ToolName = "memory",
                        Arguments = """{"action":"write","key":"note","content":"hello"}""",
                        Result = "Saved note: note",
                        Duration = TimeSpan.FromMilliseconds(12)
                    }
                ]
            });
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "Tool said: Saved note: note"
            });

            var expectedHash = MafSessionStateStore.ComputeHistoryHash(session);

            var writerStore = new FileMemoryStore(storagePath, 4);
            await writerStore.SaveSessionAsync(session, TestContext.Current.CancellationToken);

            var readerStore = new FileMemoryStore(storagePath, 4);
            var loaded = await readerStore.GetSessionAsync(session.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal(expectedHash, MafSessionStateStore.ComputeHistoryHash(loaded!));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }


    private static MafAgentRuntime CreateRuntime(
        string storagePath,
        ILlmExecutionService executionService,
        MafOptions options,
        List<string>? sessionStoreLogs = null,
        ITurnRoutingPolicy? routingPolicy = null,
        IReadOnlyList<ITool>? tools = null,
        IReadOnlyList<SkillDefinition>? skills = null,
        SkillsConfig? skillsConfig = null)
    {
        var serviceCollection = new ServiceCollection();
        if (routingPolicy is not null)
            serviceCollection.AddSingleton(routingPolicy);
        var services = serviceCollection.BuildServiceProvider();
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Llm = new LlmProviderConfig
            {
                Provider = "test-maf",
                Model = "maf-test-model"
            }
        };

        return new MafAgentRuntime(
            new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                ChatClient = new MafTestChatClient(),
                Tools = tools ?? [],
                MemoryStore = new FileMemoryStore(storagePath, 4),
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = executionService,
                Skills = skills ?? [],
                SkillsConfig = skillsConfig ?? new SkillsConfig(),
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
            },
            options,
            new MafAgentFactory(Options.Create(options), NullLoggerFactory.Instance, services),
            new MafSessionStateStore(
                config,
                Options.Create(options),
                sessionStoreLogs is null
                    ? NullLogger<MafSessionStateStore>.Instance
                    : new CapturingLogger<MafSessionStateStore>(sessionStoreLogs)),
            new MafTelemetryAdapter(),
            NullLogger<MafAgentRuntime>.Instance);
    }

    private static async Task<string> InvokeMafMetaSkillAsync(
        MafAgentRuntime runtime,
        Session session,
        string skillName,
        string input,
        CancellationToken ct)
    {
        var method = typeof(MafAgentRuntime).GetMethod("ExecuteMetaSkillAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(runtime, [session, skillName, input, ct]) as Task<string>;
        Assert.NotNull(task);
        return await task!;
    }

    private static async Task<string> InvokeMafMetaSkillWithExecutionContextAsync(
        MafAgentRuntime runtime,
        Session session,
        string skillName,
        string input,
        CancellationToken ct)
    {
        using var scope = AgentExecutionContextScope.Push(new AgentExecutionContext
        {
            Session = session,
            TurnContext = new TurnContext
            {
                SessionId = session.Id,
                ChannelId = session.ChannelId
            },
            SystemPromptLength = 0,
            SkillPromptLength = 0,
            SessionTokenBudget = 0,
            ToolInvocations = []
        });

        return await InvokeMafMetaSkillAsync(runtime, session, skillName, input, ct);
    }

    private static void RemoveMafToolMapping(MafAgentRuntime runtime, string toolName)
    {
        var field = typeof(MafAgentRuntime).GetField("_mafToolsByName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);

        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, AITool>>(field!.GetValue(runtime));
        var replacement = map
            .Where(pair => !string.Equals(pair.Key, toolName, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        field.SetValue(runtime, replacement);
    }

    private static MafSessionStateStore CreateStore(string storagePath)
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            }
        };

        return new MafSessionStateStore(
            config,
            Options.Create(new MafOptions()),
            NullLogger<MafSessionStateStore>.Instance);
    }

    private static ChatClientAgent CreateAgent()
    {
        var factory = new MafAgentFactory(
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

        return factory.Create(new MafTestChatClient(), "Test instructions", []);
    }

    private static Session CreateSession(string sessionId)
    {
        var session = new Session
        {
            Id = sessionId,
            ChannelId = "test",
            SenderId = "user"
        };
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "hello"
        });
        return session;
    }

    private static List<ITool> BuildCreatorToolSetForMafTests() =>
    [
        new EmitTextTool(),
        new MetaSkillFillSlotsTool(),
        new MetaSkillAssembleTool(),
        new MetaSkillLintRunTool(),
        new MetaSkillSmokeRunTool(),
        new MetaSkillRuntimeE2ERunTool(),
        new MetaSkillPersistProposalTool()
    ];

    private static SkillDefinition CreateMetaSkillCreatorTestDefinition(bool fullGated)
    {
        var persistHome = Path.Combine(Path.GetTempPath(), "openclaw-meta-creator-tests", Guid.NewGuid().ToString("N"));
        var persistHomeJson = persistHome.Replace("\\", "\\\\", StringComparison.Ordinal);

        var steps = new List<MetaSkillStepDefinition>
        {
            new()
            {
                Id = "fill_slots",
                Kind = "tool_call",
                Tool = "meta_skill_fill_slots",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"history_summary\":\"recent usage\",\"user_intent\":\"create a meta-skill\"}"
            },
            new()
            {
                Id = "assemble",
                Kind = "tool_call",
                Tool = "meta_skill_assemble",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"slots_json\":\"{\\\"name\\\":\\\"meta-demo\\\",\\\"description\\\":\\\"Generated meta workflow for deterministic creator parity.\\\",\\\"meta_priority\\\":50,\\\"triggers\\\":[\\\"create a meta-skill\\\"],\\\"steps\\\":[{\\\"id\\\":\\\"gather\\\",\\\"skill\\\":\\\"history-explorer\\\",\\\"task\\\":\\\"Collect context\\\",\\\"with_keys\\\":{}},{\\\"id\\\":\\\"synthesize\\\",\\\"skill\\\":\\\"summarize\\\",\\\"task\\\":\\\"Build answer\\\",\\\"with_keys\\\":{}}]}\"}",
                DependsOn = ["fill_slots"]
            },
            new()
            {
                Id = "lint",
                Kind = "tool_call",
                Tool = "meta_skill_lint_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator lint fixture with valid composition.\\\"\\nkind: meta\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n---\\nbody\\n\"}",
                DependsOn = ["assemble"]
            }
        };

        if (fullGated)
        {
            steps.Add(new MetaSkillStepDefinition
            {
                Id = "smoke",
                Kind = "tool_call",
                Tool = "meta_skill_smoke_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator smoke fixture with trigger.\\\"\\nkind: meta\\ntriggers:\\n  - \\\"create a meta-skill\\\"\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n---\\nbody\\n\"}",
                DependsOn = ["lint"]
            });
            steps.Add(new MetaSkillStepDefinition
            {
                Id = "runtime_e2e",
                Kind = "tool_call",
                Tool = "meta_skill_runtime_e2e_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\"}",
                DependsOn = ["smoke"]
            });
            steps.Add(new MetaSkillStepDefinition
            {
                Id = "persist",
                Kind = "tool_call",
                Tool = "meta_skill_persist_proposal",
                ToolArgsJson = $"{{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\",\"lint_result\":\"{{}}\",\"smoke_result\":\"{{}}\",\"home\":\"{persistHomeJson}\"}}",
                DependsOn = ["runtime_e2e"]
            });
        }

        steps.Add(new MetaSkillStepDefinition
        {
            Id = "preview",
            Kind = "tool_call",
            Tool = "emit_text",
            ToolArgsJson = "{\"text\":\"proposal preview ready\"}",
            DependsOn = fullGated ? ["persist"] : ["lint"]
        });

        return new SkillDefinition
        {
            Name = "meta-skill-creator",
            Description = "meta skill creator dependency parity test definition",
            Instructions = "...",
            Location = "/skills/meta-skill-creator",
            Kind = SkillKind.Meta,
            FinalTextMode = "step:preview",
            Composition = new MetaSkillComposition
            {
                Steps = steps
            }
        };
    }

    private static SkillDefinition CreateMetaSkillCreatorLintFailureTestDefinition()
    {
        var persistHome = Path.Combine(Path.GetTempPath(), "openclaw-meta-creator-tests", Guid.NewGuid().ToString("N"));
        var persistHomeJson = persistHome.Replace("\\", "\\\\", StringComparison.Ordinal);

        const string lintSkillMd = "---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator lint fixture with invalid dependency.\\\"\\nkind: meta\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n    - id: synthesize\\n      skill: \\\"summarize\\\"\\n      depends_on: [missing_step]\\n      with:\\n        task: \\\"Build answer\\\"\\n---\\nbody\\n";

        var steps = new List<MetaSkillStepDefinition>
        {
            new()
            {
                Id = "fill_slots",
                Kind = "tool_call",
                Tool = "meta_skill_fill_slots",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"history_summary\":\"recent usage\",\"user_intent\":\"create a meta-skill\"}"
            },
            new()
            {
                Id = "assemble",
                Kind = "tool_call",
                Tool = "meta_skill_assemble",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"slots_json\":\"{\\\"name\\\":\\\"meta-demo\\\",\\\"description\\\":\\\"Generated meta workflow for deterministic creator parity.\\\",\\\"meta_priority\\\":50,\\\"triggers\\\":[\\\"create a meta-skill\\\"],\\\"steps\\\":[{\\\"id\\\":\\\"gather\\\",\\\"skill\\\":\\\"history-explorer\\\",\\\"task\\\":\\\"Collect context\\\",\\\"with_keys\\\":{}},{\\\"id\\\":\\\"synthesize\\\",\\\"skill\\\":\\\"summarize\\\",\\\"task\\\":\\\"Build answer\\\",\\\"with_keys\\\":{}}]}\"}",
                DependsOn = ["fill_slots"]
            },
            new()
            {
                Id = "lint",
                Kind = "tool_call",
                Tool = "meta_skill_lint_run",
                ToolArgsJson = $"{{\"skill_md\":\"{lintSkillMd}\"}}",
                DependsOn = ["assemble"]
            },
            new()
            {
                Id = "smoke",
                Kind = "tool_call",
                Tool = "meta_skill_smoke_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator smoke fixture with trigger.\\\"\\nkind: meta\\ntriggers:\\n  - \\\"create a meta-skill\\\"\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n---\\nbody\\n\"}",
                DependsOn = ["lint"]
            },
            new()
            {
                Id = "runtime_e2e",
                Kind = "tool_call",
                Tool = "meta_skill_runtime_e2e_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\"}",
                DependsOn = ["smoke"]
            },
            new()
            {
                Id = "persist",
                Kind = "tool_call",
                Tool = "meta_skill_persist_proposal",
                ToolArgsJson = $"{{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\",\"lint_result\":\"{{}}\",\"smoke_result\":\"{{}}\",\"home\":\"{persistHomeJson}\"}}",
                DependsOn = ["runtime_e2e"]
            },
            new()
            {
                Id = "preview",
                Kind = "tool_call",
                Tool = "emit_text",
                ToolArgsJson = "{\"text\":\"proposal preview ready\"}",
                DependsOn = ["persist"]
            }
        };

        return new SkillDefinition
        {
            Name = "meta-skill-creator",
            Description = "meta skill creator lint failure test definition",
            Instructions = "...",
            Location = "/skills/meta-skill-creator",
            Kind = SkillKind.Meta,
            FinalTextMode = "step:preview",
            Composition = new MetaSkillComposition
            {
                Steps = steps
            }
        };
    }

    private static SkillDefinition CreateMetaSkillCreatorPersistFailureTestDefinition()
    {
        var persistHome = Path.Combine(Path.GetTempPath(), "openclaw-meta-creator-tests", Guid.NewGuid().ToString("N"));
        var persistHomeJson = persistHome.Replace("\\", "\\\\", StringComparison.Ordinal);

        // Persist step omits lint_result and smoke_result to trigger tool-level error
        var steps = new List<MetaSkillStepDefinition>
        {
            new()
            {
                Id = "fill_slots",
                Kind = "tool_call",
                Tool = "meta_skill_fill_slots",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"history_summary\":\"recent usage\",\"user_intent\":\"create a meta-skill\"}"
            },
            new()
            {
                Id = "assemble",
                Kind = "tool_call",
                Tool = "meta_skill_assemble",
                ToolArgsJson = "{\"pattern_id\":\"p1_sequential\",\"slots_json\":\"{\\\"name\\\":\\\"meta-demo\\\",\\\"description\\\":\\\"Generated meta workflow for deterministic creator parity.\\\",\\\"meta_priority\\\":50,\\\"triggers\\\":[\\\"create a meta-skill\\\"],\\\"steps\\\":[{\\\"id\\\":\\\"gather\\\",\\\"skill\\\":\\\"history-explorer\\\",\\\"task\\\":\\\"Collect context\\\",\\\"with_keys\\\":{}},{\\\"id\\\":\\\"synthesize\\\",\\\"skill\\\":\\\"summarize\\\",\\\"task\\\":\\\"Build answer\\\",\\\"with_keys\\\":{}}]}\"}",
                DependsOn = ["fill_slots"]
            },
            new()
            {
                Id = "lint",
                Kind = "tool_call",
                Tool = "meta_skill_lint_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator lint fixture with valid composition.\\\"\\nkind: meta\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n---\\nbody\\n\"}",
                DependsOn = ["assemble"]
            },
            new()
            {
                Id = "smoke",
                Kind = "tool_call",
                Tool = "meta_skill_smoke_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: \\\"meta-demo\\\"\\ndescription: \\\"Generated creator smoke fixture with trigger.\\\"\\nkind: meta\\ntriggers:\\n  - \\\"create a meta-skill\\\"\\ncomposition:\\n  steps:\\n    - id: gather\\n      skill: \\\"history-explorer\\\"\\n      with:\\n        task: \\\"Collect context\\\"\\n---\\nbody\\n\"}",
                DependsOn = ["lint"]
            },
            new()
            {
                Id = "runtime_e2e",
                Kind = "tool_call",
                Tool = "meta_skill_runtime_e2e_run",
                ToolArgsJson = "{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\"}",
                DependsOn = ["smoke"]
            },
            new()
            {
                Id = "persist",
                Kind = "tool_call",
                Tool = "meta_skill_persist_proposal",
                ToolArgsJson = $"{{\"skill_md\":\"---\\nname: meta-demo\\nkind: meta\\n---\\n\",\"home\":\"{persistHomeJson}\"}}",
                DependsOn = ["runtime_e2e"]
            },
            new()
            {
                Id = "preview",
                Kind = "tool_call",
                Tool = "emit_text",
                ToolArgsJson = "{\"text\":\"proposal preview ready\"}",
                DependsOn = ["persist"]
            }
        };

        return new SkillDefinition
        {
            Name = "meta-skill-creator",
            Description = "meta skill creator persist failure test definition",
            Instructions = "...",
            Location = "/skills/meta-skill-creator",
            Kind = SkillKind.Meta,
            FinalTextMode = "step:preview",
            Composition = new MetaSkillComposition
            {
                Steps = steps
            }
        };
    }

    private static async Task<AgentSession> CreatePopulatedAgentSessionAsync(ChatClientAgent agent)
    {
        var agentSession = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        _ = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "hello from maf sidecar")],
            agentSession,
            new ChatClientAgentRunOptions(new ChatOptions()),
            TestContext.Current.CancellationToken);
        return agentSession;
    }

    private static string NormalizeJson(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = false });

    private sealed class TestTool(string name = "echo_tool") : ITool
    {
        public string Name => name;

        public string Description => "Echo test tool.";

        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            return ValueTask.FromResult("ok");
        }
    }

    private sealed class CountingMafTool(string name, string result) : ITool
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public string Name => name;
        public string Description => "Counting test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MafConcurrencyTracker
    {
        private int _current;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _current);
            while (true)
            {
                var snapshot = Volatile.Read(ref _max);
                if (current <= snapshot)
                    break;

                if (Interlocked.CompareExchange(ref _max, current, snapshot) == snapshot)
                    break;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class ConcurrentProbeMafTool(string name, string result, MafConcurrencyTracker tracker, int delayMs) : ITool
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public string Name => name;
        public string Description => "Concurrent probe test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            Interlocked.Increment(ref _callCount);
            tracker.Enter();
            try
            {
                await Task.Delay(delayMs, ct);
                return result;
            }
            finally
            {
                tracker.Exit();
            }
        }
    }

    private sealed class EchoArgumentsMafTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "Echo arguments test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = ct;
            return ValueTask.FromResult(argumentsJson);
        }
    }

    private sealed class ThrowingMafTool(string name, string message) : ITool
    {
        public string Name => name;
        public string Description => "Throwing test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FlakyMafTool(string name, int failAttempts, string result) : ITool
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public string Name => name;
        public string Description => "Flaky test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            var callCount = Interlocked.Increment(ref _callCount);
            if (callCount <= failAttempts)
                throw new InvalidOperationException("transient");

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellationAwareMafTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "Cancellation-aware test tool.";
        public string ParameterSchema => """{"type":"object"}""";

        public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                return "not-timeout";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return "timed out";
            }
        }
    }

    private sealed class TestToolPresetResolver(IEnumerable<string> allowedTools) : IToolPresetResolver
    {
        private readonly ResolvedToolPreset _preset = new()
        {
            PresetId = "test-preset",
            AllowedTools = allowedTools.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        public ResolvedToolPreset Resolve(Session session, IEnumerable<string> availableToolNames)
        {
            _ = session;
            _ = availableToolNames;
            return _preset;
        }

        public IReadOnlyList<ResolvedToolPreset> ListPresets(IEnumerable<string> availableToolNames)
        {
            _ = availableToolNames;
            return [_preset];
        }
    }

    private sealed class CapturingLlmExecutionService : ILlmExecutionService
    {
        public IReadOnlyList<string> LastToolNames { get; private set; } = [];

        public IReadOnlyList<string> LastPreferredModelTags { get; private set; } = [];

        public IReadOnlyList<string> LastFallbackModelProfileIds { get; private set; } = [];

        public string? LastReasoningEffort { get; private set; }

        public string LastResponseMode { get; private set; } = SessionResponseModes.Default;

        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = messages;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            LastToolNames = options.Tools?.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? [];
            LastPreferredModelTags = session.PreferredModelTags;
            LastFallbackModelProfileIds = session.FallbackModelProfileIds;
            LastReasoningEffort = session.ReasoningEffort;
            LastResponseMode = session.ResponseMode;
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = AsyncEnumerable.Empty<ChatResponseUpdate>()
            });
        }
    }

    private sealed class SequencedLlmExecutionService(params string[] responses) : ILlmExecutionService
    {
        private int _responseIndex;

        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;

            var index = Interlocked.Increment(ref _responseIndex) - 1;
            var response = index < responses.Length ? responses[index] : responses[^1];
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, response)])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = AsyncEnumerable.Empty<ChatResponseUpdate>()
            });
        }
    }


    private sealed class CapturingLogger<T>(List<string> messages) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = exception;
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class BlockingStreamingLlmExecutionService : ILlmExecutionService
    {
        public TaskCompletionSource ProducerBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ProducerCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCancelledProducerToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = GetUpdates(ct)
            });
        }

        private async IAsyncEnumerable<ChatResponseUpdate> GetUpdates([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            ProducerBlocked.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ProducerCancellationObserved.SetResult();
                await AllowCancelledProducerToComplete.Task;
                throw;
            }
        }
    }

    private sealed class StreamingTestLlmExecutionService : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = GetUpdates()
            });
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> GetUpdates()
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    private sealed class TestLlmExecutionService : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = AsyncEnumerable.Empty<ChatResponseUpdate>()
            });
        }
    }

    private sealed class ThrowingTestLlmExecutionService : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromException<LlmExecutionResult>(new InvalidOperationException("provider boom"));
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromException<LlmStreamingExecutionResult>(new InvalidOperationException("provider boom"));
        }
    }

    private sealed class MafTestChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            _ = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
