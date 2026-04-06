using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
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

    public ConfiguredModelProfileRegistry(GatewayConfig config, ILogger<ConfiguredModelProfileRegistry> logger)
    {
        _logger = logger;
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
                ValidationIssues = item.ValidationIssues,
                FallbackProfileIds = item.Profile.FallbackProfileIds,
                FallbackModels = item.Profile.FallbackModels
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
            Provider = config.Llm.Provider,
            Model = config.Llm.Model,
            BaseUrl = config.Llm.Endpoint,
            ApiKey = config.Llm.ApiKey,
            FallbackModels = config.Llm.FallbackModels,
            Capabilities = GuessCapabilities(config.Llm.Provider)
        };

    private static ModelCapabilities GuessCapabilities(string providerId)
    {
        var provider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        var supportsTools = provider is "openai" or "openai-compatible" or "azure-openai" or "groq" or "together" or "lmstudio" or "anthropic" or "claude" or "gemini" or "google";
        var supportsVision = provider is "openai" or "openai-compatible" or "azure-openai" or "gemini" or "google" or "ollama";
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
            SupportsAudioInput = provider is "openai" or "openai-compatible" or "azure-openai"
        };
    }

    private static ModelProfile ToProfile(GatewayConfig config, ModelProfileConfig model)
        => new()
        {
            Id = Normalize(model.Id) ?? "default",
            ProviderId = Normalize(model.Provider) ?? config.Llm.Provider,
            ModelId = Normalize(model.Model) ?? config.Llm.Model,
            BaseUrl = Normalize(model.BaseUrl),
            ApiKey = Normalize(model.ApiKey),
            Tags = NormalizeDistinct(model.Tags),
            FallbackProfileIds = NormalizeDistinct(model.FallbackProfileIds),
            FallbackModels = NormalizeDistinct(model.FallbackModels),
            Capabilities = model.Capabilities ?? GuessCapabilities(model.Provider),
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
             profile.ProviderId.Equals("azure-openai", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(profile.BaseUrl) &&
            string.IsNullOrWhiteSpace(config.Llm.Endpoint))
        {
            yield return "BaseUrl is required for OpenAI-compatible and Azure OpenAI profiles unless inherited from OpenClaw:Llm:Endpoint.";
        }

        if ((profile.ProviderId.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("groq", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("together", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("azure-openai", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
             profile.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
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
            Endpoint = profile.BaseUrl ?? config.Llm.Endpoint,
            FallbackModels = profile.FallbackModels,
            MaxTokens = profile.Capabilities.MaxOutputTokens > 0 ? profile.Capabilities.MaxOutputTokens : config.Llm.MaxTokens,
            Temperature = config.Llm.Temperature,
            TimeoutSeconds = config.Llm.TimeoutSeconds,
            RetryCount = config.Llm.RetryCount,
            CircuitBreakerThreshold = config.Llm.CircuitBreakerThreshold,
            CircuitBreakerCooldownSeconds = config.Llm.CircuitBreakerCooldownSeconds
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] NormalizeDistinct(IEnumerable<string>? values)
        => values is null
            ? []
            : values.Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
