using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for Phase 6 — Beyond Base OpenClaw features:
/// conversation branching, multi-agent delegation, middleware pipeline,
/// token budget, structured output, config validation.
/// </summary>
public sealed class BeyondBaseTests
{
    private static readonly LlmProviderConfig DefaultConfig = new()
    {
        Provider = "openai",
        Model = "gpt-4o",
        MaxTokens = 1024,
        Temperature = 0.7f,
        TimeoutSeconds = 30,
        RetryCount = 0,
        CircuitBreakerThreshold = 5,
        CircuitBreakerCooldownSeconds = 30
    };

    private static Session CreateSession(string id = "test:user1") => new()
    {
        Id = id,
        ChannelId = "test",
        SenderId = "user1"
    };

    // ═══════════════════════════════════════════════════════════════════
    // Conversation Branching
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SessionManager_BranchAsync_CreatesSnapshotOfHistory()
    {
        var store = new TestMemoryStore();
        var config = new GatewayConfig();
        var manager = new SessionManager(store, config);

        var session = await manager.GetOrCreateAsync("ws", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "Hello" });
        session.History.Add(new ChatTurn { Role = "assistant", Content = "Hi there!" });

        var branchId = await manager.BranchAsync(session, "before-change", CancellationToken.None);

        Assert.NotNull(branchId);
        Assert.Contains("before-change", branchId);

