using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public class AgentRuntimeTests
{
    private readonly IChatClient _chatClient;
    private readonly IMemoryStore _memory;
    private readonly List<ITool> _tools;
    private readonly AgentRuntime _agent;
    private readonly LlmProviderConfig _config;

    public AgentRuntimeTests()
    {
        _chatClient = Substitute.For<IChatClient>();
        _memory = Substitute.For<IMemoryStore>();
        _tools = new List<ITool>();
        _config = new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" };
        
        // Mock default behavior for ChatClient
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), 
            Arg.Any<ChatOptions>(), 
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "Hello from AI") })));

        _agent = new AgentRuntime(_chatClient, _tools, _memory, _config, maxHistoryTurns: 5);
    }

    [Fact]
    public async Task RunAsync_SingleTurn_ReturnsResponse()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        var result = await _agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.Equal("Hello from AI", result);
        Assert.Contains(session.History, t => t.Role == "user" && t.Content == "Hello");
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello from AI");
    }

    [Fact]
    public async Task RunAsync_TrimsHistory()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        // Add more history than max (5)
        for (int i = 0; i < 10; i++)
        {
            session.History.Add(new ChatTurn { Role = "user", Content = $"msg {i}" });
        }

        await _agent.RunAsync(session, "New message", CancellationToken.None);

        // Max history turns is 5.
        // The implementation trims BEFORE adding the new user message? 
        // Let's check logic:
        // 1. Adds user message (now 11)
        // 2. Trims to max (5) -> keeps last 5
        // 3. Adds assistant message -> (6)
        // Wait, standard implementation usually keeps N turns (pairs) or N messages.
        // AgentRuntime.cs: session.History.RemoveRange(0, toRemove); 
        // It keeps exactly _maxHistoryTurns items in the list.
        // So checking the count should match.
        
        // However, the assistant response is added AFTER the trim call in the current logic?
        // Let's verify:
        // RunAsync:
        //   session.History.Add(userMessage);
        //   TrimHistory(session); // Count becomes _maxHistoryTurns
        //   ...
        //   session.History.Add(assistantMessage);
        // So final count should be _maxHistoryTurns + 1.
        
        Assert.True(session.History.Count <= 6, $"Expected history <= 6 but was {session.History.Count}"); 
    }
}
