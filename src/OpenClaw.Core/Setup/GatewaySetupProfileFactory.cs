using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Core.Setup;

public static class GatewaySetupProfileFactory
{
    public static GatewayConfig CreateProfileConfig(
        string profile,
        string bindAddress,
        int port,
        string authToken,
        string workspacePath,
        string memoryPath,
        string provider,
        string model,
        string apiKey,
        string? modelPresetId = null,
        List<string>? warnings = null)
    {
        var normalizedProfile = NormalizeProfile(profile);
        var normalizedProvider = provider.Trim();
        var config = new GatewayConfig
        {
            BindAddress = bindAddress,
            Port = port,
            AuthToken = authToken,
            Llm = new LlmProviderConfig
            {
                Provider = normalizedProvider,
                Model = model,
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey
            },
            Memory = new MemoryConfig
            {
                Provider = "file",
                StoragePath = memoryPath,
                Retention = new MemoryRetentionConfig
                {
                    ArchivePath = Path.Combine(memoryPath, "archive")
                }
            },
            Tooling = new ToolingConfig
            {
                WorkspaceRoot = workspacePath,
                WorkspaceOnly = true,
                AllowShell = normalizedProfile == "local",
                EnableBrowserTool = false,
                AllowedReadRoots = [workspacePath],
                AllowedWriteRoots = [workspacePath],
                RequireToolApproval = normalizedProfile == "public"
            },
            Security = new SecurityConfig
            {
                AllowQueryStringToken = false,
                TrustForwardedHeaders = normalizedProfile == "public",
                RequireRequesterMatchForHttpToolApproval = normalizedProfile == "public"
            }
        };

        ConfigureModelProfiles(config, normalizedProvider, model, modelPresetId, warnings);

        if (normalizedProfile == "public")
        {
            config.Plugins.Enabled = false;
            warnings?.Add("Public profile disables third-party bridge plugins by default. Re-enable them only after you have a proxy, TLS, and explicit public-bind trust settings in place.");
        }

        if (normalizedProfile == "public" &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            warnings?.Add("Public profile is using a direct API key value in the config file. Prefer env:... references or OS-backed secret storage.");
        }

        return config;
    }

    public static string NormalizeProfile(string profile)
    {
        var normalized = profile.Trim().ToLowerInvariant();
        if (normalized is not ("local" or "public"))
            throw new ArgumentException("Invalid value for --profile (expected: local|public).");
        return normalized;
    }

    private static void ConfigureModelProfiles(
        GatewayConfig config,
        string provider,
        string model,
        string? modelPresetId,
        List<string>? warnings)
    {
        if (!provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(modelPresetId))
                warnings?.Add($"Ignoring model preset '{modelPresetId}' because native local presets currently apply only to the Ollama provider.");
            return;
        }

        config.Llm.Endpoint = OllamaEndpointNormalizer.DefaultBaseUrl;
        config.Models.DefaultProfile = "local-primary";

        LocalModelPresetDefinition? preset = null;
        if (!string.IsNullOrWhiteSpace(modelPresetId) &&
            !LocalModelPresetCatalog.TryGet(modelPresetId, out preset))
        {
            warnings?.Add($"Unknown model preset '{modelPresetId}'. Falling back to inferred local capabilities.");
        }

        var capabilities = preset?.Capabilities ?? new ModelCapabilities
        {
            SupportsStreaming = true,
            SupportsSystemMessages = true,
            MaxContextTokens = 32768,
            MaxOutputTokens = 4096
        };

        config.Models.Profiles =
        [
            new ModelProfileConfig
            {
                Id = "local-primary",
                PresetId = preset?.Id,
                Provider = "ollama",
                Model = model,
                BaseUrl = OllamaEndpointNormalizer.DefaultBaseUrl,
                Tags = preset?.Tags?.ToArray() ?? ["local", "private"],
                Capabilities = CloneCapabilities(capabilities)
            }
        ];
    }

    private static ModelCapabilities CloneCapabilities(ModelCapabilities source)
        => new()
        {
            SupportsTools = source.SupportsTools,
            SupportsVision = source.SupportsVision,
            SupportsJsonSchema = source.SupportsJsonSchema,
            SupportsStructuredOutputs = source.SupportsStructuredOutputs,
            SupportsStreaming = source.SupportsStreaming,
            SupportsParallelToolCalls = source.SupportsParallelToolCalls,
            SupportsReasoningEffort = source.SupportsReasoningEffort,
            SupportsSystemMessages = source.SupportsSystemMessages,
            SupportsImageInput = source.SupportsImageInput,
            SupportsAudioInput = source.SupportsAudioInput,
            SupportsPromptCaching = source.SupportsPromptCaching,
            SupportsExplicitCacheRetention = source.SupportsExplicitCacheRetention,
            ReportsCacheReadTokens = source.ReportsCacheReadTokens,
            ReportsCacheWriteTokens = source.ReportsCacheWriteTokens,
            MaxContextTokens = source.MaxContextTokens,
            MaxOutputTokens = source.MaxOutputTokens
        };
}
