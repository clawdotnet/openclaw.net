using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;

var memoryPath = Path.Join(Path.GetTempPath(), "openclaw-hello-agent", Guid.NewGuid().ToString("N"));
await using var memoryStore = new FileMemoryStore(memoryPath);

var agent = new AgentRuntime(
    new HelloChatClient(),
    [new EchoTool()],
    memoryStore,
    new LlmProviderConfig
    {
        Provider = "deterministic",
        Model = "hello-agent",
        TimeoutSeconds = 0,
        RetryCount = 0
    },
    maxHistoryTurns: 8,
    parallelToolExecution: false);

var session = new Session
{
    Id = $"hello:{Guid.NewGuid():N}",
    ChannelId = "sample",
    SenderId = "developer"
};

var prompt = args.Length > 0 ? string.Join(' ', args) : "hello";
var response = await agent.RunAsync(session, prompt, CancellationToken.None);

Console.WriteLine("OpenClaw.HelloAgent");
Console.WriteLine($"User: {prompt}");
Console.WriteLine(response);

file sealed class EchoTool : ITool
{
    public string Name => "hello_echo";
    public string Description => "Echoes the supplied text for the hello-agent sample.";
    public string ParameterSchema => """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var text = doc.RootElement.TryGetProperty("text", out var value)
            ? value.GetString() ?? ""
            : "";
        return ValueTask.FromResult($"Tool: echo({text}): ok");
    }
}

file sealed class HelloChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var toolResult = messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .LastOrDefault();

        if (toolResult is not null)
        {
            var result = toolResult.Result?.ToString() ?? "";
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $"Agent: hello from OpenClaw.NET{Environment.NewLine}{result}")));
        }

        var userText = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text ?? "hello";
        var call = new FunctionCallContent(
            callId: "call_hello_echo",
            name: "hello_echo",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = userText
            });

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
