using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Gateway.Extensions;

internal sealed class OllamaChatClient : IChatClient
{
    private readonly string _defaultModel;
    private readonly Uri _chatUri;
    private readonly HttpClient _httpClient;

    public OllamaChatClient(LlmProviderConfig config, HttpClient? httpClient = null)
    {
        _defaultModel = config.Model;
        _chatUri = new Uri($"{OllamaEndpointNormalizer.NormalizeBaseUrl(config.Endpoint).TrimEnd('/')}/api/chat", UriKind.Absolute);
        _httpClient = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(messages, options, stream: false);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var assistantContents = ParseAssistantContents(document.RootElement);
        var usage = ParseUsage(document.RootElement);
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, assistantContents))
        {
            Usage = usage,
            AdditionalProperties = new AdditionalPropertiesDictionary()
        };

        if (document.RootElement.TryGetProperty("done_reason", out var doneReason) && doneReason.ValueKind == JsonValueKind.String)
            chatResponse.AdditionalProperties["done_reason"] = doneReason.GetString();

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(messages, options, stream: true);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (TryGetMessageText(root, out var text) && !string.IsNullOrEmpty(text))
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);

            var toolCalls = ParseToolCalls(root);
            if (toolCalls.Count > 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant, toolCalls);

            if (root.TryGetProperty("done", out var doneElement) &&
                doneElement.ValueKind == JsonValueKind.True)
            {
                var usage = ParseUsage(root);
                if (usage.InputTokenCount is > 0 || usage.OutputTokenCount is > 0)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? _defaultModel,
            ["stream"] = stream,
            ["messages"] = SerializeMessages(messages)
        };

        if (ShouldSendTools(options))
            payload["tools"] = SerializeTools(options!.Tools!);

        var format = SerializeFormat(options?.ResponseFormat);
        if (format is not null)
            payload["format"] = format;

        var ollamaOptions = new JsonObject();
        if (options?.Temperature is { } temperature)
            ollamaOptions["temperature"] = temperature;
        if (options?.MaxOutputTokens is { } maxOutputTokens)
            ollamaOptions["num_predict"] = maxOutputTokens;
        if (options?.TopP is { } topP)
            ollamaOptions["top_p"] = topP;
        if (options?.TopK is { } topK)
            ollamaOptions["top_k"] = topK;
        if (options?.Seed is { } seed)
            ollamaOptions["seed"] = seed;

        if (ollamaOptions.Count > 0)
            payload["options"] = ollamaOptions;

        return new HttpRequestMessage(HttpMethod.Post, _chatUri)
        {
            Content = CreateJsonContent(payload)
        };
    }

    private static JsonArray SerializeMessages(IEnumerable<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            foreach (var serialized in SerializeMessage(message))
                array.Add((JsonNode)serialized);
        }

        return array;
    }

    private static IEnumerable<JsonObject> SerializeMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length > 0)
            {
                foreach (var result in results)
                {
                    yield return new JsonObject
                    {
                        ["role"] = "tool",
                        ["content"] = result.Result?.ToString() ?? string.Empty
                    };
                }

                yield break;
            }
        }

        var textBuilder = new StringBuilder();
        var images = new JsonArray();
        var toolCalls = new JsonArray();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    textBuilder.Append(text.Text);
                    break;
                case FunctionCallContent call:
                    toolCalls.Add((JsonNode)new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = ToJsonNode(call.Arguments)
                        }
                    });
                    break;
                case UriContent uri when uri.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true:
                    var image = TrySerializeImage(uri.Uri);
                    if (!string.IsNullOrWhiteSpace(image))
                        images.Add((JsonNode?)JsonValue.Create(image));
                    break;
            }
        }

        var obj = new JsonObject
        {
            ["role"] = NormalizeRole(message.Role),
            ["content"] = textBuilder.ToString()
        };
        if (images.Count > 0)
            obj["images"] = images;
        if (toolCalls.Count > 0)
            obj["tool_calls"] = toolCalls;

        yield return obj;
    }

    private static string NormalizeRole(ChatRole role)
        => role == ChatRole.System ? "system"
            : role == ChatRole.Assistant ? "assistant"
            : role == ChatRole.Tool ? "tool"
            : "user";

    private static bool ShouldSendTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 })
            return false;

        return options.ToolMode is null || !options.ToolMode.Equals(ChatToolMode.None);
    }

    private static JsonArray SerializeTools(IEnumerable<AITool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
            array.Add((JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = declaration?.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? null
                        : JsonNode.Parse(declaration!.JsonSchema.GetRawText())
                }
            });
        }

        return array;
    }

    private static JsonNode? SerializeFormat(ChatResponseFormat? format)
    {
        if (format is null)
            return null;

        if (format is ChatResponseFormatJson json)
        {
            if (json.Schema is { } schema &&
                schema.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return JsonNode.Parse(schema.GetRawText());
            }

            return JsonValue.Create("json");
        }

        return null;
    }

    private static List<AIContent> ParseAssistantContents(JsonElement root)
    {
        var contents = new List<AIContent>();
        if (TryGetMessageText(root, out var text) && !string.IsNullOrWhiteSpace(text))
            contents.Add(new TextContent(text));

        contents.AddRange(ParseToolCalls(root));
        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));

        return contents;
    }

    private static List<AIContent> ParseToolCalls(JsonElement root)
    {
        if (!TryGetToolCalls(root, out var toolCalls))
            return [];

        var contents = new List<AIContent>();
        var index = 0;
        foreach (var item in toolCalls.EnumerateArray())
        {
            if (!item.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (function.TryGetProperty("arguments", out var argumentsElement))
            {
                if (argumentsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in argumentsElement.EnumerateObject())
                        arguments[property.Name] = ToObject(property.Value);
                }
                else if (argumentsElement.ValueKind == JsonValueKind.String)
                {
                    var raw = argumentsElement.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        using var parsed = JsonDocument.Parse(raw);
                        if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in parsed.RootElement.EnumerateObject())
                                arguments[property.Name] = ToObject(property.Value);
                        }
                    }
                }
            }

            contents.Add(new FunctionCallContent($"ollama_call_{++index}", nameElement.GetString()!, arguments));
        }

        return contents;
    }

    private static bool TryGetMessageText(JsonElement root, out string? text)
    {
        text = null;
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetToolCalls(JsonElement root, out JsonElement toolCalls)
    {
        toolCalls = default;
        return root.TryGetProperty("message", out var message) &&
               message.TryGetProperty("tool_calls", out toolCalls) &&
               toolCalls.ValueKind == JsonValueKind.Array;
    }

    private static UsageDetails ParseUsage(JsonElement root)
    {
        var usage = new UsageDetails();
        if (root.TryGetProperty("prompt_eval_count", out var input) && input.ValueKind == JsonValueKind.Number && input.TryGetInt64(out var inputCount))
            usage.InputTokenCount = inputCount;
        if (root.TryGetProperty("eval_count", out var output) && output.ValueKind == JsonValueKind.Number && output.TryGetInt64(out var outputCount))
            usage.OutputTokenCount = outputCount;
        return usage;
    }

    private static object? ToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TrySerializeImage(Uri uri)
    {
        if (uri.IsFile && File.Exists(uri.LocalPath))
            return Convert.ToBase64String(File.ReadAllBytes(uri.LocalPath));

        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            var raw = uri.OriginalString;
            var comma = raw.IndexOf(',');
            if (comma >= 0)
                return raw[(comma + 1)..];
        }

        return null;
    }

    internal static HttpContent CreateJsonContent(JsonNode payload)
        => new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonNode node)
            return node.DeepClone();

        if (value is JsonElement element)
            return JsonNode.Parse(element.GetRawText());

        if (value is string text)
            return JsonValue.Create(text);

        if (value is bool boolean)
            return JsonValue.Create(boolean);

        if (value is int int32)
            return JsonValue.Create(int32);

        if (value is long int64)
            return JsonValue.Create(int64);

        if (value is float single)
            return JsonValue.Create(single);

        if (value is double dbl)
            return JsonValue.Create(dbl);

        if (value is decimal dec)
            return JsonValue.Create(dec);

        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key?.ToString() is not { } key)
                    continue;

                obj[key] = ToJsonNode(entry.Value);
            }

            return obj;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var array = new JsonArray();
            foreach (var item in enumerable)
                array.Add(ToJsonNode(item));

            return array;
        }

        return JsonValue.Create(value.ToString());
    }

    internal static async Task<Exception> CreateFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"Ollama request failed with HTTP {(int)response.StatusCode}.";
        if (!string.IsNullOrWhiteSpace(detail))
            message += $" {(detail.Length <= 400 ? detail : detail[..400])}";

        return new HttpRequestException(message, null, response.StatusCode);
    }
}

internal sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Uri _embedUri;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public OllamaEmbeddingGenerator(LlmProviderConfig config, string embeddingModel, HttpClient? httpClient = null)
    {
        _model = embeddingModel;
        _embedUri = new Uri($"{OllamaEndpointNormalizer.NormalizeBaseUrl(config.Endpoint).TrimEnd('/')}/api/embed", UriKind.Absolute);
        _httpClient = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
        Metadata = new EmbeddingGeneratorMetadata("ollama");
    }

    public EmbeddingGeneratorMetadata Metadata { get; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["input"] = SerializeInputs(values)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _embedUri)
        {
            Content = OllamaChatClient.CreateJsonContent(payload)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await OllamaChatClient.CreateFailureAsync(response, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("embeddings", out var embeddingsElement) ||
            embeddingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Ollama embedding response did not include an embeddings array.");
        }

        var generated = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var embedding in embeddingsElement.EnumerateArray())
        {
            generated.Add(new Embedding<float>(embedding.EnumerateArray().Select(static item => item.GetSingle()).ToArray()));
        }

        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static JsonArray SerializeInputs(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));

        return array;
    }
}
