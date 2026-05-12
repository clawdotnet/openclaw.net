using Microsoft.Extensions.AI;

namespace OpenClaw.Providers.MicrosoftExtensionsAI;

/// <summary>
/// Creates the Microsoft.Extensions.AI chat client used by one OpenClaw provider registration.
/// </summary>
public interface IMicrosoftExtensionsAiChatClientFactory
{
    IChatClient Create(MicrosoftExtensionsAiProviderFactoryContext context);
}
