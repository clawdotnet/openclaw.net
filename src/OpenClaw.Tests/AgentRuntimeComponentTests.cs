using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentRuntimeComponentTests
{
    [Fact]
    public void PromptContextAssembler_BuildMessages_ReconstructsMediaAndCheckpointToolBatch()
    {
        var assembler = new AgentPromptContextAssembler(
            Substitute.For<IMemoryStore>(),
            requireToolApproval: false,
            recall: null,
            profileStore: null,
            profilesConfig: null,
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null);
        assembler.ApplySkills([], skillsInstructionPrompt: null);

        var session = new Session
        {
            Id = "sess-components",
            ChannelId = "websocket",
            SenderId = "user",
            SystemPromptOverride = "Answer as the routed test agent."
        };
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "What is in this image?\n[IMAGE_URL:data:image/png;base64,AAAA]"
        });
        session.History.Add(new ChatTurn
        {
            Role = "assistant",
            Content = "[tool_use]",
            ToolCalls =
            [
                new ToolInvocation
                {
                    CallId = "call_resume_1",
                    ToolName = "demo_tool",
                    Arguments = """{"value":"one"}""",
                    Result = "tool result"
                }
            ]
        });

        var messages = assembler.BuildMessages(session, maxHistoryTurns: 10, exactLatestToolBatch: true);

        Assert.Contains("[Route Instructions]", messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("routed test agent", messages[0].Text, StringComparison.Ordinal);
        var userMessage = messages.Single(message => message.Role == ChatRole.User);
        Assert.Contains(userMessage.Contents.OfType<TextContent>(), content => content.Text.Contains("What is in this image?", StringComparison.Ordinal));
        Assert.Contains(userMessage.Contents.OfType<UriContent>(), content => content.Uri.ToString() == "data:image/png;base64,AAAA");
        Assert.Contains(messages, message =>
            message.Role == ChatRole.Assistant &&
            message.Contents.OfType<FunctionCallContent>().Any(content =>
                content.CallId == "call_resume_1" &&
                content.Name == "demo_tool"));
        Assert.Contains(messages, message =>
            message.Role == ChatRole.Tool &&
            message.Contents.OfType<FunctionResultContent>().Any(content =>
                content.CallId == "call_resume_1" &&
                string.Equals(content.Result?.ToString(), "tool result", StringComparison.Ordinal)));
    }

    [Fact]
    public void PromptContextAssembler_ApplySkills_PublishesSnapshot()
    {
        var assembler = new AgentPromptContextAssembler(
            Substitute.For<IMemoryStore>(),
            requireToolApproval: false,
            recall: null,
            profileStore: null,
            profilesConfig: null,
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null);
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "alpha",
                Description = "Alpha skill",
                Instructions = "Use alpha.",
                Location = "/tmp/alpha"
            }
        };

        assembler.ApplySkills(skills, skillsInstructionPrompt: null);
        skills.Add(new SkillDefinition
        {
            Name = "beta",
            Description = "Beta skill",
            Instructions = "Use beta.",
            Location = "/tmp/beta"
        });

        var loaded = Assert.Single(assembler.LoadedSkills);
        Assert.Equal("alpha", loaded.Name);
        Assert.Equal(["alpha"], assembler.LoadedSkillNames);
    }

    [Fact]
    public void PromptContextAssembler_Constructor_FailsFastForEnabledUnsupportedSources()
    {
        var memory = Substitute.For<IMemoryStore>();

        var recallError = Assert.Throws<ArgumentException>(() => new AgentPromptContextAssembler(
            memory,
            requireToolApproval: false,
            recall: new MemoryRecallConfig { Enabled = true },
            profileStore: null,
            profilesConfig: null,
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null));
        Assert.Contains("IMemoryNoteSearch", recallError.Message, StringComparison.Ordinal);

        var profileError = Assert.Throws<ArgumentException>(() => new AgentPromptContextAssembler(
            memory,
            requireToolApproval: false,
            recall: null,
            profileStore: null,
            profilesConfig: new ProfilesConfig { Enabled = true, InjectRecall = true },
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null));
        Assert.Contains("_profileStore", profileError.Message, StringComparison.Ordinal);

        var fractalError = Assert.Throws<ArgumentException>(() => new AgentPromptContextAssembler(
            memory,
            requireToolApproval: false,
            recall: null,
            profileStore: null,
            profilesConfig: null,
            contextBudgetPlanner: null,
            fractalMemory: new FractalMemoryConfig { Enabled = true, AutoContextMode = "auto" },
            metrics: null,
            logger: null,
            memoryRecallPrefix: null));
        Assert.Contains("_contextBudgetPlanner", fractalError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptContextAssembler_RecallInsertion_PreservesIndexOrder()
    {
        var memory = Substitute.For<IMemoryStore, IMemoryNoteSearch>();
        var search = (IMemoryNoteSearch)memory;
        search.SearchNotesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<MemoryNoteHit>>(
            [
                new() { Key = "note:1", Content = "memory result", UpdatedAt = DateTimeOffset.UtcNow, Score = 1 }
            ]));

        var fractal = new FractalMemoryConfig
        {
            Enabled = true,
            AutoContextMode = "auto",
            MaxContextChars = 4_000,
            MaxContextTokens = 1_000
        };
        var gatewayConfig = new GatewayConfig
        {
            Memory = new MemoryConfig { Fractal = fractal }
        };
        var structuredMemory = Substitute.For<IStructuredMemoryProvider>();
        structuredMemory.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StructuredMemorySearchResult
            {
                Success = true,
                Items = [new StructuredMemorySourceRef { Path = "memory/topic" }]
            }));
        structuredMemory.ExportAsync("memory/topic", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StructuredMemoryExportResult
            {
                Success = true,
                Path = "memory/topic",
                Mode = "compact",
                Content = "structured memory result"
            }));
        var contextPlanner = new ContextBudgetPlanner(gatewayConfig, structuredMemory);

        var profileStore = Substitute.For<IUserProfileStore>();
        profileStore.GetProfileAsync("websocket:user", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<UserProfile?>(new UserProfile
            {
                ActorId = "websocket:user",
                ChannelId = "websocket",
                SenderId = "user",
                Summary = "profile result"
            }));

        var assembler = new AgentPromptContextAssembler(
            memory,
            requireToolApproval: false,
            recall: new MemoryRecallConfig { Enabled = true, MaxNotes = 5, MaxChars = 4_000 },
            profileStore,
            profilesConfig: new ProfilesConfig { Enabled = true, InjectRecall = true, MaxRecallChars = 2_000 },
            contextBudgetPlanner: contextPlanner,
            fractalMemory: fractal,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null);
        assembler.ApplySkills([], skillsInstructionPrompt: null);

        var session = new Session { Id = "sess-recall", ChannelId = "websocket", SenderId = "user" };
        session.History.Add(new ChatTurn { Role = "user", Content = "what should I remember?" });
        var messages = assembler.BuildMessages(session, maxHistoryTurns: 10);

        var memoryInjected = await assembler.TryInjectRecallAsync(messages, "what should I remember?", CancellationToken.None);
        await assembler.TryInjectStructuredMemoryContextAsync(messages, session, "what should I remember?", memoryInjected, CancellationToken.None);
        await assembler.TryInjectProfileRecallAsync(messages, session, CancellationToken.None);

        var userTexts = messages
            .Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text ?? "")
            .ToList();

        Assert.StartsWith("[Relevant memory]", userTexts[0], StringComparison.Ordinal);
        Assert.StartsWith("[User profile recall]", userTexts[1], StringComparison.Ordinal);
        Assert.Contains("<fractal_memory_context>", userTexts[2], StringComparison.Ordinal);
        Assert.Equal("what should I remember?", userTexts[3]);
    }

    [Fact]
    public async Task PromptContextAssembler_ProfileRecall_PropagatesCancellation()
    {
        var profileStore = Substitute.For<IUserProfileStore>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        profileStore.GetProfileAsync("websocket:user", cts.Token)
            .Returns<ValueTask<UserProfile?>>(_ => throw new OperationCanceledException(cts.Token));
        var assembler = new AgentPromptContextAssembler(
            Substitute.For<IMemoryStore>(),
            requireToolApproval: false,
            recall: null,
            profileStore,
            profilesConfig: new ProfilesConfig { Enabled = true, InjectRecall = true },
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await assembler.TryInjectProfileRecallAsync(
                [new ChatMessage(ChatRole.System, "system")],
                new Session { Id = "sess-cancel", ChannelId = "websocket", SenderId = "user" },
                cts.Token));
    }

    [Fact]
    public async Task CheckpointManager_PersistToolBatch_RetriesTransientSaveFailure()
    {
        var memory = new RetryCheckpointMemoryStore();
        var manager = new AgentCheckpointManager(memory, logger: null);
        var session = new Session { Id = "sess-checkpoint", ChannelId = "websocket", SenderId = "user" };
        session.History.Add(new ChatTurn { Role = "user", Content = "run tool" });
        session.History.Add(new ChatTurn { Role = "assistant", Content = "[tool_use]" });
        var invocation = new ToolInvocation
        {
            CallId = "call_1",
            ToolName = "demo_tool",
            Arguments = """{"value":"one"}""",
            Result = "tool result",
            Duration = TimeSpan.FromMilliseconds(12)
        };

        await manager.PersistToolBatchCheckpointAsync(
            session,
            new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            iteration: 0,
            [invocation],
            CancellationToken.None);

        Assert.Equal(2, memory.SaveAttempts);
        Assert.NotNull(memory.SavedCheckpoint);
        Assert.NotNull(memory.SavedCheckpoint.PersistedAtUtc);
        Assert.Equal(SessionCheckpointStates.ReadyToResume, memory.SavedCheckpoint.State);
        Assert.Equal("call_1", Assert.Single(memory.SavedCheckpoint.ToolCalls).CallId);
    }

    [Fact]
    public async Task CheckpointManager_PersistToolBatch_ThrowsAfterExhaustedRetries()
    {
        var memory = new AlwaysFailCheckpointMemoryStore();
        var manager = new AgentCheckpointManager(memory, logger: null);
        var session = new Session { Id = "sess-checkpoint-fail", ChannelId = "websocket", SenderId = "user" };
        var invocation = new ToolInvocation
        {
            CallId = "call_1",
            ToolName = "demo_tool",
            Arguments = "{}",
            Result = "tool result"
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await manager.PersistToolBatchCheckpointAsync(
                session,
                new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
                iteration: 0,
                [invocation],
                CancellationToken.None));

        Assert.Equal(3, memory.SaveAttempts);
        Assert.NotNull(session.ExecutionCheckpoint);
        Assert.Null(session.ExecutionCheckpoint.PersistedAtUtc);
    }

    [Fact]
    public async Task ModelExecutor_EstimatedTokenAdmission_ThrowsForNonStreamingAndReturnsStreamingError()
    {
        var metrics = new RuntimeMetrics();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model",
            TimeoutSeconds = 0,
            RetryCount = 0
        };
        var accounting = CreateAccounting(
            config,
            metrics,
            sessionTokenBudget: 1,
            estimateTokenBudgetAdmission: true);
        var executor = new AgentModelExecutor(
            new NeverCalledChatClient(),
            config,
            new CircuitBreaker(failureThreshold: 5, cooldown: TimeSpan.FromSeconds(1)),
            llmExecutionService: null,
            accounting,
            logger: null);
        var session = new Session
        {
            Id = "sess-budget",
            ChannelId = "websocket",
            SenderId = "user",
            TotalInputTokens = 1
        };
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var turnCtx = new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId };

        var exception = await Assert.ThrowsAsync<EstimatedBudgetAdmissionException>(() =>
            executor.CallLlmWithResilienceAsync(
                session,
                messages,
                new ChatOptions { ModelId = "primary-model" },
                turnCtx,
                skillPromptLength: 0,
                CancellationToken.None));
        var streamResult = await executor.StreamLlmCollectAsync(
            session,
            messages,
            new ChatOptions { ModelId = "primary-model" },
            turnCtx,
            skillPromptLength: 0,
            CancellationToken.None);

        Assert.Contains("close to its token budget", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("close to its token budget", streamResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, metrics.EstimatedTokenAdmissionRejects);
        Assert.Equal(0, metrics.TotalLlmCalls);
    }

    [Fact]
    public async Task ModelExecutor_NonStreaming_RetriesTransportHttpRequestException()
    {
        var chatClient = new RetryThenSuccessChatClient();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model",
            TimeoutSeconds = 0,
            RetryCount = 1
        };
        var executor = new AgentModelExecutor(
            chatClient,
            config,
            new CircuitBreaker(failureThreshold: 5, cooldown: TimeSpan.FromSeconds(1)),
            llmExecutionService: null,
            CreateAccounting(config),
            logger: null);

        var result = await executor.CallLlmWithResilienceAsync(
            new Session { Id = "sess-retry", ChannelId = "websocket", SenderId = "user" },
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "primary-model" },
            new TurnContext { SessionId = "sess-retry", ChannelId = "websocket" },
            skillPromptLength: 0,
            CancellationToken.None);

        Assert.Equal("retry ok", result.Response.Text);
        Assert.Equal(2, chatClient.Calls);
    }

    [Fact]
    public async Task ModelExecutor_StreamingDirectFallback_ClearsFailedPartialAndUsesFallbackModel()
    {
        var chatClient = new FallbackStreamingChatClient();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model",
            FallbackModels = ["fallback-model"],
            TimeoutSeconds = 0,
            RetryCount = 0
        };
        var accounting = CreateAccounting(config);
        var executor = new AgentModelExecutor(
            chatClient,
            config,
            new CircuitBreaker(failureThreshold: 5, cooldown: TimeSpan.FromSeconds(1)),
            llmExecutionService: null,
            accounting,
            logger: null);

        var result = await executor.StreamLlmCollectAsync(
            new Session { Id = "sess-model", ChannelId = "websocket", SenderId = "user" },
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "primary-model" },
            new TurnContext { SessionId = "sess-model", ChannelId = "websocket" },
            skillPromptLength: 0,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("fallback ok", result.FullText);
        Assert.True(result.IsUsageEstimated);
        Assert.Equal(["primary-model", "fallback-model"], chatClient.StreamedModels);
    }

    [Fact]
    public async Task ModelExecutor_StreamingDirectFallback_ClearsFailedCacheCounters()
    {
        var chatClient = new FallbackStreamingUsageThenSuccessChatClient();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model",
            FallbackModels = ["fallback-model"],
            TimeoutSeconds = 0,
            RetryCount = 0
        };
        var executor = new AgentModelExecutor(
            chatClient,
            config,
            new CircuitBreaker(failureThreshold: 5, cooldown: TimeSpan.FromSeconds(1)),
            llmExecutionService: null,
            CreateAccounting(config),
            logger: null);

        var result = await executor.StreamLlmCollectAsync(
            new Session { Id = "sess-cache-reset", ChannelId = "websocket", SenderId = "user" },
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "primary-model" },
            new TurnContext { SessionId = "sess-cache-reset", ChannelId = "websocket" },
            skillPromptLength: 0,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("fallback ok", result.FullText);
        Assert.Equal(0, result.CacheReadTokens);
        Assert.Equal(0, result.CacheWriteTokens);
    }

    [Fact]
    public void TurnAccounting_RecordCompactionUsage_RecordsProviderAndCacheUsage()
    {
        var metrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model"
        };
        var accounting = CreateAccounting(config, metrics, providerUsage);
        var session = new Session { Id = "sess-compaction", ChannelId = "websocket", SenderId = "user" };
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "summary")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 12,
                OutputTokenCount = 4,
                CachedInputTokenCount = 3,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_creation_input_tokens"] = 2
                }
            }
        };

        accounting.RecordCompactionUsage(
            session,
            new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            TimeSpan.FromMilliseconds(5),
            [new ChatMessage(ChatRole.User, "summarize")],
            new LlmExecutionResult
            {
                ProviderId = "openai",
                ModelId = "primary-model",
                Response = response
            },
            inputTokens: 12,
            outputTokens: 4,
            skillPromptLength: 0);

        Assert.Equal(12, session.TotalInputTokens);
        Assert.Equal(4, session.TotalOutputTokens);
        Assert.Equal(3, session.TotalCacheReadTokens);
        Assert.Equal(2, session.TotalCacheWriteTokens);
        Assert.Equal(3, metrics.PromptCacheReads);
        Assert.Equal(2, metrics.PromptCacheWrites);
        var provider = Assert.Single(providerUsage.Snapshot());
        Assert.Equal(12, provider.InputTokens);
        Assert.Equal(4, provider.OutputTokens);
        Assert.Equal(3, provider.CacheReadTokens);
        Assert.Equal(2, provider.CacheWriteTokens);
        var turn = Assert.Single(providerUsage.RecentTurns(session.Id));
        Assert.Equal(3, turn.CacheReadTokens);
        Assert.Equal(2, turn.CacheWriteTokens);
    }

    [Fact]
    public void TurnAccounting_RecordStreamingTurnUsage_PropagatesEstimatedFlag()
    {
        var observer = new RecordingTurnTokenUsageObserver();
        var config = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test",
            Model = "primary-model"
        };
        var accounting = new AgentTurnAccounting(
            metrics: null,
            providerUsage: null,
            config,
            sessionTokenBudget: 0,
            estimateTokenBudgetAdmission: false,
            turnTokenUsageObserver: observer,
            circuitState: () => CircuitState.Closed,
            isContractTokenBudgetExceeded: null,
            isContractRuntimeBudgetExceeded: null,
            recordContractTurnUsage: null,
            appendContractSnapshot: null,
            logger: null);
        var session = new Session { Id = "sess-stream-estimated", ChannelId = "websocket", SenderId = "user" };

        accounting.RecordStreamingTurnUsage(
            session,
            new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            [new ChatMessage(ChatRole.User, "hello")],
            new AgentStreamCollectResult
            {
                ProviderId = "openai",
                ModelId = "primary-model",
                InputTokens = 12,
                OutputTokens = 4,
                IsUsageEstimated = true,
                Elapsed = TimeSpan.FromMilliseconds(5)
            },
            skillPromptLength: 0);

        var record = Assert.Single(observer.Records);
        Assert.True(record.IsEstimated);
        Assert.Equal(12, record.InputTokens);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task ToolCallLoop_ExecuteToolCalls_RespectsParallelSetting(bool parallelToolExecution, int expectedMaxConcurrent)
    {
        var tool = new TrackingDelayTool();
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: []);
        var loop = new AgentToolCallLoop(executor, parallelToolExecution);

        var batch = await loop.ExecuteToolCallsAsync(
            [
                new FunctionCallContent("call_delay_1", "delay_tool", new Dictionary<string, object?> { ["id"] = "1" }),
                new FunctionCallContent("call_delay_2", "delay_tool", new Dictionary<string, object?> { ["id"] = "2" })
            ],
            new Session { Id = "sess-tools", ChannelId = "websocket", SenderId = "user" },
            new TurnContext { SessionId = "sess-tools", ChannelId = "websocket" },
            isStreaming: false,
            approvalCallback: null,
            CancellationToken.None);

        Assert.Equal(2, batch.Invocations.Count);
        Assert.Equal(expectedMaxConcurrent, tool.MaxConcurrent);
    }

    [Fact]
    public async Task ToolCallLoop_ToolStartFallsBackToEmptyJsonForUnserializableArguments()
    {
        var executor = new OpenClawToolExecutor(
            [],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: []);
        var loop = new AgentToolCallLoop(executor, parallelToolExecution: false);
        var cyclicArguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        cyclicArguments["self"] = cyclicArguments;

        await using var enumerator = loop.ExecuteStreamingToolCallsAsync(
            [new FunctionCallContent("call_cyclic", "cyclic_tool", cyclicArguments)],
            new Session { Id = "sess-tools", ChannelId = "websocket", SenderId = "user" },
            new TurnContext { SessionId = "sess-tools", ChannelId = "websocket" },
            approvalCallback: null,
            CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var evt = enumerator.Current.StreamEvent!.Value;
        Assert.Equal(AgentStreamEventType.ToolStart, evt.Type);
        Assert.Equal("{}", evt.ToolArguments);
    }

    [Fact]
    public async Task ToolCallLoop_StreamingTool_EmitsDeltasBeforeCompletedBatch()
    {
        var executor = new OpenClawToolExecutor(
            [new StreamingSmokeEchoTool()],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: []);
        var loop = new AgentToolCallLoop(executor, parallelToolExecution: true);
        var updates = new List<AgentToolLoopUpdate>();

        await foreach (var update in loop.ExecuteStreamingToolCallsAsync(
            [
                new FunctionCallContent(
                    "call_stream_1",
                    "stream_echo",
                    new Dictionary<string, object?>
                    {
                        ["chunks"] = new[] { "alpha", "beta" }
                    })
            ],
            new Session { Id = "sess-tools", ChannelId = "websocket", SenderId = "user" },
            new TurnContext { SessionId = "sess-tools", ChannelId = "websocket" },
            approvalCallback: null,
            CancellationToken.None))
        {
            updates.Add(update);
        }

        var events = updates.Where(update => update.StreamEvent.HasValue).Select(update => update.StreamEvent!.Value).ToList();
        Assert.Collection(
            events,
            evt => Assert.Equal(AgentStreamEventType.ToolStart, evt.Type),
            evt =>
            {
                Assert.Equal(AgentStreamEventType.ToolDelta, evt.Type);
                Assert.Equal("alpha", evt.Content);
            },
            evt =>
            {
                Assert.Equal(AgentStreamEventType.ToolDelta, evt.Type);
                Assert.Equal("beta", evt.Content);
            },
            evt =>
            {
                Assert.Equal(AgentStreamEventType.ToolResult, evt.Type);
                Assert.Equal("alphabeta", evt.Content);
            });
        var batch = Assert.Single(updates, update => update.Batch is not null).Batch!;
        Assert.Equal("alphabeta", Assert.Single(batch.Invocations).Result);
        Assert.Equal("alphabeta", Assert.Single(batch.Results).Result?.ToString());
    }

    private static AgentTurnAccounting CreateAccounting(
        LlmProviderConfig config,
        RuntimeMetrics? metrics = null,
        ProviderUsageTracker? providerUsage = null,
        long sessionTokenBudget = 0,
        bool estimateTokenBudgetAdmission = false)
        => new(
            metrics,
            providerUsage,
            config,
            sessionTokenBudget,
            estimateTokenBudgetAdmission,
            turnTokenUsageObserver: null,
            circuitState: () => CircuitState.Closed,
            isContractTokenBudgetExceeded: null,
            isContractRuntimeBudgetExceeded: null,
            recordContractTurnUsage: null,
            appendContractSnapshot: null,
            logger: null);

    private sealed class RecordingTurnTokenUsageObserver : ITurnTokenUsageObserver
    {
        public List<TurnTokenUsageRecord> Records { get; } = [];

        public void RecordTurn(TurnTokenUsageRecord record)
            => Records.Add(record);
    }

    private sealed class NeverCalledChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("never-called-test");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The chat client should not be called.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The streaming chat client should not be called.");

        public void Dispose()
        {
        }
    }

    private sealed class RetryThenSuccessChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata => new("retry-then-success-test");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                throw new HttpRequestException("transport failure");

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "retry ok")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class TrackingDelayTool : ITool
    {
        private int _currentConcurrent;
        private int _maxConcurrent;

        public string Name => "delay_tool";
        public string Description => "Delays long enough to observe concurrency.";
        public string ParameterSchema => """{"type":"object","properties":{"id":{"type":"string"}}}""";
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
            => new(ExecuteCoreAsync(ct));

        private async Task<string> ExecuteCoreAsync(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _currentConcurrent);
            UpdateMaxConcurrent(current);
            try
            {
                await Task.Delay(50, ct);
                return "done";
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }

        private void UpdateMaxConcurrent(int value)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrent);
                if (value <= observed)
                    return;

                if (Interlocked.CompareExchange(ref _maxConcurrent, value, observed) == observed)
                    return;
            }
        }
    }

    private sealed class RetryCheckpointMemoryStore : IMemoryStore
    {
        public int SaveAttempts { get; private set; }
        public SessionExecutionCheckpoint? SavedCheckpoint { get; private set; }

        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<Session?>(null);

        public ValueTask SaveSessionAsync(Session session, CancellationToken ct)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
                throw new IOException("temporary checkpoint store failure");

            SavedCheckpoint = session.ExecutionCheckpoint;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteNoteAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
            => ValueTask.FromResult<SessionBranch?>(null);

        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<SessionBranch>>([]);

        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class AlwaysFailCheckpointMemoryStore : IMemoryStore
    {
        public int SaveAttempts { get; private set; }

        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<Session?>(null);

        public ValueTask SaveSessionAsync(Session session, CancellationToken ct)
        {
            SaveAttempts++;
            throw new IOException("persistent checkpoint store failure");
        }

        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);

        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteNoteAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
            => ValueTask.FromResult<SessionBranch?>(null);

        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<SessionBranch>>([]);

        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class FallbackStreamingChatClient : IChatClient
    {
        public List<string?> StreamedModels { get; } = [];
        public ChatClientMetadata Metadata => new("fallback-streaming-test");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamedModels.Add(options?.ModelId);
            await Task.Yield();
            if (StreamedModels.Count == 1)
                throw new IOException("primary stream failed");

            yield return new ChatResponseUpdate(ChatRole.Assistant, "fallback ok");
        }

        public void Dispose()
        {
        }
    }

    private sealed class FallbackStreamingUsageThenSuccessChatClient : IChatClient
    {
        private int _streamCount;
        public ChatClientMetadata Metadata => new("fallback-streaming-cache-reset-test");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _streamCount++;
            await Task.Yield();
            if (_streamCount == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 5,
                    CachedInputTokenCount = 4,
                    AdditionalCounts = new AdditionalPropertiesDictionary<long>
                    {
                        ["cache_creation_input_tokens"] = 2
                    }
                })]);
                throw new IOException("primary stream failed after usage");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "fallback ok");
        }

        public void Dispose()
        {
        }
    }
}
