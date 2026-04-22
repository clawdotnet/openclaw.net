using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.Validation;

public static class ModelDoctorEvaluator
{
    public static ModelSelectionDoctorResponse Build(GatewayConfig config, IModelProfileRegistry? registry = null)
    {
        if (registry is not null)
            return BuildFromRegistry(registry);

        var statuses = BuildStatusesFromConfig(config);
        var warnings = new List<string>();
        var errors = new List<string>();
        var defaultProfileId = ResolveDefaultProfileId(config, statuses);

        if (statuses.Count == 0)
            errors.Add("No model profiles are registered.");
        if (string.IsNullOrWhiteSpace(defaultProfileId))
            errors.Add("No default model profile is configured.");

        foreach (var status in statuses)
        {
            if (status.ValidationIssues.Length > 0)
                warnings.Add($"Profile '{status.Id}' has validation issues: {string.Join("; ", status.ValidationIssues)}");
        }

        return new ModelSelectionDoctorResponse
        {
            DefaultProfileId = defaultProfileId,
            Errors = errors,
            Warnings = warnings,
            Profiles = statuses
        };
    }

    private static ModelSelectionDoctorResponse BuildFromRegistry(IModelProfileRegistry registry)
    {
        var statuses = registry.ListStatuses();
        var warnings = new List<string>();
        var errors = new List<string>();

        if (statuses.Count == 0)
            errors.Add("No model profiles are registered.");
        if (string.IsNullOrWhiteSpace(registry.DefaultProfileId))
            errors.Add("No default model profile is configured.");

        foreach (var status in statuses)
        {
            if (status.ValidationIssues.Length > 0)
                warnings.Add($"Profile '{status.Id}' has validation issues: {string.Join("; ", status.ValidationIssues)}");
        }

        return new ModelSelectionDoctorResponse
        {
            DefaultProfileId = registry.DefaultProfileId,
            Errors = errors,
            Warnings = warnings,
            Profiles = statuses
        };
    }

    private static IReadOnlyList<ModelProfileStatus> BuildStatusesFromConfig(GatewayConfig config)
    {
        var profiles = config.Models.Profiles.Count > 0
            ? config.Models.Profiles
            : [CreateImplicitProfile(config)];
        var defaultProfileId = ResolveDefaultProfileId(config, profiles);
        var statuses = new List<ModelProfileStatus>(profiles.Count);

        foreach (var profile in profiles)
        {
            var normalizedId = Normalize(profile.Id) ?? "default";
            var providerId = Normalize(profile.Provider) ?? Normalize(config.Llm.Provider) ?? "unknown";
            var modelId = Normalize(profile.Model) ?? Normalize(config.Llm.Model) ?? "unknown";
            var validationIssues = ValidateProfile(config, profile, providerId).ToArray();
            statuses.Add(new ModelProfileStatus
            {
                Id = normalizedId,
                ProviderId = providerId,
                ModelId = modelId,
                IsDefault = string.Equals(normalizedId, defaultProfileId, StringComparison.OrdinalIgnoreCase),
                IsImplicit = config.Models.Profiles.Count == 0 && string.Equals(normalizedId, "default", StringComparison.OrdinalIgnoreCase),
                IsAvailable = validationIssues.Length == 0,
                Tags = NormalizeDistinct(profile.Tags),
                Capabilities = profile.Capabilities ?? GuessCapabilities(providerId),
                PromptCaching = MergePromptCaching(config.Llm.PromptCaching, profile.PromptCaching),
                ValidationIssues = validationIssues,
                FallbackProfileIds = NormalizeDistinct(profile.FallbackProfileIds),
                FallbackModels = NormalizeDistinct(profile.FallbackModels)
            });
        }

        return statuses
            .OrderByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveDefaultProfileId(GatewayConfig config, IReadOnlyList<ModelProfileConfig> profiles)
    {
        var configured = Normalize(config.Models.DefaultProfile);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (profiles.Count == 0)
            return null;

        return Normalize(profiles[0].Id) ?? "default";
    }

    private static string? ResolveDefaultProfileId(GatewayConfig config, IReadOnlyList<ModelProfileStatus> statuses)
    {
        var configured = Normalize(config.Models.DefaultProfile);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return statuses.FirstOrDefault(static item => item.IsDefault)?.Id
            ?? statuses.FirstOrDefault()?.Id;
    }

    private static ModelProfileConfig CreateImplicitProfile(GatewayConfig config)
        => new()
        {
            Id = "default",
            Provider = config.Llm.Provider,
            Model = config.Llm.Model,
            BaseUrl = config.Llm.Endpoint,
            ApiKey = config.Llm.ApiKey,
            FallbackModels = config.Llm.FallbackModels,
            Capabilities = GuessCapabilities(config.Llm.Provider),
            PromptCaching = ClonePromptCaching(config.Llm.PromptCaching)
        };

    private static IEnumerable<string> ValidateProfile(GatewayConfig config, ModelProfileConfig profile, string providerId)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            yield return "Profile id is required.";
        if (string.IsNullOrWhiteSpace(providerId))
            yield return "Provider is required.";
        if (string.IsNullOrWhiteSpace(profile.Model) && string.IsNullOrWhiteSpace(config.Llm.Model))
            yield return "Model is required.";

        if (RequiresEndpoint(providerId) &&
            string.IsNullOrWhiteSpace(ResolveSecretValue(profile.BaseUrl)) &&
            string.IsNullOrWhiteSpace(ResolveSecretValue(config.Llm.Endpoint)))
        {
            yield return "BaseUrl is required for this provider unless inherited from OpenClaw:Llm:Endpoint.";
        }

        if (RequiresCredentials(providerId) &&
            string.IsNullOrWhiteSpace(ResolveSecretValue(profile.ApiKey)) &&
            string.IsNullOrWhiteSpace(ResolveSecretValue(config.Llm.ApiKey)))
        {
            yield return "ApiKey is required for this provider unless inherited from OpenClaw:Llm:ApiKey.";
        }
    }

