using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Gateway.Extensions;

internal readonly record struct LlmClientTransportOptions(Uri? Endpoint, int HiddenRetryCount);

public static class LlmClientFactory
{
    private sealed class DynamicProviderRegistration
    {
        public required string OwnerId { get; init; }
        public required IChatClient Client { get; init; }
    }

    private static readonly ConcurrentDictionary<string, DynamicProviderRegistration> _dynamicProviders = new(StringComparer.OrdinalIgnoreCase);

    public enum DynamicProviderRegistrationResult
    {
        Registered,
        Duplicate
    }

    /// <summary>
    /// Registers a dynamic provider (e.g. from a plugin bridge).
    /// </summary>
    public static void RegisterProvider(string providerName, IChatClient client)
    {
        _dynamicProviders[providerName] = new DynamicProviderRegistration
        {
            OwnerId = "manual",
            Client = client
        };
    }

    public static DynamicProviderRegistrationResult TryRegisterProvider(string providerName, IChatClient client, string ownerId)
    {
        var registration = new DynamicProviderRegistration
        {
            OwnerId = ownerId,
            Client = client
        };

        return _dynamicProviders.TryAdd(providerName, registration)
            ? DynamicProviderRegistrationResult.Registered
            : DynamicProviderRegistrationResult.Duplicate;
    }

    public static void UnregisterProvidersOwnedBy(string ownerId)
    {
        foreach (var entry in _dynamicProviders)
        {
            if (string.Equals(entry.Value.OwnerId, ownerId, StringComparison.Ordinal))
                _dynamicProviders.TryRemove(entry.Key, out _);
        }
    }

    public static void ResetDynamicProviders()
    {
        _dynamicProviders.Clear();
    }

