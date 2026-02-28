using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.SemanticKernelAdapter;
using AiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using AiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

var builder = WebApplication.CreateSlimBuilder(args);

// Reuse OpenClaw's source-generated JSON context for request/response DTOs.
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

var app = builder.Build();

// Ephemeral local file store (sample only).
var basePath = Path.Combine(Path.GetTempPath(), "openclaw-sk-sample", Guid.NewGuid().ToString("N"));
var memoryStore = new FileMemoryStore(basePath);

// Deterministic chat client that forces one SK tool call, then returns the tool result.
var chatClient = new DeterministicToolCallChatClient();

// Build SK kernel factory and tools.
static ValueTask<Kernel> CreateKernelAsync(CancellationToken ct)
{
    // Keep kernel creation simple and deterministic for the sample.
    var builder = Kernel.CreateBuilder();
    var kernel = builder.Build();

    // Demo plugin is added under the plugin name "demo".
    kernel.Plugins.AddFromObject(new DemoPlugin(), "demo");
    return ValueTask.FromResult(kernel);
}

Func<CancellationToken, ValueTask<Kernel>> kernelFactory = CreateKernelAsync;
var discoveryKernel = await kernelFactory(CancellationToken.None);

var interopOptions = new SemanticKernelInteropOptions
{
    ToolNamePrefix = "sk_",
    AllowedPlugins = ["demo"],
    MaxMappedTools = 32,
    MaxToolNameLength = 64
};

var skTools = SemanticKernelToolFactory.CreateTools(kernelFactory, discoveryKernel, interopOptions);
var entrypoint = new SemanticKernelEntrypointTool(kernelFactory);

IReadOnlyList<ITool> tools = [entrypoint, ..skTools];

// Minimal LLM config (only used for AgentRuntime settings, not for any real provider calls).
var llmConfig = new LlmProviderConfig { Provider = "openai", Model = "sample" };

var agentRuntime = new AgentRuntime(
    chatClient,
    tools,
    memoryStore,
    llmConfig,
    maxHistoryTurns: 16,
    logger: null,
    toolTimeoutSeconds: 30,
    parallelToolExecution: false,
    enableCompaction: false,
    requireToolApproval: false,
    hooks: []);

app.MapPost("/v1/responses", async (HttpContext ctx) =>
{
    OpenAiResponseRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync(ctx.Request.Body,
            CoreJsonContext.Default.OpenAiResponseRequest, ctx.RequestAborted);
    }
    catch
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Invalid JSON request body.", ctx.RequestAborted);
        return;
    }

    if (req is null || string.IsNullOrWhiteSpace(req.Input))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Request must include an 'input' field.", ctx.RequestAborted);
        return;
    }

    using var activity = Telemetry.ActivitySource.StartActivity("Sample.Request");

    var session = new Session
    {
        Id = $"sample:{Guid.NewGuid():N}",
        ChannelId = "sample-http",
        SenderId = "anon"
    };

    var result = await agentRuntime.RunAsync(session, req.Input, ctx.RequestAborted);

    var responseId = $"resp-{Guid.NewGuid():N}"[..24];
    var msgId = $"msg-{Guid.NewGuid():N}"[..23];

    var response = new OpenAiResponseResponse
    {
        Id = responseId,
        Status = "completed",
        Output =
        [
            new OpenAiResponseOutput
            {
                Id = msgId,
                Role = "assistant",
                Content = [new OpenAiResponseContent { Text = result }]
            }
        ],
        Usage = new OpenAiUsage
        {
            PromptTokens = (int)session.TotalInputTokens,
            CompletionTokens = (int)session.TotalOutputTokens,
            TotalTokens = (int)(session.TotalInputTokens + session.TotalOutputTokens)
        }
    };

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(
        JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse),
        ctx.RequestAborted);
});

app.MapGet("/", () => Results.Text(
    "OpenClaw.SemanticKernelInteropHost\n\n" +
    "POST /v1/responses { \"input\": \"hello\" }\n\n" +
    "This sample uses a deterministic chat client that forces one SK tool call (sk_demo_echo).\n"));

app.Run();

file sealed class DemoPlugin
{
    [KernelFunction, Description("Echo text back.")]
    public string Echo(string text) => text;

    [KernelFunction, Description("Reverse text.")]
    public string Reverse(string text) => new string((text ?? "").Reverse().ToArray());

    [KernelFunction, Description("Add two integers passed as strings.")]
    public string Add(string a, string b)
    {
        _ = int.TryParse(a, out var ai);
        _ = int.TryParse(b, out var bi);
        return (ai + bi).ToString();
    }
}

file sealed class DeterministicToolCallChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // If the tool already ran, return the tool result as assistant text.
        var toolResult = messages
            .SelectMany(m => m.Contents)
            .OfType<AiFunctionResultContent>()
            .LastOrDefault();

        if (toolResult is not null)
        {
            var text = toolResult.Result?.ToString() ?? "";
            var assistant = new ChatMessage(ChatRole.Assistant, $"SK result: {text}");
            return Task.FromResult(new ChatResponse(assistant));
        }

        var userText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                       ?? messages.LastOrDefault(m => m.Role == ChatRole.User)?.ToString()
                       ?? "";

        var call = new AiFunctionCallContent(
            callId: "call_1",
            name: "sk_demo_echo",
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = userText });

        var assistantMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { call });
        return Task.FromResult(new ChatResponse(assistantMsg));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Provide a trivial streaming implementation for completeness.
        var resp = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var msg in resp.Messages)
            yield return new ChatResponseUpdate(msg.Role, msg.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => null;

    public void Dispose() { }
}
