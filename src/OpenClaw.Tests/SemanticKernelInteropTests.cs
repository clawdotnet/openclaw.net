using System.Text.Json;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.SemanticKernelAdapter;
using Xunit;
using AiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using AiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace OpenClaw.Tests;

public sealed class SemanticKernelInteropTests
{
    [Fact]
    public void ToolName_SanitizesAndCapsLength()
    {
        var name = SemanticKernelToolName.MakeToolName(
            prefix: "sk_",
            pluginName: "D	m Plugin !!!",
            functionName: new string('X', 200),
            maxLen: 64);

        Assert.True(name.Length <= 64);
        Assert.Equal(name, name.ToLowerInvariant());
        Assert.Matches("^[a-z0-9_-]+$", name);
        Assert.StartsWith("sk_", name);
    }

    [Fact]
    public async Task SemanticKernelFunctionTool_InvokesFunction()
    {
        // Arrange: small kernel with one plugin.
        async ValueTask<Kernel> Factory(CancellationToken ct)
        {
            var kb = Kernel.CreateBuilder();
            var k = kb.Build();
            k.Plugins.AddFromObject(new DemoPlugin(), "demo");
            return k;
        }

        var discovery = await Factory(CancellationToken.None);
        var tools = SemanticKernelToolFactory.CreateTools(Factory, discovery, new SemanticKernelInteropOptions
        {
            AllowedPlugins = ["demo"],
            MaxMappedTools = 16
        });

        var tool = Assert.Single(tools, t => t.Name == "sk_demo_echo");

        // Act
        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new { text = "hello" }), CancellationToken.None);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task AgentRuntime_PassesContextToContextAwareHooks()
    {
        var store = new FileMemoryStore(Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")));

        var tool = new SimpleTool("noop");
        var hook = new CaptureContextHook();

        var chat = new SingleToolCallChatClient("noop", new Dictionary<string, object?>());
        var runtime = new AgentRuntime(
            chat,
            new[] { tool },
            store,
            new LlmProviderConfig { Provider = "openai", Model = "test" },
            maxHistoryTurns: 8,
            hooks: new IToolHook[] { hook },
            parallelToolExecution: false);

        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "u1" };

        _ = await runtime.RunAsync(session, "hi", CancellationToken.None);

        Assert.NotNull(hook.Last);
        Assert.Equal("s1", hook.Last.Value.SessionId);
        Assert.Equal("test", hook.Last.Value.ChannelId);
        Assert.Equal("u1", hook.Last.Value.SenderId);
        Assert.Equal("noop", hook.Last.Value.ToolName);
        Assert.False(hook.Last.Value.IsStreaming);
        Assert.False(string.IsNullOrWhiteSpace(hook.Last.Value.CorrelationId));
    }

    [Fact]
    public async Task StreamingTool_EmitsToolChunksAndAggregatesResult()
    {
        var store = new FileMemoryStore(Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")));

        var tool = new ChunkTool();
        var chat = new StreamingToolCallChatClient(tool.Name);

        var runtime = new AgentRuntime(
            chat,
            new ITool[] { tool },
            store,
            new LlmProviderConfig { Provider = "openai", Model = "test" },
            maxHistoryTurns: 8,
            parallelToolExecution: true);

        var session = new Session { Id = "s1", ChannelId = "ws", SenderId = "u1" };

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in runtime.RunStreamingAsync(session, "hi", CancellationToken.None))
            events.Add(evt);

        Assert.Contains(events, e => e.EnvelopeType == "tool_start" && e.ToolName == tool.Name);
        Assert.Contains(events, e => e.EnvelopeType == "tool_chunk" && e.ToolName == tool.Name && e.Content == "a");
        Assert.Contains(events, e => e.EnvelopeType == "tool_chunk" && e.ToolName == tool.Name && e.Content == "b");
        Assert.Contains(events, e => e.EnvelopeType == "tool_chunk" && e.ToolName == tool.Name && e.Content == "c");
        Assert.Contains(events, e => e.EnvelopeType == "tool_result" && e.ToolName == tool.Name && e.Content == "abc");
    }

    private sealed class DemoPlugin
    {
        [KernelFunction, Description("Echo text back.")]
        public string Echo(string text) => text;
    }

    private sealed class SimpleTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "noop";
        public string ParameterSchema => "{\"type\":\"object\",\"properties\":{}}";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => new("ok");
    }

    private sealed class CaptureContextHook : IToolHookWithContext
    {
        public string Name => "capture";
        public ToolHookContext? Last { get; private set; }

        public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct) => new(true);
        public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct)
        {
            Last = context;
            return new(true);
        }

        public ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class SingleToolCallChatClient(string toolName, IDictionary<string, object?> args) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var hasToolResult = messages
                .SelectMany(m => m.Contents)
                .OfType<AiFunctionResultContent>()
                .Any();

            if (hasToolResult)
            {
                var doneMsg = new ChatMessage(ChatRole.Assistant, "done");
                return Task.FromResult(new ChatResponse(doneMsg));
            }

            var call = new AiFunctionCallContent("call_1", toolName, args.ToDictionary(k => k.Key, v => v.Value));
            var assistantMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { call });
            return Task.FromResult(new ChatResponse(assistantMsg));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ChunkTool : ITool, IStreamingTool
    {
        public string Name => "chunk";
        public string Description => "chunk";
        public string ParameterSchema => "{\"type\":\"object\",\"properties\":{}}";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => new("abc");

        public async IAsyncEnumerable<string> ExecuteStreamingAsync(string argumentsJson, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return "a";
            yield return "b";
            yield return "c";
        }
    }

    private sealed class StreamingToolCallChatClient(string toolName) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var hasToolResult = messages
                .SelectMany(m => m.Contents)
                .OfType<AiFunctionResultContent>()
                .Any();

            if (!hasToolResult)
            {
                var call = new AiFunctionCallContent("call_1", toolName, new Dictionary<string, object?>(StringComparer.Ordinal));
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { call });
                yield break;
            }

            // Final assistant output.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
