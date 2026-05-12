using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Providers.MicrosoftExtensionsAI;

/// <summary>
/// Context passed to a factory while creating an OpenClaw provider-backed IChatClient.
/// </summary>
public sealed class MicrosoftExtensionsAiProviderFactoryContext
{
    public required string PluginId { get; init; }
    public required string ProviderId { get; init; }
    public required IReadOnlyList<string> Models { get; init; }
    public JsonElement? Config { get; init; }
    public required ILogger Logger { get; init; }
}
