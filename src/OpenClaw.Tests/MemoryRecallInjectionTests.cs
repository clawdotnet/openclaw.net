using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MemoryRecallInjectionTests
{
    [Fact]
    public async Task RunAsync_InsertsRelevantMemorySystemMessage_WhenEnabled()
    {
        var chatClient = Substitute.For<IChatClient>();

        IList<ChatMessage>? captured = null;
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(m => captured = m),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "ok") })));

        var memory = Substitute.For<IMemoryStore, IMemoryNoteSearch>();
        var search = (IMemoryNoteSearch)memory;
        search.SearchNotesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<MemoryNoteHit>>(new List<MemoryNoteHit>
            {
                new() { Key = "note:1", Content = "remember this", UpdatedAt = DateTimeOffset.UtcNow, Score = 1 }
            }));

        var agent = new AgentRuntime(
            chatClient,
            tools: [],
            memory,
            new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            maxHistoryTurns: 5,
            recall: new MemoryRecallConfig { Enabled = true, MaxNotes = 5, MaxChars = 4000 });

        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "u1" };
        _ = await agent.RunAsync(session, "what should I remember?", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains(captured!, m => m.Role == ChatRole.System && (m.Text ?? "").Contains("[Relevant memory]", StringComparison.Ordinal));
    }
}
