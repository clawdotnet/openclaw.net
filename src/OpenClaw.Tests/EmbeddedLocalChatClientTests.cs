using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public class EmbeddedLocalChatClientTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"test response\"}}]}")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return ResponseToReturn;
        }
    }

    private class FakeLocalInferenceSupervisor : LocalInferenceSupervisor
    {
        public FakeLocalInferenceSupervisor() : base(new LocalInferenceConfig()) { }
        public override Task<LocalInferenceEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct = default)
        {
            return Task.FromResult(new LocalInferenceEndpoint(
                new Uri("http://localhost:8080/"),
                new OpenClaw.Core.Models.LocalModelPackageDefinition {
                    Id = "fake-id",
                    PresetId = "fake-preset",
                    Capabilities = new ModelCapabilities { SupportsParallelToolCalls = true },
                    ModelId = modelId
                },
                "/fake/path/model.gguf"));
        }
    }

    private sealed class FakeVideoFrameExtractionService : IVideoFrameExtractionService
    {
        public VideoFrameExtractionRequest? LastRequest { get; private set; }

        public Task<VideoFrameExtractionResult> ExtractFramesAsync(VideoFrameExtractionRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new VideoFrameExtractionResult
            {
                Succeeded = true,
                SourceLabel = request.SourceLabel,
                DurationSeconds = 6,
                Frames =
                [
                    new()
                    {
                        Index = 0,
                        Timestamp = TimeSpan.Zero,
                        DataUrl = "data:image/jpeg;base64,Zmlyc3Q=",
                        Asset = new StoredMediaAsset
                        {
                            Id = "media_first",
                            MediaType = "image/jpeg",
                            FileName = "frame-001.jpg",
                            Path = "/tmp/frame-001.jpg",
                            SizeBytes = 5
                        }
                    },
                    new()
                    {
                        Index = 1,
                        Timestamp = TimeSpan.FromSeconds(5),
                        DataUrl = "data:image/jpeg;base64,c2Vjb25k",
                        Asset = new StoredMediaAsset
                        {
                            Id = "media_second",
                            MediaType = "image/jpeg",
                            FileName = "frame-002.jpg",
                            Path = "/tmp/frame-002.jpg",
                            SizeBytes = 6
                        }
                    }
                ]
            });
        }
    }

    [Fact]
    public async Task GetResponseAsync_SerializesToolsCorrectly()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var supervisor = new FakeLocalInferenceSupervisor();

        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig { Provider = "embedded", Model = "gemma-local-small-q4" },
            new LocalInferenceConfig(),
            supervisor,
            httpClient);

        var messages = new[] { new ChatMessage(ChatRole.User, "Call a tool") };
        var options = new ChatOptions
        {
            Tools = new[]
            {
                AIFunctionFactory.Create((string location) => "Sunny", "get_weather", "Get the weather")
            }
        };

        await client.GetResponseAsync(messages, options);

        Assert.NotNull(handler.LastRequestBody);
        var doc = JsonDocument.Parse(handler.LastRequestBody);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("tools", out var toolsArray));
        Assert.Equal(JsonValueKind.Array, toolsArray.ValueKind);
        Assert.Equal(1, toolsArray.GetArrayLength());

        var tool = toolsArray[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("get_weather", function.GetProperty("name").GetString());
        Assert.Equal("Get the weather", function.GetProperty("description").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
    }

    [Fact]
    public async Task GetResponseAsync_ParsesToolCallsCorrectly()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"
                {
                    ""choices"": [
                        {
                            ""message"": {
                                ""content"": null,
                                ""tool_calls"": [
                                    {
                                        ""id"": ""call_123"",
                                        ""type"": ""function"",
                                        ""function"": {
                                            ""name"": ""get_weather"",
                                            ""arguments"": ""{\""location\"":\""Paris\""}""
                                        }
                                    }
                                ]
                            }
                        }
                    ]
                }")
            }
        };
        var httpClient = new HttpClient(handler);
        var supervisor = new FakeLocalInferenceSupervisor();

        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig { Provider = "embedded", Model = "gemma-local-small-q4" },
            new LocalInferenceConfig(),
            supervisor,
            httpClient);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        Assert.Single(response.Messages[0].Contents);
        var toolCall = Assert.IsType<FunctionCallContent>(response.Messages[0].Contents[0]);
        Assert.Equal("call_123", toolCall.CallId);
        Assert.Equal("get_weather", toolCall.Name);
        Assert.NotNull(toolCall.Arguments);
        Assert.True(toolCall.Arguments.ContainsKey("location"));
    }

    [Fact]
    public async Task GetResponseAsync_SerializesImagesCorrectly()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var supervisor = new FakeLocalInferenceSupervisor();

        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig { Provider = "embedded", Model = "gemma-local-small-q4" },
            new LocalInferenceConfig(),
            supervisor,
            httpClient);

        var messages = new[] {
            new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent("Look at this"),
                new UriContent(new Uri("https://example.com/image.jpg"), "image/jpeg")
            })
        };

        await client.GetResponseAsync(messages);

        Assert.NotNull(handler.LastRequestBody);
        var doc = JsonDocument.Parse(handler.LastRequestBody);
        var contentArray = doc.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, contentArray.ValueKind);
        Assert.Equal(2, contentArray.GetArrayLength());

        Assert.Equal("text", contentArray[0].GetProperty("type").GetString());
        Assert.Equal("Look at this", contentArray[0].GetProperty("text").GetString());

        Assert.Equal("image_url", contentArray[1].GetProperty("type").GetString());
        Assert.Equal("https://example.com/image.jpg", contentArray[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetResponseAsync_ExpandsVideoIntoOrderedImageFrames()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var supervisor = new FakeLocalInferenceSupervisor();
        var videoFrames = new FakeVideoFrameExtractionService();

        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig { Provider = "embedded", Model = "gemma-4-e4b" },
            new LocalInferenceConfig(),
            multimodal: new MultimodalConfig(),
            supervisor: supervisor,
            httpClient: httpClient,
            videoFrames: videoFrames);

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent("What happens?"),
                new DataContent(new BinaryData([1, 2, 3]), "video/mp4")
            })
        };

        await client.GetResponseAsync(messages);

        Assert.NotNull(videoFrames.LastRequest);
        Assert.Equal("video/mp4", videoFrames.LastRequest!.MediaType);
        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody);
        var contentArray = doc.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, contentArray.ValueKind);
        Assert.DoesNotContain("video_url", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text", contentArray[0].GetProperty("type").GetString());
        Assert.Equal("text", contentArray[1].GetProperty("type").GetString());
        Assert.Equal("image_url", contentArray[2].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,Zmlyc3Q=", contentArray[2].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", contentArray[3].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,c2Vjb25k", contentArray[3].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ParsesStreamingToolCallsCorrectly()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_123","type":"function","function":{"name":"get_weather","arguments":"{\"location\""}}]}}]}

                    data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"Paris\"}"}}]}}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream")
            }
        };
        var httpClient = new HttpClient(handler);
        var supervisor = new FakeLocalInferenceSupervisor();

        using var client = new EmbeddedLocalChatClient(
            new LlmProviderConfig { Provider = "embedded", Model = "gemma-local-small-q4" },
            new LocalInferenceConfig(),
            supervisor,
            httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
            updates.Add(update);

        var toolCall = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call_123", toolCall.CallId);
        Assert.Equal("get_weather", toolCall.Name);
        Assert.NotNull(toolCall.Arguments);
        Assert.True(toolCall.Arguments.ContainsKey("location"));
    }
}