    public static IReadOnlyDictionary<string, string> GetDynamicProviderOwners()
        => _dynamicProviders.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.OwnerId,
            StringComparer.OrdinalIgnoreCase);

    public static IChatClient CreateChatClient(LlmProviderConfig config)
        => CreateChatClientCore(config, localInference: null, multimodal: null, videoFrames: null);

    public static IChatClient CreateChatClient(
        LlmProviderConfig config,
        LocalInferenceConfig? localInference = null,
        MultimodalConfig? multimodal = null)
        => CreateChatClientCore(config, localInference, multimodal, videoFrames: null);

    internal static IChatClient CreateChatClient(
        LlmProviderConfig config,
        LocalInferenceConfig? localInference,
        MultimodalConfig? multimodal,
        IVideoFrameExtractionService? videoFrames)
        => CreateChatClientCore(config, localInference, multimodal, videoFrames);

    private static IChatClient CreateChatClientCore(
        LlmProviderConfig config,
        LocalInferenceConfig? localInference,
        MultimodalConfig? multimodal,
        IVideoFrameExtractionService? videoFrames)
    {
        // Check dynamic providers first (plugin-registered)
        if (_dynamicProviders.TryGetValue(config.Provider, out var dynamicClient))
            return dynamicClient.Client;

        return config.Provider.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAiClient(config)
                .GetChatClient(config.Model)
                .AsIChatClient(),
            "anthropic" or "claude" => CreateAnthropicClient(config)
                .AsIChatClient(config.Model),
            "anthropic-vertex" => CreateAnthropicClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey,
                    Endpoint = config.Endpoint
                        ?? throw new InvalidOperationException(
                            "Endpoint must be set for provider 'anthropic-vertex'. " +
                            "Set OpenClaw:Llm:Endpoint or Models:Profiles:<id>:BaseUrl."),
                    Model = config.Model
                })
                .AsIChatClient(config.Model),
            "gemini" or "google" => CreateGeminiClient(config),
            "ollama" => new OllamaChatClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey,
                    Endpoint = OllamaEndpointNormalizer.NormalizeBaseUrl(config.Endpoint),
                    Model = config.Model
                }),
            "embedded" => new EmbeddedLocalChatClient(
                config,
                localInference ?? new LocalInferenceConfig(),
                multimodal,
                videoFrames: videoFrames),
            "azure-openai" => CreateAzureOpenAiClient(config)
                .GetChatClient(config.Model)
                .AsIChatClient(),
            "openai-compatible" or "aperture" or "groq" or "together" or "lmstudio" =>
                CreateOpenAiCompatibleClient(new LlmProviderConfig
                {
                    Provider = config.Provider,
                    ApiKey = config.ApiKey,
                    AuthMode = config.AuthMode,
                    SendRequestMetadata = config.SendRequestMetadata,
                    Model = config.Model,
                    Endpoint = config.Endpoint
                        ?? throw new InvalidOperationException(
                            $"Endpoint must be set for provider '{config.Provider}'. " +
                            "Set OpenClaw:Llm:Endpoint or MODEL_PROVIDER_ENDPOINT.")
                }),
            "amazon-bedrock" => CreateAnthropicClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey,
                    Endpoint = config.Endpoint
                        ?? throw new InvalidOperationException(
                            "Endpoint must be set for provider 'amazon-bedrock'. " +
                            "Use a Bedrock-compatible proxy endpoint or register a dynamic provider."),
                    Model = config.Model
                })
                .AsIChatClient(config.Model),
            _ => throw new InvalidOperationException(
                $"Unsupported LLM provider: {config.Provider}. " +
                "Supported: openai, anthropic, claude, anthropic-vertex, gemini, google, ollama, embedded, azure-openai, openai-compatible, aperture, groq, together, lmstudio, amazon-bedrock")
        };
    }

    /// <summary>
    /// Creates an embedding generator using the same provider/apiKey/endpoint as chat.
    /// Returns null if embeddingModel is null or whitespace.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(
        LlmProviderConfig config, string? embeddingModel)
    {
        if (string.IsNullOrWhiteSpace(embeddingModel))
            return null;

        return config.Provider.ToLowerInvariant() switch
        {
            "openai" or "azure-openai" => CreateOpenAiEmbeddingClient(config, embeddingModel!),
            "ollama" => new OllamaEmbeddingGenerator(new LlmProviderConfig
            {
                ApiKey = config.ApiKey,
                Endpoint = OllamaEndpointNormalizer.NormalizeBaseUrl(config.Endpoint),
                Model = config.Model
            }, embeddingModel!),
            "gemini" or "google" => CreateGeminiEmbeddingClient(config, embeddingModel!),
            "openai-compatible" or "aperture" or "groq" or "together" or "lmstudio" =>
                CreateOpenAiEmbeddingClient(new LlmProviderConfig
                {
                    Provider = config.Provider,
                    ApiKey = config.ApiKey,
                    AuthMode = config.AuthMode,
                    Model = config.Model,
                    Endpoint = config.Endpoint
                }, embeddingModel!),
            "anthropic-vertex" or "amazon-bedrock" => null,
            _ => null
        };
    }

    private static IChatClient CreateGeminiClient(LlmProviderConfig llm)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("MODEL_PROVIDER_KEY must be set for the Gemini provider.");

        var options = new GeminiClientOptions
        {
            ApiKey = llm.ApiKey
        };

        if (!string.IsNullOrWhiteSpace(llm.Endpoint))
            options.Endpoint = new Uri(llm.Endpoint, UriKind.Absolute);

        return new GeminiChatClient(options);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateGeminiEmbeddingClient(
        LlmProviderConfig llm,
        string embeddingModel)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("MODEL_PROVIDER_KEY must be set for the Gemini provider.");

        var options = new GeminiClientOptions
        {
            ApiKey = llm.ApiKey
        };

        if (!string.IsNullOrWhiteSpace(llm.Endpoint))
            options.Endpoint = new Uri(llm.Endpoint, UriKind.Absolute);

        return new GeminiEmbeddingGenerator(options);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiEmbeddingClient(
        LlmProviderConfig config, string embeddingModel)
    {
        if (IsTailnetIdentityAuth(config.AuthMode))
            throw new InvalidOperationException("Embeddings do not support AuthMode=tailnet-identity yet. Configure bearer auth for embeddings or disable embeddings for this profile.");

        var transport = CreateTransportOptions(config.Endpoint);
        var client = new OpenAI.OpenAIClient(
            new ApiKeyCredential(config.ApiKey ?? throw new InvalidOperationException("API key required for embeddings.")),
            CreateOpenAiClientOptions(transport));
        return client.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
    }

    private static OpenAI.OpenAIClient CreateOpenAiClient(LlmProviderConfig llm)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("MODEL_PROVIDER_KEY must be set for the OpenAI provider.");

        var transport = CreateTransportOptions(llm.Endpoint);
        return new OpenAI.OpenAIClient(new ApiKeyCredential(llm.ApiKey), CreateOpenAiClientOptions(transport));
    }

    private static IChatClient CreateOpenAiCompatibleClient(LlmProviderConfig llm)
    {
        var policy = RequiresOpenClawRequestPolicy(llm)
            ? new OpenClawProviderRequestPolicy(IsTailnetIdentityAuth(llm.AuthMode))
            : null;

        var apiKey = llm.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!IsTailnetIdentityAuth(llm.AuthMode))
                throw new InvalidOperationException(BuildMissingOpenAiCompatibleKeyMessage(llm.Provider));

            apiKey = "openclaw-tailnet-identity";
        }

        var transport = CreateTransportOptions(llm.Endpoint);
        var options = CreateOpenAiClientOptions(transport, policy);
        var client = new OpenAI.OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(llm.Model)
            .AsIChatClient();

        return policy is null
            ? client
            : new OpenClawProviderRequestMetadataChatClient(client);
    }

    private static OpenAI.OpenAIClient CreateAzureOpenAiClient(LlmProviderConfig llm)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("MODEL_PROVIDER_KEY must be set for the Azure OpenAI provider.");
        if (string.IsNullOrWhiteSpace(llm.Endpoint))
            throw new InvalidOperationException("MODEL_PROVIDER_ENDPOINT must be set for the Azure OpenAI provider (e.g. https://myresource.openai.azure.com/).");

        var transport = CreateTransportOptions(llm.Endpoint);
        return new OpenAI.OpenAIClient(new ApiKeyCredential(llm.ApiKey), CreateOpenAiClientOptions(transport));
    }

    private static IAnthropicClient CreateAnthropicClient(LlmProviderConfig llm)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("MODEL_PROVIDER_KEY must be set for the Anthropic provider.");

        if (string.IsNullOrWhiteSpace(llm.Endpoint))
        {
            return new AnthropicClient
            {
                ApiKey = llm.ApiKey
            };
        }

        return new AnthropicClient
        {
            ApiKey = llm.ApiKey,
            BaseUrl = llm.Endpoint
        };
    }

    internal static LlmClientTransportOptions CreateTransportOptions(string? endpoint)
        => new(
            string.IsNullOrWhiteSpace(endpoint)
                ? null
                : new Uri(endpoint, UriKind.Absolute),
            HiddenRetryCount: 0);

    private static OpenAI.OpenAIClientOptions CreateOpenAiClientOptions(
        LlmClientTransportOptions transport,
        PipelinePolicy? requestPolicy = null)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(transport.HiddenRetryCount)
        };

        if (transport.Endpoint is not null)
            options.Endpoint = transport.Endpoint;
        if (requestPolicy is not null)
            options.AddPolicy(requestPolicy, PipelinePosition.BeforeTransport);

        return options;
    }

    private static bool RequiresOpenClawRequestPolicy(LlmProviderConfig config)
        => config.SendRequestMetadata || IsTailnetIdentityAuth(config.AuthMode);

    private static bool IsTailnetIdentityAuth(string? authMode)
        => string.Equals(authMode?.Trim(), "tailnet-identity", StringComparison.OrdinalIgnoreCase);

    private static string BuildMissingOpenAiCompatibleKeyMessage(string provider)
        => string.Equals(provider, "aperture", StringComparison.OrdinalIgnoreCase)
            ? "OPENCLAW_APERTURE_TOKEN or OpenClaw:Llm:ApiKey must be set for provider 'aperture' when AuthMode is 'bearer'."
            : $"MODEL_PROVIDER_KEY must be set for provider '{provider}'.";
}

