using System.Text.Json;

namespace OpenClaw.Providers.MicrosoftExtensionsAI;

public sealed class MicrosoftExtensionsAiProviderConfig
{
    public MicrosoftExtensionsAiProviderRegistrationConfig[] Providers { get; set; } = [];
}

public sealed class MicrosoftExtensionsAiProviderRegistrationConfig
{
    public string ProviderId { get; set; } = "";
    public string[]? Models { get; set; } = [];
    public string FactoryTypeName { get; set; } = "";
    public string? FactoryAssemblyPath { get; set; }
    public JsonElement? Config { get; set; }
}
