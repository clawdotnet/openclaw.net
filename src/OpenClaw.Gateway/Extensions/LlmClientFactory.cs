using System;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

public static class LlmClientFactory
{
    public static IChatClient CreateChatClient(LlmProviderConfig config)
    {
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
                            "Set OpenClaw:Llm:Endpoint or OPENCLAW_ENDPOINT.")
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
            throw new InvalidOperationException("OPENCLAW_API_KEY must be set for the OpenAI provider.");

        if (string.IsNullOrWhiteSpace(llm.Endpoint))
            return new OpenAI.OpenAIClient(llm.ApiKey);

        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri(llm.Endpoint, UriKind.Absolute)
        };

        return new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(llm.ApiKey), options);
    }

    private static OpenAI.OpenAIClient CreateAzureOpenAiClient(LlmProviderConfig llm)
    {
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
            throw new InvalidOperationException("OPENCLAW_API_KEY must be set for the Azure OpenAI provider.");
        if (string.IsNullOrWhiteSpace(llm.Endpoint))
            throw new InvalidOperationException("OPENCLAW_ENDPOINT must be set for the Azure OpenAI provider (e.g. https://myresource.openai.azure.com/).");

        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri(llm.Endpoint, UriKind.Absolute)
        };

        return new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(llm.ApiKey), options);
    }
}