internal sealed class OpenClawProviderRequestPolicy : PipelinePolicy
{
    internal const string MetadataHeadersPropertyName = "openclaw.request_metadata_headers";
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> RequestHeaders = new();
    private static readonly HashSet<string> BlockedMetadataHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
        "WWW-Authenticate"
    };
    private readonly bool _removeAuthorization;

    public OpenClawProviderRequestPolicy(bool removeAuthorization)
        => _removeAuthorization = removeAuthorization;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    internal static IDisposable PushHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var previous = RequestHeaders.Value;
        RequestHeaders.Value = headers;
        return new HeaderScope(previous);
    }

    private void Apply(PipelineMessage message)
    {
        if (_removeAuthorization)
            message.Request.Headers.Remove("Authorization");

        var headers = RequestHeaders.Value;
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            var headerName = name.Trim();
            var headerValue = value.Trim();
            if (!string.IsNullOrWhiteSpace(headerName) &&
                !string.IsNullOrWhiteSpace(headerValue) &&
                !BlockedMetadataHeaderNames.Contains(headerName))
            {
                message.Request.Headers.Set(headerName, headerValue);
            }
        }
    }

    private sealed class HeaderScope(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        public void Dispose() => RequestHeaders.Value = previous;
    }
}

internal sealed class OpenClawProviderRequestMetadataChatClient(IChatClient inner) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (headers, effectiveOptions) = ExtractMetadataHeaders(options);
        using var _ = OpenClawProviderRequestPolicy.PushHeaders(headers);
        return await inner.GetResponseAsync(messages, effectiveOptions, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (headers, effectiveOptions) = ExtractMetadataHeaders(options);
        using var _ = OpenClawProviderRequestPolicy.PushHeaders(headers);
        await foreach (var update in inner.GetStreamingResponseAsync(messages, effectiveOptions, cancellationToken).WithCancellation(cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private static (IReadOnlyDictionary<string, string>? Headers, ChatOptions? Options) ExtractMetadataHeaders(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null ||
            !options.AdditionalProperties.TryGetValue(OpenClawProviderRequestPolicy.MetadataHeadersPropertyName, out var raw) ||
            raw is not IReadOnlyDictionary<string, string> headers)
        {
            return (null, options);
        }

        var clone = options.Clone();
        clone.AdditionalProperties?.Remove(OpenClawProviderRequestPolicy.MetadataHeadersPropertyName);
        return (headers, clone);
    }
}
