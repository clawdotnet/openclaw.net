using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenClaw.Providers.MicrosoftExtensionsAI;

namespace OpenClaw.MicrosoftExtensionsAIProvider;

public sealed class SampleDeterministicChatClientFactory : IMicrosoftExtensionsAiChatClientFactory
{
    public IChatClient Create(MicrosoftExtensionsAiProviderFactoryContext context)
        => new SampleDeterministicChatClient(context.ProviderId);

    private sealed class SampleDeterministicChatClient(string providerId) : IChatClient
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
                $"sample provider={providerId} model={model} user={userText}")));
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