        // Verify the branch was saved
        var savedBranch = store.SavedBranches.Values.FirstOrDefault();
        Assert.NotNull(savedBranch);
        Assert.Equal(2, savedBranch.History.Count);
        Assert.Equal("Hello", savedBranch.History[0].Content);
    }

    [Fact]
    public async Task SessionManager_RestoreBranchAsync_RestoresHistory()
    {
        var store = new TestMemoryStore();
        var config = new GatewayConfig();
        var manager = new SessionManager(store, config);

        var session = await manager.GetOrCreateAsync("ws", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "Original" });

        var branchId = await manager.BranchAsync(session, "snapshot", CancellationToken.None);

        // Modify history after branching
        session.History.Clear();
        session.History.Add(new ChatTurn { Role = "user", Content = "Changed" });
        Assert.Equal("Changed", session.History[0].Content);

        // Restore from branch
        var restored = await manager.RestoreBranchAsync(session, branchId, CancellationToken.None);
        Assert.True(restored);
        Assert.Single(session.History);
        Assert.Equal("Original", session.History[0].Content);
    }

    [Fact]
    public async Task SessionManager_RestoreBranchAsync_ReturnsFalseForUnknownBranch()
    {
        var store = new TestMemoryStore();
        var config = new GatewayConfig();
        var manager = new SessionManager(store, config);

        var session = await manager.GetOrCreateAsync("ws", "user1", CancellationToken.None);
        var restored = await manager.RestoreBranchAsync(session, "nonexistent", CancellationToken.None);
        Assert.False(restored);
    }

    [Fact]
    public async Task SessionManager_ListBranchesAsync_ReturnsAllBranchesForSession()
    {
        var store = new TestMemoryStore();
        var config = new GatewayConfig();
        var manager = new SessionManager(store, config);

        var session = await manager.GetOrCreateAsync("ws", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "Test" });

        await manager.BranchAsync(session, "branch-1", CancellationToken.None);
        await manager.BranchAsync(session, "branch-2", CancellationToken.None);

        var branches = await manager.ListBranchesAsync(session.Id, CancellationToken.None);
        Assert.Equal(2, branches.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Multi-Agent Delegation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DelegateTool_DelegatesToSubAgent()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        // Sub-agent responds with a summary
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "Research complete: quantum computing uses qubits.")])));

        var mockTool = Substitute.For<ITool>();
        mockTool.Name.Returns("web_search");
        mockTool.Description.Returns("Search");
        mockTool.ParameterSchema.Returns("{}");

        var delegationConfig = new DelegationConfig
        {
            Enabled = true,
            MaxDepth = 3,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["researcher"] = new AgentProfile
                {
                    Name = "researcher",
                    SystemPrompt = "You are a research assistant.",
                    AllowedTools = ["web_search"],
                    MaxIterations = 3
                }
            }
        };

        var tool = new DelegateTool(chatClient, [mockTool], memory, DefaultConfig, delegationConfig);

        var result = await tool.ExecuteAsync(
            """{"profile":"researcher","task":"Explain quantum computing"}""",
            CancellationToken.None);

        Assert.Contains("quantum computing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DelegateTool_RejectsUnknownProfile()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();
        var delegationConfig = new DelegationConfig
        {
            Enabled = true,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["coder"] = new AgentProfile { Name = "coder" }
            }
        };

        var tool = new DelegateTool(chatClient, [], memory, DefaultConfig, delegationConfig);
        var result = await tool.ExecuteAsync(
            """{"profile":"unknown","task":"test"}""", CancellationToken.None);

        Assert.Contains("Unknown agent profile", result);
    }

    [Fact]
    public async Task DelegateTool_RejectsExceededMaxDepth()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();
        var delegationConfig = new DelegationConfig
        {
            Enabled = true,
            MaxDepth = 2,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["coder"] = new AgentProfile { Name = "coder" }
            }
        };

        // Set depth to MaxDepth (should be rejected)
        var tool = new DelegateTool(chatClient, [], memory, DefaultConfig, delegationConfig, currentDepth: 2);
        var result = await tool.ExecuteAsync(
            """{"profile":"coder","task":"test"}""", CancellationToken.None);

        Assert.Contains("Maximum delegation depth", result);
    }

    [Fact]
    public async Task DelegateTool_RespectsProfileMaxIterations()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "web_search", new Dictionary<string, object?> { ["query"] = "test" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "This would be returned only if more iterations were allowed.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(callCount == 1 ? toolCallResponse : finalResponse);
            });

        var mockTool = Substitute.For<ITool>();
        mockTool.Name.Returns("web_search");
        mockTool.Description.Returns("Search");
        mockTool.ParameterSchema.Returns("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        mockTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult("search results"));

        var delegationConfig = new DelegationConfig
        {
            Enabled = true,
            MaxDepth = 3,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["researcher"] = new AgentProfile
                {
                    Name = "researcher",
                    AllowedTools = ["web_search"],
                    MaxIterations = 1
                }
            }
        };

        var tool = new DelegateTool(chatClient, [mockTool], memory, DefaultConfig, delegationConfig);
        var result = await tool.ExecuteAsync(
            """{"profile":"researcher","task":"Research this"}""",
            CancellationToken.None);

        Assert.Contains("maximum number of tool iterations", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DelegateTool_DescriptionIncludesProfileNames()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();
        var delegationConfig = new DelegationConfig
        {
            Enabled = true,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["researcher"] = new AgentProfile { Name = "researcher" },
                ["coder"] = new AgentProfile { Name = "coder" }
            }
        };

        var tool = new DelegateTool(chatClient, [], memory, DefaultConfig, delegationConfig);
        Assert.Contains("researcher", tool.Description);
        Assert.Contains("coder", tool.Description);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Middleware Pipeline
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MiddlewarePipeline_EmptyPipeline_ReturnsTrue()
    {
        var pipeline = new MiddlewarePipeline([]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };
        var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task MiddlewarePipeline_MiddlewareCanShortCircuit()
    {
        var blockingMw = new TestBlockingMiddleware("Blocked!");
        var pipeline = new MiddlewarePipeline([blockingMw]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };

        var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.False(result);
        Assert.Equal("Blocked!", context.ShortCircuitResponse);
    }

    [Fact]
    public async Task MiddlewarePipeline_MiddlewareChainExecutesInOrder()
    {
        var order = new List<string>();
        var mw1 = new TestOrderMiddleware("first", order);
        var mw2 = new TestOrderMiddleware("second", order);
        var pipeline = new MiddlewarePipeline([mw1, mw2]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };

        await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task MiddlewarePipeline_ShortCircuitStopsChain()
    {
        var order = new List<string>();
        var mw1 = new TestBlockingMiddleware("Blocked!");
        var mw2 = new TestOrderMiddleware("should-not-run", order);
        var pipeline = new MiddlewarePipeline([mw1, mw2]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };

        await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.Empty(order); // Second middleware should not have executed
    }

    [Fact]
    public async Task MiddlewarePipeline_MiddlewareCanTransformText()
    {
        var transformMw = new TestTransformMiddleware();
        var pipeline = new MiddlewarePipeline([transformMw]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hello" };

        await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal("HELLO", context.Text);
    }

    [Fact]
    public async Task MiddlewarePipeline_Execution_IsAllocationLight()
    {
        var pipeline = new MiddlewarePipeline([new TestPassThroughMiddleware()]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };

        // Warm up JIT
        await pipeline.ExecuteAsync(context, CancellationToken.None);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            context.Text = "hi";
            await pipeline.ExecuteAsync(context, CancellationToken.None);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Smoke test: catches accidental high-per-message allocations/regressions.
        Assert.True(allocated < 5_000_000, $"Allocated too much: {allocated:n0} bytes");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Rate Limit Middleware
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RateLimitMiddleware_AllowsWithinLimit()
    {
        var mw = new RateLimitMiddleware(5);
        var pipeline = new MiddlewarePipeline([mw]);
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };

        for (var i = 0; i < 5; i++)
        {
            var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
            Assert.True(result);
        }
    }

    [Fact]
    public async Task RateLimitMiddleware_BlocksOverLimit()
    {
        var mw = new RateLimitMiddleware(3);

        // Send 3 — should all succeed
        for (var i = 0; i < 3; i++)
        {
            var pipeline = new MiddlewarePipeline([mw]);
            var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };
            var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
            Assert.True(result);
        }

        // 4th should be blocked
        var blocked = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };
        var blockedPipeline = new MiddlewarePipeline([mw]);
        var blockedResult = await blockedPipeline.ExecuteAsync(blocked, CancellationToken.None);
        Assert.False(blockedResult);
        Assert.Contains("too quickly", blocked.ShortCircuitResponse);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Token Budget Middleware
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TokenBudgetMiddleware_AllowsUnderBudget()
    {
        var mw = new TokenBudgetMiddleware(10000);
        var pipeline = new MiddlewarePipeline([mw]);
        var context = new MessageContext
        {
            ChannelId = "ws", SenderId = "u1", Text = "hi",
            SessionInputTokens = 500,
            SessionOutputTokens = 300
        };

        var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task TokenBudgetMiddleware_BlocksOverBudget()
    {
        var mw = new TokenBudgetMiddleware(1000);
        var pipeline = new MiddlewarePipeline([mw]);
        var context = new MessageContext
        {
            ChannelId = "ws", SenderId = "u1", Text = "hi",
            SessionInputTokens = 600,
            SessionOutputTokens = 500
        };

        var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.False(result);
        Assert.Contains("token budget", context.ShortCircuitResponse);
    }

    [Fact]
    public async Task TokenBudgetMiddleware_UnlimitedBudgetAllowsAll()
    {
        var mw = new TokenBudgetMiddleware(0); // 0 = unlimited
        var pipeline = new MiddlewarePipeline([mw]);
        var context = new MessageContext
        {
            ChannelId = "ws", SenderId = "u1", Text = "hi",
            SessionInputTokens = 1_000_000,
            SessionOutputTokens = 1_000_000
        };

        var result = await pipeline.ExecuteAsync(context, CancellationToken.None);
        Assert.True(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Session Token Tracking
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentRuntime_TracksTokenUsageOnSession()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var response = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, "Reply")])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 }
        };

        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();

        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);

        await agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.Equal(100, session.TotalInputTokens);
        Assert.Equal(50, session.TotalOutputTokens);
    }

    [Fact]
    public async Task AgentRuntime_AccumulatesTokensAcrossTurns()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var response = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, "Reply")])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 }
        };

        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();

        await agent.RunAsync(session, "Hello", CancellationToken.None);
        await agent.RunAsync(session, "Again", CancellationToken.None);

        Assert.Equal(200, session.TotalInputTokens);
        Assert.Equal(100, session.TotalOutputTokens);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Structured Output
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentRuntime_StructuredOutput_PassesResponseSchema()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, """{"name":"test"}""")]));
            });

        var schema = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""");
        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();

        await agent.RunAsync(session, "Give me structured output", CancellationToken.None,
            responseSchema: schema.RootElement);

        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions!.ResponseFormat);
    }

    [Fact]
    public async Task AgentRuntime_NoSchema_ResponseFormatIsNull()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, "Plain text")]));
            });

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();

        await agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions!.ResponseFormat);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Configuration Validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ConfigValidator_ValidConfig_NoErrors()
    {
        var config = new GatewayConfig();
        var errors = ConfigValidator.Validate(config);
        Assert.Empty(errors);
    }

    [Fact]
    public void ConfigValidator_InvalidPort_ReturnsError()
    {
        var config = new GatewayConfig { Port = -1 };
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Port"));
    }

    [Fact]
    public void ConfigValidator_InvalidTemperature_ReturnsError()
    {
        var config = new GatewayConfig();
        config.Llm.Temperature = 5.0f;
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Temperature"));
    }

    [Fact]
    public void ConfigValidator_CompactionKeepRecentExceedsThreshold_ReturnsError()
    {
        var config = new GatewayConfig();
        config.Memory.EnableCompaction = true;
        config.Memory.CompactionThreshold = 10;
        config.Memory.CompactionKeepRecent = 15;
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("CompactionKeepRecent"));
    }

    [Fact]
    public void ConfigValidator_DelegationEnabledWithNoProfiles_ReturnsError()
    {
        var config = new GatewayConfig();
        config.Delegation.Enabled = true;
        config.Delegation.Profiles.Clear();
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("no profiles"));
    }

    [Fact]
    public void ConfigValidator_NegativeTokenBudget_ReturnsError()
    {
        var config = new GatewayConfig { SessionTokenBudget = -1 };
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("SessionTokenBudget"));
    }

    [Fact]
    public void ConfigValidator_MultipleErrors_ReturnsAll()
    {
        var config = new GatewayConfig
        {
            Port = 0,
            SessionTimeoutMinutes = 0,
            MaxConcurrentSessions = 0
        };
        var errors = ConfigValidator.Validate(config);
        Assert.True(errors.Count >= 3);
    }

    // ═══════════════════════════════════════════════════════════════════
    // IMemoryStore Branch Methods (via interface)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TestMemoryStore_SaveAndLoadBranch()
    {
        var store = new TestMemoryStore();
        var branch = new SessionBranch
        {
            BranchId = "b1",
            SessionId = "s1",
            Name = "test-branch",
            History = [new ChatTurn { Role = "user", Content = "hi" }]
        };

        await store.SaveBranchAsync(branch, CancellationToken.None);
        var loaded = await store.LoadBranchAsync("b1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("test-branch", loaded!.Name);
        Assert.Single(loaded.History);
    }

    [Fact]
    public async Task TestMemoryStore_ListBranches_FiltersBySessionId()
    {
        var store = new TestMemoryStore();

        await store.SaveBranchAsync(new SessionBranch { BranchId = "b1", SessionId = "s1", Name = "a", History = [] }, CancellationToken.None);
        await store.SaveBranchAsync(new SessionBranch { BranchId = "b2", SessionId = "s1", Name = "b", History = [] }, CancellationToken.None);
        await store.SaveBranchAsync(new SessionBranch { BranchId = "b3", SessionId = "s2", Name = "c", History = [] }, CancellationToken.None);

        var s1Branches = await store.ListBranchesAsync("s1", CancellationToken.None);
        Assert.Equal(2, s1Branches.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Session Model
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Session_DefaultTokenCountsAreZero()
    {
        var session = CreateSession();
        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);
    }

    [Fact]
    public void SessionBranch_HistoryIsIndependentCopy()
    {
        var originalHistory = new List<ChatTurn>
        {
            new() { Role = "user", Content = "hello" }
        };

        var branch = new SessionBranch
        {
            BranchId = "b1",
            SessionId = "s1",
            Name = "test",
            History = originalHistory.ToList()
        };

        // Modifying original should not affect branch
        originalHistory.Add(new ChatTurn { Role = "assistant", Content = "world" });
        Assert.Single(branch.History);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GatewayConfig Defaults
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GatewayConfig_NewPhase6Defaults()
    {
        var config = new GatewayConfig();
        Assert.Equal(0, config.SessionTokenBudget);
        Assert.Equal(0, config.SessionRateLimitPerMinute);
        Assert.Equal(15, config.GracefulShutdownSeconds);
        Assert.False(config.Delegation.Enabled);
        Assert.Equal(3, config.Delegation.MaxDepth);
        Assert.Empty(config.Delegation.Profiles);
    }

    [Fact]
    public void AgentProfile_Defaults()
    {
        var profile = new AgentProfile { Name = "test" };
        Assert.Equal(5, profile.MaxIterations);
        Assert.Equal(20, profile.MaxHistoryTurns);
        Assert.Empty(profile.AllowedTools);
        Assert.Empty(profile.SystemPrompt);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MessageContext
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void MessageContext_ShortCircuit_SetsFlags()
    {
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };
        Assert.False(context.IsShortCircuited);
        Assert.Null(context.ShortCircuitResponse);

        context.ShortCircuit("Blocked");
        Assert.True(context.IsShortCircuited);
        Assert.Equal("Blocked", context.ShortCircuitResponse);
    }

    [Fact]
    public void MessageContext_Properties_Accessible()
    {
        var context = new MessageContext { ChannelId = "ws", SenderId = "u1", Text = "hi" };
        context.Properties["key"] = "value";
        Assert.Equal("value", context.Properties["key"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DelegateTool Parameter Validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DelegateTool_MissingProfile_ReturnsError()
    {
        var tool = CreateDelegateTool();
        var result = await tool.ExecuteAsync("""{"task":"test"}""", CancellationToken.None);
        Assert.Contains("'profile' parameter is required", result);
    }

    [Fact]
    public async Task DelegateTool_MissingTask_ReturnsError()
    {
        var tool = CreateDelegateTool();
        var result = await tool.ExecuteAsync("""{"profile":"coder"}""", CancellationToken.None);
        Assert.Contains("'task' parameter is required", result);
    }

    [Fact]
    public void DelegateTool_SchemaIsValidJson()
    {
        var tool = CreateDelegateTool();
        var doc = JsonDocument.Parse(tool.ParameterSchema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("profile", out _));
        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("task", out _));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static DelegateTool CreateDelegateTool()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();
        var config = new DelegationConfig
        {
            Enabled = true,
            Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
            {
                ["coder"] = new AgentProfile { Name = "coder" }
            }
        };
        return new DelegateTool(chatClient, [], memory, DefaultConfig, config);
    }

    /// <summary>In-memory IMemoryStore for branch testing.</summary>
    private sealed class TestMemoryStore : IMemoryStore
    {
        public Dictionary<string, SessionBranch> SavedBranches { get; } = new(StringComparer.Ordinal);

        public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
            => ValueTask.FromResult<Session?>(null);
        public ValueTask SaveSessionAsync(Session session, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
        public ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask DeleteNoteAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
        {
            SavedBranches[branch.BranchId] = branch;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
        {
            SavedBranches.TryGetValue(branchId, out var branch);
            return ValueTask.FromResult(branch);
        }

        public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
        {
            var matches = SavedBranches.Values
                .Where(b => b.SessionId == sessionId)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<SessionBranch>>(matches);
        }

        public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
        {
            SavedBranches.Remove(branchId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestBlockingMiddleware : IMessageMiddleware
    {
        private readonly string _response;
        public string Name => "TestBlocking";

        public TestBlockingMiddleware(string response) => _response = response;

        public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
        {
            context.ShortCircuit(_response);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestOrderMiddleware : IMessageMiddleware
    {
        private readonly string _name;
        private readonly List<string> _order;
        public string Name => _name;

        public TestOrderMiddleware(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public async ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
        {
            _order.Add(_name);
            await next();
        }
    }

    private sealed class TestTransformMiddleware : IMessageMiddleware
    {
        public string Name => "TestTransform";

        public async ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
        {
            context.Text = context.Text.ToUpperInvariant();
            await next();
        }
    }

    private sealed class TestPassThroughMiddleware : IMessageMiddleware
    {
        public string Name => "TestPassThrough";

        public ValueTask InvokeAsync(MessageContext context, Func<ValueTask> next, CancellationToken ct)
            => next();
    }
}
