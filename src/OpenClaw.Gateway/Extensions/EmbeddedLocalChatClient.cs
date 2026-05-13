using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

internal sealed class EmbeddedLocalChatClient : IChatClient
{
    private readonly string _modelId;
    private readonly LocalInferenceSupervisor _supervisor;
    private readonly HttpClient _httpClient;
    private readonly LocalInferenceConfig _config;
    private readonly IVideoFrameExtractionService _videoFrames;

    public EmbeddedLocalChatClient(
        LlmProviderConfig config,
        LocalInferenceConfig localInference,
        LocalInferenceSupervisor? supervisor,
        HttpClient? httpClient)
        : this(config, localInference, null, supervisor, httpClient)
    {
    }

    public EmbeddedLocalChatClient(
        LlmProviderConfig config,
        LocalInferenceConfig localInference,
        MultimodalConfig? multimodal = null,
        LocalInferenceSupervisor? supervisor = null,
        HttpClient? httpClient = null,
        IVideoFrameExtractionService? videoFrames = null)
    {
        _modelId = string.IsNullOrWhiteSpace(config.Model) ? "gemma-local-small-q4" : config.Model.Trim();
        _supervisor = supervisor ?? new LocalInferenceSupervisor(localInference);
        _httpClient = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
        _config = localInference;
        _videoFrames = videoFrames ?? CreateDefaultVideoFrameExtractionService(multimodal ?? new MultimodalConfig());
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await _supervisor.EnsureRunningAsync(options?.ModelId ?? _modelId, cancellationToken);
        using var request = await BuildRequestAsync(endpoint, messages, options, stream: false, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var message = ParseAssistantMessage(document.RootElement);
        return new ChatResponse(message)
        {
            Usage = ParseUsage(document.RootElement)
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = await _supervisor.EnsureRunningAsync(options?.ModelId ?? _modelId, cancellationToken);
        using var request = await BuildRequestAsync(endpoint, messages, options, stream: true, cancellationToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var streamingToolCalls = new Dictionary<int, StreamingToolCallBuilder>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                foreach (var update in BuildStreamingToolCallUpdates(streamingToolCalls))
                    yield return update;
                yield break;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (TryReadStreamingText(root, out var text) && !string.IsNullOrEmpty(text))
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);

            CollectStreamingToolCalls(root, streamingToolCalls);

            var usage = ParseUsage(root);
            if (usage.InputTokenCount is > 0 || usage.OutputTokenCount is > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }

        foreach (var update in BuildStreamingToolCallUpdates(streamingToolCalls))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(LocalInferenceSupervisor) ? _supervisor : null;

    public void Dispose()
    {
        _httpClient.Dispose();
        _supervisor.Dispose();
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        LocalInferenceEndpoint endpoint,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? endpoint.Package.ModelId,
            ["stream"] = stream,
            ["messages"] = await SerializeMessagesAsync(messages, ct)
        };

        if (options?.Temperature is { } temperature)
            payload["temperature"] = temperature;
        if (options?.MaxOutputTokens is { } maxOutputTokens)
            payload["max_tokens"] = maxOutputTokens;
        if (options?.TopP is { } topP)
            payload["top_p"] = topP;
        if (options?.Seed is { } seed)
            payload["seed"] = seed;
        if (options?.StopSequences is { Count: > 0 } stop)
            payload["stop"] = new JsonArray(stop.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());

        if (!string.IsNullOrWhiteSpace(_config.ReasoningEffort))
            payload["reasoning_effort"] = _config.ReasoningEffort;

        if (options?.Tools is { Count: > 0 } tools)
        {
            var toolsArray = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is AIFunction function)
                {
                    toolsArray.Add((JsonNode?)new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = function.Name,
                            ["description"] = function.Description,
                            ["parameters"] = function.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? new JsonObject() : JsonNode.Parse(function.JsonSchema.GetRawText())
                        }
                    });
                }
            }
            payload["tools"] = toolsArray;
            payload["tool_choice"] = "auto";
            if (endpoint.Package.Capabilities.SupportsParallelToolCalls)
                payload["parallel_tool_calls"] = true;
        }

        return new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "v1/chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private async Task<JsonArray> SerializeMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var msgObj = new JsonObject
            {
                ["role"] = NormalizeRole(message.Role),
                ["content"] = await SerializeContentAsync(message, ct)
            };

            if (message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            {
                msgObj["tool_call_id"] = result.CallId;
                msgObj["content"] = result.Result?.ToString() ?? "";
            }

            var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count > 0)
            {
                var toolCallsArray = new JsonArray();
                foreach (var call in functionCalls)
                {
                    toolCallsArray.Add((JsonNode?)new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = JsonSerializer.Serialize(call.Arguments, CoreJsonContext.Default.Object)
                        }
                    });
                }
                msgObj["tool_calls"] = toolCallsArray;
            }

            array.Add((JsonNode?)msgObj);
        }

        return array;
    }

    private async Task<JsonNode> SerializeContentAsync(ChatMessage message, CancellationToken ct)
    {
        if (message.Contents.Count == 1 && message.Contents[0] is TextContent singleText)
        {
            return JsonValue.Create(singleText.Text ?? "")!;
        }

        var array = new JsonArray();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    array.Add((JsonNode?)new JsonObject { ["type"] = "text", ["text"] = text.Text ?? "" });
                    break;
                case DataContent data:
                {
                    var bytes = data.Data.ToArray();
                    if (IsVideo(data.MediaType))
                    {
                        await AddVideoFramesAsync(
                            array,
                            new VideoFrameExtractionRequest
                            {
                                SourceLabel = data.MediaType ?? "video/data",
                                MediaType = data.MediaType ?? "video/mp4",
                                Data = bytes,
                                FileName = "input-video"
                            },
                            ct);
                        break;
                    }

                    AddMediaUrl(
                        array,
                        GetOpenAiContentType(data.MediaType),
                        $"data:{data.MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}");
                    break;
                }
                case UriContent uri:
                {
                    if (IsVideo(uri.MediaType))
                    {
                        await AddVideoFramesAsync(
                            array,
                            new VideoFrameExtractionRequest
                            {
                                SourceLabel = uri.Uri?.ToString() ?? "video/uri",
                                MediaType = uri.MediaType ?? "video/mp4",
                                Uri = uri.Uri
                            },
                            ct);
                        break;
                    }

                    AddMediaUrl(array, GetOpenAiContentType(uri.MediaType), uri.Uri?.ToString() ?? "");
                    break;
                }
                case FunctionResultContent result:
                    // Only added for non-tool roles as fallback
                    array.Add((JsonNode?)new JsonObject { ["type"] = "text", ["text"] = result.Result?.ToString() ?? "" });
                    break;
            }
        }

        return array;
    }

    private async Task AddVideoFramesAsync(JsonArray array, VideoFrameExtractionRequest request, CancellationToken ct)
    {
        var result = await _videoFrames.ExtractFramesAsync(request, ct);
        if (!result.Succeeded || result.Frames.Count == 0)
        {
            array.Add((JsonNode?)new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"[VIDEO_UNAVAILABLE source={request.SourceLabel} reason={result.Issue ?? "no_frames"}]"
            });
            return;
        }

        var summary = new StringBuilder();
        summary.Append("[VIDEO_FRAMES");
        summary.Append(" source=").Append(request.SourceLabel);
        summary.Append(" frames=").Append(result.Frames.Count);
        if (result.DurationSeconds is { } duration)
            summary.Append(" duration_seconds=").Append(duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        summary.Append("]\nThe attached image_url parts are ordered sampled frames from the video.");
        if (result.AudioTranscript is { Text.Length: > 0 } transcript)
            summary.Append("\n[AUDIO_TRANSCRIPT provider=").Append(transcript.Provider).Append("]\n").Append(transcript.Text.Trim()).Append("\n[/AUDIO_TRANSCRIPT]");
        summary.Append("\n[/VIDEO_FRAMES]");

        array.Add((JsonNode?)new JsonObject
        {
            ["type"] = "text",
            ["text"] = summary.ToString()
        });

        foreach (var frame in result.Frames.OrderBy(static item => item.Index))
            AddMediaUrl(array, "image_url", frame.DataUrl);
    }

    private static bool IsVideo(string? mediaType)
        => mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetOpenAiContentType(string? mediaType)
    {
        if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return "image_url";
        if (mediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return "audio_url";
        return "file_url";
    }

    private static void AddMediaUrl(JsonArray array, string type, string url)
        => array.Add((JsonNode?)new JsonObject
        {
            ["type"] = type,
            [type] = new JsonObject
            {
                ["url"] = url
            }
        });

    private static IVideoFrameExtractionService CreateDefaultVideoFrameExtractionService(MultimodalConfig multimodal)
    {
        var config = new GatewayConfig { Multimodal = multimodal };
        return new VideoFrameExtractionService(
            config,
            new MediaCacheStore(multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);
    }

    private static ChatMessage ParseAssistantMessage(JsonElement root)
    {
        var content = "";
        var messageElement = default(JsonElement);

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out messageElement) &&
            messageElement.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            content = contentElement.GetString() ?? "";
        }

        var message = new ChatMessage(ChatRole.Assistant, (IList<AIContent>)new List<AIContent>());
        if (!string.IsNullOrEmpty(content))
        {
            message.Contents.Add(new TextContent(content));
        }

        if (messageElement.ValueKind == JsonValueKind.Object &&
            messageElement.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.TryGetProperty("function", out var function) &&
                    call.TryGetProperty("id", out var idElement))
                {
                    var name = function.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var args = function.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                    if (name is not null)
                    {
                        var dict = new Dictionary<string, object?>();
                        if (args is not null && args.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(args);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    dict[prop.Name] = prop.Value.Clone();
                                }
                            }
                            catch (JsonException)
                            {
                            }
                        }
                        message.Contents.Add(new FunctionCallContent(idElement.GetString() ?? "", name, dict));
                    }
                }
            }
        }

        return message;
    }

    private static bool TryReadStreamingText(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = content.GetString();
        return text is not null;
    }

    private static void CollectStreamingToolCalls(JsonElement root, Dictionary<int, StreamingToolCallBuilder> toolCalls)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : toolCalls.Count;
            if (!toolCalls.TryGetValue(index, out var builder))
            {
                builder = new StreamingToolCallBuilder();
                toolCalls[index] = builder;
            }

            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                builder.Id = id.GetString();

            if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                continue;

            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                builder.Name ??= name.GetString();
            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                builder.Arguments.Append(arguments.GetString());
        }
    }

    private static IEnumerable<ChatResponseUpdate> BuildStreamingToolCallUpdates(Dictionary<int, StreamingToolCallBuilder> toolCalls)
    {
        foreach (var item in toolCalls.OrderBy(static item => item.Key))
        {
            var builder = item.Value;
            if (string.IsNullOrWhiteSpace(builder.Name))
                continue;

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        string.IsNullOrWhiteSpace(builder.Id) ? $"call_{item.Key}" : builder.Id,
                        builder.Name!,
                        ParseArguments(builder.Arguments.ToString()))
                ]);
        }
    }

    private static Dictionary<string, object?> ParseArguments(string args)
    {
        var dict = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(args) || !args.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return dict;

        try
        {
            using var doc = JsonDocument.Parse(args);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        }
        catch (JsonException)
        {
        }

        return dict;
    }

    private sealed class StreamingToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private static UsageDetails ParseUsage(JsonElement root)
    {
        var usage = new UsageDetails();
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
            return usage;

        if (TryGetLong(usageElement, "prompt_tokens", out var inputTokens))
            usage.InputTokenCount = inputTokens;
        if (TryGetLong(usageElement, "completion_tokens", out var outputTokens))
            usage.OutputTokenCount = outputTokens;
        return usage;
    }

    private static bool TryGetLong(JsonElement root, string property, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out value) => true,
            JsonValueKind.String when long.TryParse(element.GetString(), out value) => true,
            _ => false
        };
    }

    private static async Task<Exception> CreateFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        return new InvalidOperationException($"Embedded local inference request failed with HTTP {(int)response.StatusCode}: {detail}");
    }

    private static string NormalizeRole(ChatRole role)
        => role == ChatRole.System ? "system"
            : role == ChatRole.Assistant ? "assistant"
            : role == ChatRole.Tool ? "tool"
            : "user";
}