    private static bool RequiresEndpoint(string providerId)
        => providerId is "openai-compatible" or "groq" or "together" or "lmstudio" or "anthropic-vertex" or "amazon-bedrock" or "azure-openai";

    private static bool RequiresCredentials(string providerId)
        => providerId is not "ollama" and not "lmstudio";

    private static ModelCapabilities GuessCapabilities(string providerId)
    {
        var provider = Normalize(providerId) ?? string.Empty;
        var supportsTools = provider is "openai" or "openai-compatible" or "azure-openai" or "groq" or "together" or "lmstudio" or "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock" or "gemini" or "google";
        var supportsVision = provider is "openai" or "openai-compatible" or "azure-openai" or "gemini" or "google" or "ollama" or "amazon-bedrock";
        var supportsPromptCaching = provider is "openai" or "azure-openai" or "anthropic" or "claude" or "anthropic-vertex" or "gemini" or "google";
        var supportsExplicitCacheRetention = provider is "anthropic" or "claude" or "anthropic-vertex";
        return new ModelCapabilities
        {
            SupportsTools = supportsTools,
            SupportsVision = supportsVision,
            SupportsJsonSchema = provider is "openai" or "openai-compatible" or "azure-openai",
            SupportsStructuredOutputs = provider is "openai" or "openai-compatible" or "azure-openai",
            SupportsStreaming = true,
            SupportsParallelToolCalls = provider is "openai" or "openai-compatible" or "azure-openai",
            SupportsReasoningEffort = provider is "openai" or "openai-compatible" or "azure-openai",
            SupportsSystemMessages = true,
            SupportsImageInput = supportsVision,
            SupportsAudioInput = provider is "openai" or "openai-compatible" or "azure-openai",
            SupportsPromptCaching = supportsPromptCaching,
            SupportsExplicitCacheRetention = supportsExplicitCacheRetention,
            ReportsCacheReadTokens = supportsPromptCaching,
            ReportsCacheWriteTokens = provider is "anthropic" or "claude" or "anthropic-vertex"
        };
    }

    private static PromptCachingConfig MergePromptCaching(PromptCachingConfig root, PromptCachingConfig? profile)
        => new()
        {
            Enabled = profile?.Enabled ?? root.Enabled,
            Retention = profile?.Retention ?? root.Retention,
            Dialect = profile?.Dialect ?? root.Dialect,
            KeepWarmEnabled = profile?.KeepWarmEnabled ?? root.KeepWarmEnabled,
            KeepWarmIntervalMinutes = profile?.KeepWarmIntervalMinutes ?? root.KeepWarmIntervalMinutes,
            TraceEnabled = profile?.TraceEnabled ?? root.TraceEnabled,
            TraceFilePath = profile?.TraceFilePath ?? root.TraceFilePath
        };

    private static PromptCachingConfig ClonePromptCaching(PromptCachingConfig caching)
        => MergePromptCaching(caching, null);

    private static string[] NormalizeDistinct(IEnumerable<string?> values)
        => values
            .Select(Normalize)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveSecretValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : SecretResolver.Resolve(value) ?? value;
}
