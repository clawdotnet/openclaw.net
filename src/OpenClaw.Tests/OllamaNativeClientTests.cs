using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OllamaNativeClientTests
{
    [Fact]
    public async Task ChatClient_UsesNativeApiChat_AndParsesToolCallsAndUsage()
    {
        var requests = new List<(Uri Uri, string Body)>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"message":{"content":"READY","tool_calls":[{"function":{"name":"record_observation","arguments":{"value":"gemma4"}}}]},"done_reason":"stop","prompt_eval_count":12,"eval_count":4}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        using var client = new OllamaChatClient(
            new LlmProviderConfig
            {
                Provider = "ollama",
                Model = "llama3.2",
                Endpoint = "http://127.0.0.1:11434/v1"
            },
            httpClient);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.CreateDeclaration(
                        "record_observation",
                        "Record an observation",
                        JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""").RootElement.Clone(),
                        returnJsonSchema: null)
                ]
            },
            CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("/api/chat", request.Uri.AbsolutePath);
        Assert.DoesNotContain("/v1", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"record_observation\"", request.Body, StringComparison.Ordinal);
        var assistant = Assert.Single(response.Messages);
        Assert.Contains(assistant.Contents.OfType<TextContent>(), content => content.Text == "READY");
        Assert.Contains(assistant.Contents.OfType<FunctionCallContent>(), content => content.Name == "record_observation");
        Assert.Equal(12, response.Usage?.InputTokenCount);
        Assert.Equal(4, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task ChatClient_Streaming_UsesNativeApiChatStream_AndEmitsUsage()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"message":{"content":"Hel"},"done":false}
                    {"message":{"content":"lo"},"done":false}
                    {"done":true,"prompt_eval_count":7,"eval_count":3}
                    """.ReplaceLineEndings("\n"),
                    Encoding.UTF8,
                    "application/json")
            })));

        using var client = new OllamaChatClient(
            new LlmProviderConfig
            {
                Provider = "ollama",
                Model = "llama3.2"
            },
            httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: CancellationToken.None))
            updates.Add(update);

        Assert.Equal("Hello", string.Concat(updates.SelectMany(static update => update.Contents).OfType<TextContent>().Select(static content => content.Text)));
        var usage = Assert.Single(updates.SelectMany(static update => update.Contents).OfType<UsageContent>());
        Assert.Equal(7, usage.Details.InputTokenCount);
        Assert.Equal(3, usage.Details.OutputTokenCount);
    }

    [Fact]
    public async Task EmbeddingGenerator_UsesNativeApiEmbed()
    {
        var requests = new List<(Uri Uri, string Body)>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        using var generator = new OllamaEmbeddingGenerator(
            new LlmProviderConfig
            {
                Provider = "ollama",
                Model = "llama3.2",
                Endpoint = "http://127.0.0.1:11434/v1"
            },
            "nomic-embed-text",
            httpClient);

        var embeddings = await generator.GenerateAsync(["alpha", "beta"], cancellationToken: CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("/api/embed", request.Uri.AbsolutePath);
        Assert.DoesNotContain("/v1", request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("\"nomic-embed-text\"", request.Body, StringComparison.Ordinal);
        Assert.Equal(2, embeddings.Count);
        Assert.Equal(0.1f, embeddings[0].Vector.Span[0]);
        Assert.Equal(0.6f, embeddings[1].Vector.Span[2]);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
