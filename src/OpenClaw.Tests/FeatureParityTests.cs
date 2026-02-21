using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for Phase 5 features: streaming, multi-provider, compaction,
/// tool approval, parallel tools, project memory, hooks.
/// </summary>
public class FeatureParityTests
{
    private static LlmProviderConfig DefaultConfig => new()
    {
        Provider = "openai",
        ApiKey = "test-key",
        Model = "gpt-4",
        MaxTokens = 100,
        Temperature = 0.7f,
        TimeoutSeconds = 0, // No timeout in tests
        RetryCount = 0      // No retries in tests
    };

    private static Session CreateSession(string id = "sess1") => new()
    {
        Id = id,
        SenderId = "user1",
        ChannelId = "websocket"
    };

    // ── Streaming Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task RunStreamingAsync_YieldsTextDeltasAndDone()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        // Configure streaming to return chunks
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, "Hello "),
            new ChatResponseUpdate(ChatRole.Assistant, "world!")
        };
        chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();
        var events = new List<AgentStreamEvent>();

        await foreach (var evt in agent.RunStreamingAsync(session, "Hi", CancellationToken.None))
            events.Add(evt);

        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "Hello ");
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "world!");
        Assert.Contains(events, e => e.Type == AgentStreamEventType.Done);

        // History should have user and assistant turns
        Assert.Contains(session.History, t => t.Role == "user" && t.Content == "Hi");
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello world!");
    }

    [Fact]
    public async Task RunStreamingAsync_ErrorDuringStream_YieldsErrorEvent()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ThrowingAsyncEnumerable());

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();
        var events = new List<AgentStreamEvent>();

        await foreach (var evt in agent.RunStreamingAsync(session, "Hi", CancellationToken.None))
            events.Add(evt);

        Assert.Contains(events, e => e.Type == AgentStreamEventType.Error);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.Done);
    }

    [Fact]
    public async Task RunStreamingAsync_TracksEstimatedSessionTokens()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, "Hello "),
            new ChatResponseUpdate(ChatRole.Assistant, "world!")
        };
        chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig, maxHistoryTurns: 10);
        var session = CreateSession();

        await foreach (var _ in agent.RunStreamingAsync(session, "Hi", CancellationToken.None))
        {
            // drain
        }

        Assert.True(session.TotalInputTokens > 0);
        Assert.True(session.TotalOutputTokens > 0);
    }

    [Fact]
    public void AgentStreamEvent_EnvelopeType_Maps_Correctly()
    {
        Assert.Equal("assistant_chunk", AgentStreamEvent.TextDelta("x").EnvelopeType);
        Assert.Equal("tool_start", AgentStreamEvent.ToolStarted("shell").EnvelopeType);
        Assert.Equal("tool_result", AgentStreamEvent.ToolCompleted("shell", "ok").EnvelopeType);
        Assert.Equal("error", AgentStreamEvent.ErrorOccurred("fail").EnvelopeType);
        Assert.Equal("assistant_done", AgentStreamEvent.Complete().EnvelopeType);
    }

    // ── Parallel Tool Execution Tests ────────────────────────────────────

    [Fact]
    public async Task RunAsync_ParallelTools_ExecutesConcurrently()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        // First call: returns two tool calls
        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "slow_tool", new Dictionary<string, object?> { ["id"] = "1" }),
                new FunctionCallContent("call2", "slow_tool", new Dictionary<string, object?> { ["id"] = "2" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Both tools done!")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        // Track concurrent executions
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var slowTool = Substitute.For<ITool>();
        slowTool.Name.Returns("slow_tool");
        slowTool.Description.Returns("A slow tool");
        slowTool.ParameterSchema.Returns("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        slowTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                Interlocked.Exchange(ref maxConcurrent, Math.Max(maxConcurrent, current));
                return new ValueTask<string>(Task.Run(async () =>
                {
                    await Task.Delay(50, ci.Arg<CancellationToken>());
                    Interlocked.Decrement(ref concurrentCount);
                    return "done";
                }));
            });

        var agent = new AgentRuntime(chatClient, [slowTool], memory, DefaultConfig,
            maxHistoryTurns: 10, parallelToolExecution: true);
        var session = CreateSession();

        var result = await agent.RunAsync(session, "Run tools", CancellationToken.None);

        Assert.Equal("Both tools done!", result);
        // With parallel execution, both tools should have been running concurrently
        Assert.Equal(2, maxConcurrent);
    }

    [Fact]
    public async Task RunAsync_SequentialTools_ExecutesOneAtATime()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "slow_tool", new Dictionary<string, object?> { ["id"] = "1" }),
                new FunctionCallContent("call2", "slow_tool", new Dictionary<string, object?> { ["id"] = "2" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Done")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var concurrentCount = 0;
        var maxConcurrent = 0;
        var slowTool = Substitute.For<ITool>();
        slowTool.Name.Returns("slow_tool");
        slowTool.Description.Returns("A slow tool");
        slowTool.ParameterSchema.Returns("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        slowTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                Interlocked.Exchange(ref maxConcurrent, Math.Max(maxConcurrent, current));
                return new ValueTask<string>(Task.Run(async () =>
                {
                    await Task.Delay(50, ci.Arg<CancellationToken>());
                    Interlocked.Decrement(ref concurrentCount);
                    return "done";
                }));
            });

        // parallelToolExecution: false
        var agent = new AgentRuntime(chatClient, [slowTool], memory, DefaultConfig,
            maxHistoryTurns: 10, parallelToolExecution: false);
        var session = CreateSession();

        var result = await agent.RunAsync(session, "Run tools", CancellationToken.None);

        Assert.Equal("Done", result);
        // With sequential execution, max concurrent should be 1
        Assert.Equal(1, maxConcurrent);
    }

    // ── Tool Approval Tests ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolApproval_DeniedByCallback()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "shell", new Dictionary<string, object?> { ["command"] = "rm -rf /" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "OK, I won't do that.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var shellTool = Substitute.For<ITool>();
        shellTool.Name.Returns("shell");
        shellTool.Description.Returns("Execute shell commands");
        shellTool.ParameterSchema.Returns("""{"type":"object","properties":{"command":{"type":"string"}}}""");

        var agent = new AgentRuntime(chatClient, [shellTool], memory, DefaultConfig,
            maxHistoryTurns: 10, requireToolApproval: true,
            approvalRequiredTools: ["shell"]);
        var session = CreateSession();

        // Approval callback denies
        ToolApprovalCallback denyAll = (_, _, _) => ValueTask.FromResult(false);

        var result = await agent.RunAsync(session, "Run dangerous command", CancellationToken.None, denyAll);

        Assert.Equal("OK, I won't do that.", result);
        // Tool should NOT have been executed
        await shellTool.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ToolApproval_ApprovedByCallback()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "shell", new Dictionary<string, object?> { ["command"] = "ls" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Here are the files.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var shellTool = Substitute.For<ITool>();
        shellTool.Name.Returns("shell");
        shellTool.Description.Returns("Execute shell commands");
        shellTool.ParameterSchema.Returns("""{"type":"object","properties":{"command":{"type":"string"}}}""");
        shellTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult("file1.txt\nfile2.txt"));

        var agent = new AgentRuntime(chatClient, [shellTool], memory, DefaultConfig,
            maxHistoryTurns: 10, requireToolApproval: true,
            approvalRequiredTools: ["shell"]);
        var session = CreateSession();

        // Approval callback approves
        ToolApprovalCallback allowAll = (_, _, _) => ValueTask.FromResult(true);

        var result = await agent.RunAsync(session, "List files", CancellationToken.None, allowAll);

        Assert.Equal("Here are the files.", result);
        // Tool should have been executed
        await shellTool.Received(1).ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ToolApproval_NoCallback_DeniedByDefault()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "shell", new Dictionary<string, object?> { ["command"] = "ls" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Cannot execute without approval.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var shellTool = Substitute.For<ITool>();
        shellTool.Name.Returns("shell");
        shellTool.Description.Returns("Execute shell commands");
        shellTool.ParameterSchema.Returns("""{"type":"object","properties":{"command":{"type":"string"}}}""");

        var agent = new AgentRuntime(chatClient, [shellTool], memory, DefaultConfig,
            maxHistoryTurns: 10, requireToolApproval: true,
            approvalRequiredTools: ["shell"]);
        var session = CreateSession();

        // No approval callback — should deny by default
        var result = await agent.RunAsync(session, "List files", CancellationToken.None);

        await shellTool.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ToolApproval_LegacyFileWriteAliasStillProtected()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "write_file", new Dictionary<string, object?> { ["path"] = "/tmp/x", ["content"] = "x" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Cannot execute without approval.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var writeTool = Substitute.For<ITool>();
        writeTool.Name.Returns("write_file");
        writeTool.Description.Returns("Write files");
        writeTool.ParameterSchema.Returns("""{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}}}""");

        var agent = new AgentRuntime(chatClient, [writeTool], memory, DefaultConfig,
            maxHistoryTurns: 10, requireToolApproval: true,
            approvalRequiredTools: ["file_write"]);
        var session = CreateSession();

        var result = await agent.RunAsync(session, "Write a file", CancellationToken.None);

        Assert.Equal("Cannot execute without approval.", result);
        await writeTool.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Hooks Tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Hooks_BeforeExecute_DeniesToolExecution()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "some_tool", new Dictionary<string, object?> { ["x"] = "1" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Hook denied the tool.")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("some_tool");
        tool.Description.Returns("A tool");
        tool.ParameterSchema.Returns("""{"type":"object","properties":{"x":{"type":"string"}}}""");

        var denyHook = Substitute.For<IToolHook>();
        denyHook.Name.Returns("DenyAll");
        denyHook.BeforeExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));

        var agent = new AgentRuntime(chatClient, [tool], memory, DefaultConfig,
            maxHistoryTurns: 10, hooks: [denyHook]);
        var session = CreateSession();

        var result = await agent.RunAsync(session, "Use tool", CancellationToken.None);

        await tool.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("Hook denied the tool.", result);
    }

    [Fact]
    public async Task Hooks_AfterExecute_CalledWithResult()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var toolCallResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call1", "some_tool", new Dictionary<string, object?> { ["x"] = "1" })
            })
        });

        var finalResponse = new ChatResponse(new[]
        {
            new ChatMessage(ChatRole.Assistant, "Done")
        });

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = Interlocked.Increment(ref callCount);
                return Task.FromResult(c == 1 ? toolCallResponse : finalResponse);
            });

        var tool = Substitute.For<ITool>();
        tool.Name.Returns("some_tool");
        tool.Description.Returns("A tool");
        tool.ParameterSchema.Returns("""{"type":"object","properties":{"x":{"type":"string"}}}""");
        tool.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult("tool result"));

        var auditHook = Substitute.For<IToolHook>();
        auditHook.Name.Returns("Audit");
        auditHook.BeforeExecuteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var agent = new AgentRuntime(chatClient, [tool], memory, DefaultConfig,
            maxHistoryTurns: 10, hooks: [auditHook]);
        var session = CreateSession();

        await agent.RunAsync(session, "Use tool", CancellationToken.None);

        await auditHook.Received(1).AfterExecuteAsync(
            "some_tool", Arg.Any<string>(), "tool result", Arg.Any<TimeSpan>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditLogHook_AllowsExecution()
    {
        var logger = Substitute.For<ILogger>();
        var hook = new AuditLogHook(logger);

        Assert.Equal("AuditLog", hook.Name);

        var result = await hook.BeforeExecuteAsync("shell", "{}", CancellationToken.None);
        Assert.True(result);
    }

    // ── Context Compaction Tests ─────────────────────────────────────────

    [Fact]
    public async Task CompactHistory_BelowThreshold_NoCompaction()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig,
            maxHistoryTurns: 50, enableCompaction: true, compactionThreshold: 40);

        var session = CreateSession();
        // Add 10 turns — below threshold of 40
        for (var i = 0; i < 10; i++)
            session.History.Add(new ChatTurn { Role = "user", Content = $"msg {i}" });

        await agent.CompactHistoryAsync(session, CancellationToken.None);

        // No compaction should have occurred — no LLM calls
        await chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompactHistory_AboveThreshold_SummarizesOldTurns()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        // Set up the summarization response
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[]
            {
                new ChatMessage(ChatRole.Assistant, "User discussed project architecture and setup steps.")
            })));

        var agent = new AgentRuntime(chatClient, [], memory, DefaultConfig,
            maxHistoryTurns: 50, enableCompaction: true, compactionThreshold: 10, compactionKeepRecent: 5);

        var session = CreateSession();
        // Add 15 turns — above threshold of 10
        for (var i = 0; i < 15; i++)
            session.History.Add(new ChatTurn { Role = i % 2 == 0 ? "user" : "assistant", Content = $"msg {i}" });

        await agent.CompactHistoryAsync(session, CancellationToken.None);

        // First turn should be the summary
        Assert.StartsWith("[Previous conversation summary:", session.History[0].Content);
        Assert.Equal("system", session.History[0].Role);

        // Should have keep_recent + 1 (summary) turns
        Assert.True(session.History.Count <= 6, $"Expected ≤6 turns after compaction, got {session.History.Count}");
    }

    [Fact]
    public async Task CompactHistory_UsesRetryResilienceForTransientFailure()
    {
        var chatClient = Substitute.For<IChatClient>();
        var memory = Substitute.For<IMemoryStore>();

        var retryConfig = new LlmProviderConfig
        {
            Provider = "openai",
            ApiKey = "test-key",
            Model = "gpt-4",
            MaxTokens = 100,
            Temperature = 0.7f,
            TimeoutSeconds = 0,
            RetryCount = 1
        };

        var callCount = 0;
        chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("transient", null, HttpStatusCode.TooManyRequests);

                return Task.FromResult(new ChatResponse(new[]
                {
                    new ChatMessage(ChatRole.Assistant, "Recovered summary.")
                }));
            });

        var agent = new AgentRuntime(chatClient, [], memory, retryConfig,
            maxHistoryTurns: 50, enableCompaction: true, compactionThreshold: 10, compactionKeepRecent: 5);

        var session = CreateSession();
        for (var i = 0; i < 15; i++)
            session.History.Add(new ChatTurn { Role = i % 2 == 0 ? "user" : "assistant", Content = $"msg {i}" });

        await agent.CompactHistoryAsync(session, CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.StartsWith("[Previous conversation summary:", session.History[0].Content);
    }

    // ── Project Memory Tests ─────────────────────────────────────────────

    [Fact]
    public async Task ProjectMemoryTool_SaveAndLoad()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-pm-{Guid.NewGuid():N}");
        try
        {
            var store = new OpenClaw.Core.Memory.FileMemoryStore(tempDir, 10);
            var tool = new ProjectMemoryTool(store, "test-project");

            // Save
            var saveResult = await tool.ExecuteAsync(
                """{"action":"save","key":"arch","content":"Microservices with event sourcing"}""",
                CancellationToken.None);
            Assert.Contains("Saved", saveResult);

            // Load
            var loadResult = await tool.ExecuteAsync(
                """{"action":"load","key":"arch"}""",
                CancellationToken.None);
            Assert.Equal("Microservices with event sourcing", loadResult);

            // List
            var listResult = await tool.ExecuteAsync(
                """{"action":"list"}""",
                CancellationToken.None);
            Assert.Contains("arch", listResult);

            // Delete
            var deleteResult = await tool.ExecuteAsync(
                """{"action":"delete","key":"arch"}""",
                CancellationToken.None);
            Assert.Contains("Deleted", deleteResult);

            // Load after delete
            var loadAfterDelete = await tool.ExecuteAsync(
                """{"action":"load","key":"arch"}""",
                CancellationToken.None);
            Assert.Contains("No project memory found", loadAfterDelete);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectMemoryTool_ProjectIsolation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-pm-{Guid.NewGuid():N}");
        try
        {
            var store = new OpenClaw.Core.Memory.FileMemoryStore(tempDir, 10);
            var toolA = new ProjectMemoryTool(store, "project-a");
            var toolB = new ProjectMemoryTool(store, "project-b");

            // Save to project A
            await toolA.ExecuteAsync(
                """{"action":"save","key":"config","content":"A's config"}""",
                CancellationToken.None);

            // Load from project B — should not find it
            var result = await toolB.ExecuteAsync(
                """{"action":"load","key":"config"}""",
                CancellationToken.None);
            Assert.Contains("No project memory found", result);

            // Load from project A — should find it
            result = await toolA.ExecuteAsync(
                """{"action":"load","key":"config"}""",
                CancellationToken.None);
            Assert.Equal("A's config", result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectMemoryTool_InvalidAction_ReturnsError()
    {
        var memory = Substitute.For<IMemoryStore>();
        var tool = new ProjectMemoryTool(memory, "test");

        var result = await tool.ExecuteAsync(
            """{"action":"purge"}""", CancellationToken.None);
        Assert.Contains("Unknown action", result);
    }

    [Fact]
    public async Task ProjectMemoryTool_MissingKey_ReturnsError()
    {
        var memory = Substitute.For<IMemoryStore>();
        var tool = new ProjectMemoryTool(memory, "test");

        var result = await tool.ExecuteAsync(
            """{"action":"save","content":"x"}""", CancellationToken.None);
        Assert.Contains("'key' is required", result);
    }

    // ── WebSocket Streaming Tests ────────────────────────────────────────

    [Fact]
    public void WebSocketChannel_IsClientUsingEnvelopes_FalseForUnknown()
    {
        var wsConfig = new WebSocketConfig();
        var channel = new WebSocketChannel(wsConfig);
        Assert.False(channel.IsClientUsingEnvelopes("nonexistent"));
    }

    [Fact]
    public async Task WebSocketChannel_SendStreamEvent_OnlyForEnvelopeClients()
    {
        var wsConfig = new WebSocketConfig();
        var channel = new WebSocketChannel(wsConfig);

        // Should not throw for non-existent client
        await channel.SendStreamEventAsync("no-client", "assistant_chunk", "hello", null, CancellationToken.None);
    }

    // ── Multi-Provider Config Tests ──────────────────────────────────────

    [Fact]
    public void GatewayConfig_ToolingConfig_HasParallelToolExecution()
    {
        var config = new ToolingConfig();
        Assert.True(config.ParallelToolExecution); // Default true
    }

    [Fact]
    public void GatewayConfig_ToolingConfig_HasToolApproval()
    {
        var config = new ToolingConfig();
        Assert.False(config.RequireToolApproval); // Default false
        Assert.Contains("shell", config.ApprovalRequiredTools);
        Assert.Contains("write_file", config.ApprovalRequiredTools);
        Assert.DoesNotContain("file_write", config.ApprovalRequiredTools);
    }

    [Fact]
    public void GatewayConfig_MemoryConfig_HasCompaction()
    {
        var config = new MemoryConfig();
        Assert.False(config.EnableCompaction); // Default false
        Assert.Equal(40, config.CompactionThreshold);
        Assert.Equal(10, config.CompactionKeepRecent);
    }

    [Fact]
    public void GatewayConfig_MemoryConfig_HasProjectId()
    {
        var config = new MemoryConfig();
        Assert.Null(config.ProjectId); // Default null
    }

    // ── FileMemoryStore Extension Tests ──────────────────────────────────

    [Fact]
    public async Task FileMemoryStore_DeleteNote()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-fms-{Guid.NewGuid():N}");
        try
        {
            var store = new OpenClaw.Core.Memory.FileMemoryStore(tempDir, 10);

            await store.SaveNoteAsync("test-key", "test-content", CancellationToken.None);
            var loaded = await store.LoadNoteAsync("test-key", CancellationToken.None);
            Assert.Equal("test-content", loaded);

            await store.DeleteNoteAsync("test-key", CancellationToken.None);
            loaded = await store.LoadNoteAsync("test-key", CancellationToken.None);
            Assert.Null(loaded);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileMemoryStore_ListNotesWithPrefix()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-fms-{Guid.NewGuid():N}");
        try
        {
            var store = new OpenClaw.Core.Memory.FileMemoryStore(tempDir, 10);

            await store.SaveNoteAsync("project:myapp:arch", "microservices", CancellationToken.None);
            await store.SaveNoteAsync("project:myapp:stack", "dotnet", CancellationToken.None);
            await store.SaveNoteAsync("project:other:config", "v2", CancellationToken.None);
            await store.SaveNoteAsync("session-note", "ephemeral", CancellationToken.None);

            var results = await store.ListNotesWithPrefixAsync("project:myapp:", CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, k => k.Contains("arch"));
            Assert.Contains(results, k => k.Contains("stack"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── IToolHook Interface Tests ────────────────────────────────────────

    [Fact]
    public void IToolHook_InterfaceShape()
    {
        var hook = Substitute.For<IToolHook>();
        hook.Name.Returns("test");

        Assert.Equal("test", hook.Name);
    }

    // ── CircuitBreaker ThrowIfOpen Tests ──────────────────────────────────

    [Fact]
    public void CircuitBreaker_ThrowIfOpen_DoesNotThrowWhenClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        cb.ThrowIfOpen(); // Should not throw
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingAsyncEnumerable()
    {
        await Task.CompletedTask;
        throw new HttpRequestException("Connection refused");
#pragma warning disable CS0162 // Unreachable code detected
        yield break;
#pragma warning restore CS0162
    }
}

public static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
