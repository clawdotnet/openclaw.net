using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Setup;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Models;

internal sealed class ConfiguredModelProfileRegistry : IModelProfileRegistry
{
    internal sealed class Registration
    {
        public required ModelProfile Profile { get; init; }
        public required LlmProviderConfig ProviderConfig { get; init; }
        public required string[] ValidationIssues { get; init; }
        public IChatClient? Client { get; init; }
        public bool IsDefault { get; init; }
    }

    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConfiguredModelProfileRegistry> _logger;
    private readonly LlmProviderRegistry? _providerRegistry;

    public ConfiguredModelProfileRegistry(GatewayConfig config, ILogger<ConfiguredModelProfileRegistry> logger)
        : this(config, logger, null)
    {
    }

    public ConfiguredModelProfileRegistry(
        GatewayConfig config,
        ILogger<ConfiguredModelProfileRegistry> logger,
        LlmProviderRegistry? providerRegistry)
    {
        _logger = logger;
        _providerRegistry = providerRegistry;
        DefaultProfileId = BuildRegistrations(config);
    }

    public string? DefaultProfileId { get; }

    public bool TryGet(string profileId, out ModelProfile? profile)
    {
        if (_registrations.TryGetValue(profileId, out var registration))
        {
            profile = registration.Profile;
            return true;
        }

        profile = null;
        return false;
    }

    internal bool TryGetRegistration(string profileId, out Registration? registration)
        => _registrations.TryGetValue(profileId, out registration);

