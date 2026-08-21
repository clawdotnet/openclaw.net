using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Tests.Stubs;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class SelectorBackedChatClientTests
{
    private readonly StubAgentSelector _selector = new();
    private readonly RunIdAgentSelectionCache _cache = new();
    private readonly IChatClient _mock = Substitute.For<IChatClient>();
    private readonly IChatClient _fast = Substitute.For<IChatClient>();
    private readonly SelectorOptions _options = new()
    {
        Enabled = true,
        AvailableAgents = new[] { "mock", "mock-fast" },
        TaskCategory = "General",
        InnerClients = new Dictionary<string, IChatClient>(),
    };

    private SelectorBackedChatClient BuildSut() => new(
        _selector,
        _cache,
        _mock,
        new Dictionary<string, IChatClient> { ["mock"] = _mock, ["mock-fast"] = _fast },
        _options,
        NullLogger<SelectorBackedChatClient>.Instance);

    private static ChatOptions Opts(string runId, string stepName) => new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["runId"] = runId,
            ["stepName"] = stepName,
        },
    };

    [Fact]
    public async Task Routes_To_Selected_Inner_Client_And_Records_Selection()
    {
        _selector.AgentId = "mock-fast";
        _mock.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from-mock")));
        _fast.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "from-fast")));

        var sut = BuildSut();
        var response = await sut.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            Opts("run-1", "SecurityReviewer"));

        Assert.Equal("from-fast", response.Messages[0].Text);
        var cached = _cache.TryGet("run-1", "SecurityReviewer");
        Assert.NotNull(cached);
        Assert.Equal("mock-fast", cached!.Value.AgentId);
    }

    [Fact]
    public async Task Falls_Back_To_Default_When_Selection_Fails()
    {
        _selector.SelectShouldFail = true;
        _mock.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback")));

        var sut = BuildSut();
        var response = await sut.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            Opts("run-1", "SecurityReviewer"));

        Assert.Equal("fallback", response.Messages[0].Text);
        Assert.Null(_cache.TryGet("run-1", "SecurityReviewer"));
    }

    [Fact]
    public async Task Falls_Back_To_Default_When_Selected_Inner_Client_Missing()
    {
        _selector.AgentId = "ghost"; // not in InnerClients
        _mock.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "default")));

        var sut = BuildSut();
        var response = await sut.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            Opts("run-1", "SecurityReviewer"));

        Assert.Equal("default", response.Messages[0].Text);
        Assert.Null(_cache.TryGet("run-1", "SecurityReviewer")); // no record → can never correlate back
    }

    [Fact]
    public async Task Streaming_Call_Also_Routes_To_Selected_Inner()
    {
        _selector.AgentId = "mock-fast";
        _fast.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ChatResponseUpdate(ChatRole.Assistant, "streamed") }.ToAsyncEnumerable());

        var sut = BuildSut();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            Opts("run-1", "SecurityReviewer")))
        {
            updates.Add(u);
        }

        Assert.Single(updates);
        Assert.Equal("streamed", updates[0].Text);
    }

    [Fact]
    public async Task Skips_Cache_Write_When_ChatOptions_Lacks_RunId_StepName()
    {
        _mock.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = BuildSut();
        await sut.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            options: null); // no runId/stepName

        Assert.Equal(0, _cache.Count); // nothing recorded — outcomes can never correlate
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        await Task.Yield();
        foreach (var item in source)
        {
            yield return item;
        }
    }
}
