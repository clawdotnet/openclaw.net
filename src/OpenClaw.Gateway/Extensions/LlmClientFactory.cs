using System;
using System.Collections.Concurrent;
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
            "azure-openai" => CreateAzureOpenAiClient(config)
                .GetChatClient(config.Model)
                .AsIChatClient(),
            "openai-compatible" or "groq" or "together" or "lmstudio" =>
                CreateOpenAiClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey,
                    Model = config.Model,
                    Endpoint = config.Endpoint
                        ?? throw new InvalidOperationException(
                            $"Endpoint must be set for provider '{config.Provider}'. " +
                            "Set OpenClaw:Llm:Endpoint or MODEL_PROVIDER_ENDPOINT.")
                })
                .GetChatClient(config.Model)
                .AsIChatClient(),
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
                "Supported: openai, anthropic, claude, anthropic-vertex, gemini, google, ollama, azure-openai, openai-compatible, groq, together, lmstudio, amazon-bedrock")
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
            "openai-compatible" or "groq" or "together" or "lmstudio" =>
                CreateOpenAiEmbeddingClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey,
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

    private static OpenAI.OpenAIClientOptions CreateOpenAiClientOptions(LlmClientTransportOptions transport)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(transport.HiddenRetryCount)
        };

        if (transport.Endpoint is not null)
            options.Endpoint = transport.Endpoint;

        return options;
    }
}
