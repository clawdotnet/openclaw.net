using System;
using System.Collections.Concurrent;
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

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
            "ollama" => CreateOpenAiClient(new LlmProviderConfig
                {
                    ApiKey = config.ApiKey ?? "ollama",
                    Endpoint = config.Endpoint ?? "http://localhost:11434/v1",
                    Model = config.Model
                })
                .GetChatClient(config.Model)
                .AsIChatClient(),
            "azure-openai" => CreateAzureOpenAiClient(config)
                .GetChatClient(config.Model)
                .AsIChatClient(),
            "openai-compatible" or "anthropic" or "google" or "groq" or "together" or "lmstudio" =>
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
            _ => throw new InvalidOperationException(
                $"Unsupported LLM provider: {config.Provider}. " +
                "Supported: openai, ollama, azure-openai, openai-compatible, anthropic, google, groq, together, lmstudio")
        };
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
