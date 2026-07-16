using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public static class DeepSeekProviderDefaults
{
    public const string ProviderId = "deepseek";
    public const string OpenAiBaseUrl = "https://api.deepseek.com";
    public const string DefaultApiKeyRef = "env:DEEPSEEK_API_KEY";
    public const string DefaultModel = "deepseek-v4-flash";
    public const string ProModel = "deepseek-v4-pro";

    public static ModelCapabilities BuildCapabilities()
        => new()
        {
            SupportsTools = true,
            SupportsVision = false,
            SupportsJsonSchema = false,
            SupportsStructuredOutputs = true,
            SupportsStreaming = true,
            SupportsParallelToolCalls = true,
            SupportsReasoningEffort = true,
            SupportsSystemMessages = true,
            SupportsImageInput = false,
            SupportsVideoInput = false,
            SupportsAudioInput = false,
            SupportsPromptCaching = false,
            SupportsExplicitCacheRetention = false,
            ReportsCacheReadTokens = false,
            ReportsCacheWriteTokens = false,
            MaxContextTokens = 64000,
            MaxOutputTokens = 8192
        };
}
