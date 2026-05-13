using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public static class LocalModelPresetCatalog
{
    private static readonly LocalModelPresetDefinition[] Presets =
    [
        new()
        {
            Id = "embedded-gemma-small-q4",
            Label = "Embedded Gemma Small Q4",
            Description = "OpenClaw-managed local Gemma profile for private/offline helper tasks.",
            Provider = "embedded",
            DefaultBaseUrl = "",
            PackageId = "gemma-local-small-q4",
            ModelId = "gemma-local-small-q4",
            Installable = true,
            Tags = ["local", "private", "offline", "cheap"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = false,
                SupportsVision = false,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = false,
                SupportsAudioInput = false,
                MaxContextTokens = 4096,
                MaxOutputTokens = 1024
            },
            RecommendedContextTokens = 4096,
            RecommendedOutputTokens = 1024,
            CompatibilityNotes =
            [
                "Requires a verified local GGUF model package and a llama.cpp llama-server runtime.",
                "Use a fallback profile for tool-heavy, structured-output, vision, or long-context routes."
            ],
            DoctorExpectations =
            [
                "Warn when the package is not installed or cannot be verified.",
                "Warn when routes require tool calling, structured outputs, vision, or larger context than the embedded profile advertises."
            ]
        },
        new()
        {
            Id = "ollama-general",
            Label = "Ollama General",
            Description = "Balanced local preset for everyday chat and mixed tasks.",
            Tags = ["local", "private", "generalist"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = false,
                SupportsVision = false,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = false,
                SupportsAudioInput = false,
                MaxContextTokens = 32768,
                MaxOutputTokens = 4096
            },
            RecommendedContextTokens = 32768,
            RecommendedOutputTokens = 4096,
            CompatibilityNotes =
            [
                "Use native Ollama endpoints instead of the OpenAI-compatible /v1 shim.",
                "Add a fallback profile for tool-heavy routes."
            ],
            DoctorExpectations =
            [
                "Warn when this preset is selected for routes that require tools or structured outputs.",
                "Warn when recent prompt usage routinely approaches the preset context limit."
            ]
        },
        new()
        {
            Id = "ollama-agentic",
            Label = "Ollama Agentic",
            Description = "Local-first preset for tool calling with deterministic cloud fallback.",
            Tags = ["local", "private", "agentic"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = true,
                SupportsVision = false,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = false,
                SupportsAudioInput = false,
                MaxContextTokens = 65536,
                MaxOutputTokens = 8192
            },
            RecommendedContextTokens = 65536,
            RecommendedOutputTokens = 8192,
            CompatibilityNotes =
            [
                "Best paired with a stronger fallback profile for structured outputs and long-context repairs.",
                "Recent prompt budget drift should be monitored more aggressively for this preset."
            ],
            DoctorExpectations =
            [
                "Warn when a route requires JSON schema or parallel tool calling.",
                "Warn when no fallback profile is configured for tool-heavy routes."
            ]
        },
        new()
        {
            Id = "ollama-vision",
            Label = "Ollama Vision",
            Description = "Local preset optimized for image-aware interactions with conservative tool expectations.",
            Tags = ["local", "private", "vision"],
            Capabilities = new ModelCapabilities
            {
                SupportsTools = false,
                SupportsVision = true,
                SupportsJsonSchema = false,
                SupportsStructuredOutputs = false,
                SupportsStreaming = true,
                SupportsParallelToolCalls = false,
                SupportsReasoningEffort = false,
                SupportsSystemMessages = true,
                SupportsImageInput = true,
                SupportsAudioInput = false,
                MaxContextTokens = 65536,
                MaxOutputTokens = 8192
            },
            RecommendedContextTokens = 65536,
            RecommendedOutputTokens = 8192,
            CompatibilityNotes =
            [
                "Prefer explicit fallback for structured extraction and multi-tool routes.",
                "Large inline images can exhaust prompt budget quickly."
            ],
            DoctorExpectations =
            [
                "Warn when image-heavy recent turns exceed expected context headroom.",
                "Warn when vision is enabled but the configured local model appears text-only."
            ]
        }
    ];

    public static IReadOnlyList<LocalModelPresetDefinition> List()
        => Presets
            .Concat(LocalModelPackageCatalog.List()
                .Where(package => !Presets.Any(preset => string.Equals(preset.Id, package.PresetId, StringComparison.OrdinalIgnoreCase)))
                .Select(ToPreset))
            .ToArray();

    public static bool TryGet(string? presetId, out LocalModelPresetDefinition? preset)
    {
        preset = Presets.FirstOrDefault(item => string.Equals(item.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
            return true;

        if (LocalModelPackageCatalog.TryGet(presetId, out var package) && package is not null)
        {
            preset = ToPreset(package);
            return true;
        }

        return false;
    }

    public static LocalModelPresetDefinition? GetForProfile(ModelProfile profile)
        => TryGet(profile.PresetId, out var preset) ? preset : null;

    private static LocalModelPresetDefinition ToPreset(LocalModelPackageDefinition package)
        => new()
        {
            Id = package.PresetId,
            Label = package.DisplayName,
            Description = package.Description,
            Provider = package.Provider,
            DefaultBaseUrl = "",
            PackageId = package.Id,
            ModelId = package.ModelId,
            Installable = true,
            Tags = package.Tags,
            Capabilities = package.Capabilities,
            RecommendedContextTokens = package.ContextWindow,
            RecommendedOutputTokens = package.MaxOutputTokens,
            CompatibilityNotes =
            [
                package.Runtime.Backend.Equals("litert", StringComparison.OrdinalIgnoreCase)
                    ? "Experimental: requires a verified LiteRT-LM package and an OpenClaw-compatible LiteRT adapter binary."
                    : "Requires a verified local GGUF model package and a llama.cpp llama-server runtime.",
                package.Capabilities.SupportsTools
                    ? "Tool calling requires llama-server Jinja chat-template support and OpenClaw policy approval."
                    : "Use a fallback profile for tool-heavy or structured-output routes.",
                package.Capabilities.SupportsVision
                    ? "Multimodal input requires the package projector file or OpenClaw:LocalInference:MultimodalProjectorPath."
                    : "This package is text-only."
            ],
            DoctorExpectations =
            [
                "Warn when the package is not installed or cannot be verified.",
                "Warn when routes require capabilities outside the package profile.",
                "Warn when requested context routinely exceeds local RAM guidance."
            ]
        };
}