    public IReadOnlyList<ModelProfileStatus> ListStatuses()
        => _registrations.Values
            .OrderByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.Profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static item => new ModelProfileStatus
            {
                Id = item.Profile.Id,
                ProviderId = item.Profile.ProviderId,
                ModelId = item.Profile.ModelId,
                IsDefault = item.IsDefault,
                IsImplicit = item.Profile.IsImplicit,
                IsAvailable = item.Client is not null && item.ValidationIssues.Length == 0,
                Tags = item.Profile.Tags,
                Capabilities = item.Profile.Capabilities,
                PromptCaching = item.Profile.PromptCaching,
                ValidationIssues = item.ValidationIssues,
                FallbackProfileIds = item.Profile.FallbackProfileIds,
                FallbackModels = item.Profile.FallbackModels,
                PresetId = item.Profile.PresetId,
                CompatibilityNotes = BuildCompatibilityNotes(item.Profile),
                UsesCompatibilityTransport = ProfileUsesCompatibilityTransport(item.Profile)
            })
            .ToArray();

    private string BuildRegistrations(GatewayConfig config)
    {
        var defaultProfileId = Normalize(config.Models.DefaultProfile);
        var configs = config.Models.Profiles.Count > 0
            ? config.Models.Profiles
            : [CreateImplicitConfig(config)];

        var defaultId = defaultProfileId;
        foreach (var profileConfig in configs)
        {
            var profile = ToProfile(config, profileConfig);
            var issues = ValidateProfile(profile, config).ToArray();
            var providerConfig = BuildProviderConfig(config, profile);
            IChatClient? client = null;
            if (issues.Length == 0)
            {
                if (!TryResolveRegisteredClient(profile, out client))
                {
                    try
                    {
                        client = LlmClientFactory.CreateChatClient(providerConfig);
                    }
                    catch (Exception ex)
                    {
                        issues = [.. issues, ex.Message];
                        _logger.LogWarning(ex, "Failed to initialize model profile {ProfileId}", profile.Id);
                    }
                }
            }

            var isDefault =
                string.Equals(profile.Id, defaultProfileId, StringComparison.OrdinalIgnoreCase) ||
                (defaultProfileId is null && profile.IsImplicit);
            _registrations[profile.Id] = new Registration
            {
                Profile = profile,
                ProviderConfig = providerConfig,
                ValidationIssues = issues,
                Client = client,
                IsDefault = isDefault
            };

            if (defaultId is null && profile.IsImplicit)
                defaultId = profile.Id;
        }

        if (defaultId is null || !_registrations.ContainsKey(defaultId))
        {
            defaultId = _registrations.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (defaultId is not null && _registrations.TryGetValue(defaultId, out var registration))
            {
                _registrations[defaultId] = new Registration
                {
                    Profile = registration.Profile,
                    ProviderConfig = registration.ProviderConfig,
                    ValidationIssues = registration.ValidationIssues,
                    Client = registration.Client,
                    IsDefault = true
                };
            }
        }

        return defaultId ?? "default";
    }

    private static ModelProfileConfig CreateImplicitConfig(GatewayConfig config)
        => new()
        {
            Id = "default",
            PresetId = null,
            Provider = config.Llm.Provider,
            Model = config.Llm.Model,
            BaseUrl = config.Llm.Endpoint,
            ApiKey = config.Llm.ApiKey,
            FallbackModels = config.Llm.FallbackModels,
            Capabilities = GuessCapabilities(config.Llm.Provider),
            PromptCaching = ClonePromptCaching(config.Llm.PromptCaching)
        };

    private static ModelCapabilities GuessCapabilities(string providerId)
    {
        var provider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        var supportsTools = provider is "openai" or "openai-compatible" or "azure-openai" or "groq" or "together" or "lmstudio" or "anthropic" or "claude" or "anthropic-vertex" or "amazon-bedrock" or "gemini" or "google";
        var supportsVision = provider is "openai" or "openai-compatible" or "azure-openai" or "gemini" or "google" or "ollama" or "amazon-bedrock";
        var supportsPromptCaching = provider is "openai" or "azure-openai" or "anthropic" or "claude" or "anthropic-vertex" or "gemini" or "google";
        var supportsExplicitCacheRetention = provider is "anthropic" or "claude" or "anthropic-vertex";
        var reportsCacheReadTokens = supportsPromptCaching;
        var reportsCacheWriteTokens = provider is "anthropic" or "claude" or "anthropic-vertex";
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
            ReportsCacheReadTokens = reportsCacheReadTokens,
            ReportsCacheWriteTokens = reportsCacheWriteTokens
        };
    }

    private static ModelProfile ToProfile(GatewayConfig config, ModelProfileConfig model)
        => new()
        {
            Id = Normalize(model.Id) ?? "default",
            PresetId = Normalize(model.PresetId),
            ProviderId = Normalize(model.Provider) ?? config.Llm.Provider,
            ModelId = Normalize(model.Model) ?? config.Llm.Model,
            BaseUrl = ResolveSecretValue(model.BaseUrl),
            ApiKey = ResolveSecretValue(model.ApiKey),
            Tags = MergeTags(model),
            FallbackProfileIds = NormalizeDistinct(model.FallbackProfileIds),
            FallbackModels = NormalizeDistinct(model.FallbackModels),
            Capabilities = ResolveCapabilities(config, model),
            PromptCaching = MergePromptCaching(config.Llm.PromptCaching, model.PromptCaching),
            IsImplicit = string.Equals(model.Id, "default", StringComparison.OrdinalIgnoreCase)
                && config.Models.Profiles.Count == 0
        };

    private static IEnumerable<string> ValidateProfile(ModelProfile profile, GatewayConfig config)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            yield return "Profile id is required.";
        if (string.IsNullOrWhiteSpace(profile.ProviderId))
            yield return "Provider is required.";
        if (string.IsNullOrWhiteSpace(profile.ModelId))
            yield return "Model is required.";
        if ((profile.ProviderId.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("groq", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("together", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("anthropic-vertex", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("azure-openai", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(profile.BaseUrl) &&
            string.IsNullOrWhiteSpace(config.Llm.Endpoint))
        {
            yield return "BaseUrl is required for OpenAI-compatible, Anthropic Vertex, Amazon Bedrock, and Azure OpenAI profiles unless inherited from OpenClaw:Llm:Endpoint.";
        }

        if ((profile.ProviderId.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("groq", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("together", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("azure-openai", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("anthropic-vertex", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("gemini", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("google", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(profile.ApiKey) &&
            string.IsNullOrWhiteSpace(config.Llm.ApiKey))
        {
            yield return "ApiKey is required for remote provider profiles unless inherited from OpenClaw:Llm:ApiKey.";
        }
    }

    internal static LlmProviderConfig BuildProviderConfig(GatewayConfig config, ModelProfile profile)
        => new()
        {
            Provider = profile.ProviderId,
            Model = profile.ModelId,
            ApiKey = profile.ApiKey ?? config.Llm.ApiKey,
            Endpoint = ResolveEndpoint(config, profile),
            FallbackModels = profile.FallbackModels,
            MaxTokens = profile.Capabilities.MaxOutputTokens > 0 ? profile.Capabilities.MaxOutputTokens : config.Llm.MaxTokens,
            Temperature = config.Llm.Temperature,
            TimeoutSeconds = config.Llm.TimeoutSeconds,
            RetryCount = config.Llm.RetryCount,
            CircuitBreakerThreshold = config.Llm.CircuitBreakerThreshold,
            CircuitBreakerCooldownSeconds = config.Llm.CircuitBreakerCooldownSeconds,
            PromptCaching = ClonePromptCaching(profile.PromptCaching)
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveSecretValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var resolved = SecretResolver.Resolve(value);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }

    private static string[] MergeTags(ModelProfileConfig model)
    {
        var configured = NormalizeDistinct(model.Tags);
        if (!LocalModelPresetCatalog.TryGet(model.PresetId, out var preset) || preset is null)
            return configured;

        return configured
            .Concat(preset.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ModelCapabilities ResolveCapabilities(GatewayConfig config, ModelProfileConfig model)
    {
        if (model.Capabilities is not null)
            return model.Capabilities;

        if (LocalModelPresetCatalog.TryGet(model.PresetId, out var preset) && preset is not null)
        {
            return new ModelCapabilities
            {
                SupportsTools = preset.Capabilities.SupportsTools,
                SupportsVision = preset.Capabilities.SupportsVision,
                SupportsJsonSchema = preset.Capabilities.SupportsJsonSchema,
                SupportsStructuredOutputs = preset.Capabilities.SupportsStructuredOutputs,
                SupportsStreaming = preset.Capabilities.SupportsStreaming,
                SupportsParallelToolCalls = preset.Capabilities.SupportsParallelToolCalls,
                SupportsReasoningEffort = preset.Capabilities.SupportsReasoningEffort,
                SupportsSystemMessages = preset.Capabilities.SupportsSystemMessages,
                SupportsImageInput = preset.Capabilities.SupportsImageInput,
                SupportsAudioInput = preset.Capabilities.SupportsAudioInput,
                SupportsPromptCaching = preset.Capabilities.SupportsPromptCaching,
                SupportsExplicitCacheRetention = preset.Capabilities.SupportsExplicitCacheRetention,
                ReportsCacheReadTokens = preset.Capabilities.ReportsCacheReadTokens,
                ReportsCacheWriteTokens = preset.Capabilities.ReportsCacheWriteTokens,
                MaxContextTokens = preset.Capabilities.MaxContextTokens,
                MaxOutputTokens = preset.Capabilities.MaxOutputTokens
            };
        }

        return GuessCapabilities(Normalize(model.Provider) ?? config.Llm.Provider);
    }

    private static string? ResolveEndpoint(GatewayConfig config, ModelProfile profile)
    {
        var endpoint = profile.BaseUrl ?? config.Llm.Endpoint;
        return profile.ProviderId.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? OllamaEndpointNormalizer.NormalizeBaseUrl(endpoint)
            : endpoint;
    }

    private static bool ProfileUsesCompatibilityTransport(ModelProfile profile)
        => profile.ProviderId.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
           OllamaEndpointNormalizer.UsesCompatibilityEndpoint(profile.BaseUrl);

    private static IReadOnlyList<string> BuildCompatibilityNotes(ModelProfile profile)
    {
        var notes = new List<string>();
        if (profile.ProviderId.Equals("ollama", StringComparison.OrdinalIgnoreCase) && ProfileUsesCompatibilityTransport(profile))
            notes.Add("Using legacy /v1 compatibility endpoint; migrate to the native Ollama base URL.");
        if (profile.ProviderId.Equals("ollama", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(profile.PresetId))
            notes.Add("No local preset is configured; setup and doctor guidance will be more limited until a PresetId is added.");

        if (LocalModelPresetCatalog.TryGet(profile.PresetId, out var preset) && preset is not null)
            notes.AddRange(preset.CompatibilityNotes);

        return notes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] NormalizeDistinct(IEnumerable<string>? values)
        => values is null
            ? []
            : values.Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static PromptCachingConfig MergePromptCaching(PromptCachingConfig inherited, PromptCachingConfig? configured)
    {
        if (configured is null)
            return ClonePromptCaching(inherited);

        return new PromptCachingConfig
        {
            Enabled = configured.Enabled ?? inherited.Enabled,
            Retention = string.IsNullOrWhiteSpace(configured.Retention) ? inherited.Retention : configured.Retention,
            Dialect = string.IsNullOrWhiteSpace(configured.Dialect) ? inherited.Dialect : configured.Dialect,
            KeepWarmEnabled = configured.KeepWarmEnabled ?? inherited.KeepWarmEnabled,
            KeepWarmIntervalMinutes = configured.KeepWarmIntervalMinutes > 0 ? configured.KeepWarmIntervalMinutes : inherited.KeepWarmIntervalMinutes,
            TraceEnabled = configured.TraceEnabled ?? inherited.TraceEnabled,
            TraceFilePath = string.IsNullOrWhiteSpace(configured.TraceFilePath) ? inherited.TraceFilePath : configured.TraceFilePath
        };
    }

    private static PromptCachingConfig ClonePromptCaching(PromptCachingConfig source)
        => new()
        {
            Enabled = source.Enabled,
            Retention = source.Retention,
            Dialect = source.Dialect,
            KeepWarmEnabled = source.KeepWarmEnabled,
            KeepWarmIntervalMinutes = source.KeepWarmIntervalMinutes,
            TraceEnabled = source.TraceEnabled,
            TraceFilePath = source.TraceFilePath
        };

    private bool TryResolveRegisteredClient(ModelProfile profile, out IChatClient? client)
    {
        client = null;
        if (_providerRegistry is null || !_providerRegistry.TryGet(profile.ProviderId, out var registration) || registration?.Client is null)
            return false;

        if (registration.Models.Length > 0 &&
            !registration.Models.Contains(profile.ModelId, StringComparer.OrdinalIgnoreCase) &&
            !profile.FallbackModels.Any(model => registration.Models.Contains(model, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        client = registration.Client;
        return true;
    }
}
