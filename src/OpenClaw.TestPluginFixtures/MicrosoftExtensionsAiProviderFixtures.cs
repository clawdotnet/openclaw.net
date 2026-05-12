using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenClaw.Providers.MicrosoftExtensionsAI;

namespace OpenClaw.TestPluginFixtures;

public sealed class DeterministicMicrosoftExtensionsAiChatClientFactory : IMicrosoftExtensionsAiChatClientFactory
{
    public IChatClient Create(MicrosoftExtensionsAiProviderFactoryContext context)
    {
        var responseText = context.Config is { ValueKind: System.Text.Json.JsonValueKind.Object } config &&
            config.TryGetProperty("responseText", out var responseTextElement) &&
            responseTextElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? responseTextElement.GetString() ?? "fixture response"
                : "fixture response";

        return new DeterministicChatClient(context.ProviderId, responseText);
    }

    private sealed class DeterministicChatClient(string providerId, string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var userText = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text ?? "";
            var model = options?.ModelId ?? "default";
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $"{responseText} provider={providerId} model={model} user={userText}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}

public sealed class NullMicrosoftExtensionsAiChatClientFactory : IMicrosoftExtensionsAiChatClientFactory
{
    public IChatClient Create(MicrosoftExtensionsAiProviderFactoryContext context) => null!;
}

public sealed class InvalidMicrosoftExtensionsAiChatClientFactory
{
}
