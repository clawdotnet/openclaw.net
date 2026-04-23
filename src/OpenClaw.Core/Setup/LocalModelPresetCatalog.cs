using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public static class LocalModelPresetCatalog
{
    private static readonly LocalModelPresetDefinition[] Presets =
    [
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
        => Presets;

    public static bool TryGet(string? presetId, out LocalModelPresetDefinition? preset)
    {
        preset = Presets.FirstOrDefault(item => string.Equals(item.Id, presetId, StringComparison.OrdinalIgnoreCase));
        return preset is not null;
    }

    public static LocalModelPresetDefinition? GetForProfile(ModelProfile profile)
        => TryGet(profile.PresetId, out var preset) ? preset : null;
}
