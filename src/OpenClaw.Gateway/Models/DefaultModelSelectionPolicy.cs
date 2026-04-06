using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Models;

internal sealed class DefaultModelSelectionPolicy : IModelSelectionPolicy
{
    private readonly ConfiguredModelProfileRegistry _registry;

    public DefaultModelSelectionPolicy(ConfiguredModelProfileRegistry registry)
    {
        _registry = registry;
    }

    public ModelSelectionResult Resolve(ModelSelectionRequest request)
    {
        var requirements = BuildRequirements(request);
        var preferredTags = CollectPreferredTags(request.Session);
        var fallbackProfileIds = CollectFallbackProfileIds(request.Session);
        var explicitProfileId = Normalize(request.ExplicitProfileId) ?? Normalize(request.Session.ModelProfileId);

        var attempted = new List<ModelSelectionCandidate>();
        if (!string.IsNullOrWhiteSpace(explicitProfileId))
        {
            if (!_registry.TryGetRegistration(explicitProfileId, out var explicitRegistration) || explicitRegistration is null)
                throw new ModelSelectionException($"Selected model profile '{explicitProfileId}' is not registered.");

            attempted.Add(new ModelSelectionCandidate
            {
                Profile = explicitRegistration.Profile,
                FallbackModels = explicitRegistration.ProviderConfig.FallbackModels
            });

            if (Satisfies(explicitRegistration.Profile, requirements))
                return BuildResult(explicitProfileId, explicitRegistration.Profile, requirements, preferredTags, attempted, null);

            foreach (var fallbackId in explicitRegistration.Profile.FallbackProfileIds.Concat(fallbackProfileIds))
            {
                if (!_registry.TryGetRegistration(fallbackId, out var fallbackRegistration) || fallbackRegistration is null)
                    continue;

                attempted.Add(new ModelSelectionCandidate
                {
                    Profile = fallbackRegistration.Profile,
                    FallbackModels = fallbackRegistration.ProviderConfig.FallbackModels
                });

                if (Satisfies(fallbackRegistration.Profile, requirements))
                {
                    var explanation =
                        $"Falling back from '{explicitRegistration.Profile.Id}' to '{fallbackRegistration.Profile.Id}' because {DescribeMissing(explicitRegistration.Profile, requirements)}.";
                    return BuildResult(explicitProfileId, fallbackRegistration.Profile, requirements, preferredTags, attempted, explanation);
                }
            }

            throw new ModelSelectionException(
                $"This route requires {DescribeRequirementSummary(requirements)}, but selected model profile '{explicitRegistration.Profile.Id}' does not support it.");
        }

        var candidates = _registry.ListStatuses()
            .OrderByDescending(item => Score(item, preferredTags, requirements))
            .ThenByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var status in candidates)
        {
            if (!_registry.TryGetRegistration(status.Id, out var registration) || registration is null)
                continue;

            if (!Satisfies(registration.Profile, requirements))
                continue;

            attempted.Add(new ModelSelectionCandidate
            {
                Profile = registration.Profile,
                FallbackModels = registration.ProviderConfig.FallbackModels
            });

            return BuildResult(null, registration.Profile, requirements, preferredTags, attempted, null);
        }

        throw new ModelSelectionException(
            $"No configured model profile satisfies the current request requirements ({DescribeRequirementSummary(requirements)}).");
    }

    private static ModelSelectionResult BuildResult(
        string? requestedProfileId,
        ModelProfile selectedProfile,
        ModelSelectionRequirements requirements,
        string[] preferredTags,
        IReadOnlyList<ModelSelectionCandidate> candidates,
        string? explanation)
        => new()
        {
            RequestedProfileId = requestedProfileId,
            SelectedProfileId = selectedProfile.Id,
            ProviderId = selectedProfile.ProviderId,
            ModelId = selectedProfile.ModelId,
            Requirements = requirements,
            PreferredTags = preferredTags,
            Candidates = candidates,
            Explanation = explanation
        };

    private static int Score(ModelProfileStatus status, IReadOnlyList<string> preferredTags, ModelSelectionRequirements requirements)
    {
        var score = status.IsDefault ? 100 : 0;
        score += preferredTags.Count(tag => status.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) * 10;
        if (requirements.SupportsTools == true && status.Capabilities.SupportsTools)
            score += 25;
        if (requirements.SupportsVision == true && status.Capabilities.SupportsVision)
            score += 20;
        if (requirements.SupportsStructuredOutputs == true && status.Capabilities.SupportsStructuredOutputs)
            score += 15;
        if (requirements.SupportsStreaming == true && status.Capabilities.SupportsStreaming)
            score += 10;
        if (requirements.SupportsReasoningEffort == true && status.Capabilities.SupportsReasoningEffort)
            score += 5;
        if (status.IsAvailable)
            score += 5;
        return score;
    }

    internal static ModelSelectionRequirements BuildRequirements(ModelSelectionRequest request)
    {
        var combined = Clone(request.Session.ModelRequirements);

        if (request.Streaming)
            combined.SupportsStreaming = true;
        if (request.Options?.Tools is { Count: > 0 })
        {
            combined.SupportsTools = true;
            if (request.Options.Tools.Count > 1)
                combined.SupportsParallelToolCalls ??= true;
        }

        if (request.Options?.ResponseFormat is not null)
        {
            combined.SupportsJsonSchema = true;
            combined.SupportsStructuredOutputs = true;
        }

        if (!string.IsNullOrWhiteSpace(request.Session.ReasoningEffort))
            combined.SupportsReasoningEffort = true;

        if (request.Messages.Any(static message => message.Role == ChatRole.System))
            combined.SupportsSystemMessages = true;

        if (request.Messages.SelectMany(static message => message.Contents).OfType<UriContent>().Any(static content => HasMediaPrefix(content.MediaType, "image/")))
        {
            combined.SupportsVision = true;
            combined.SupportsImageInput = true;
        }

        if (request.Messages.SelectMany(static message => message.Contents).OfType<UriContent>().Any(static content => HasMediaPrefix(content.MediaType, "audio/")))
            combined.SupportsAudioInput = true;

        return combined;
    }

    private static bool HasMediaPrefix(string? mediaType, string prefix)
        => !string.IsNullOrWhiteSpace(mediaType) &&
           mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static ModelSelectionRequirements Clone(ModelSelectionRequirements? source)
        => source is null
            ? new ModelSelectionRequirements()
            : new ModelSelectionRequirements
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
                MinContextTokens = source.MinContextTokens,
                MinOutputTokens = source.MinOutputTokens
            };

    private static bool Satisfies(ModelProfile profile, ModelSelectionRequirements requirements)
    {
        var caps = profile.Capabilities;
        return Meets(requirements.SupportsTools, caps.SupportsTools)
            && Meets(requirements.SupportsVision, caps.SupportsVision)
            && Meets(requirements.SupportsJsonSchema, caps.SupportsJsonSchema)
            && Meets(requirements.SupportsStructuredOutputs, caps.SupportsStructuredOutputs)
            && Meets(requirements.SupportsStreaming, caps.SupportsStreaming)
            && Meets(requirements.SupportsParallelToolCalls, caps.SupportsParallelToolCalls)
            && Meets(requirements.SupportsReasoningEffort, caps.SupportsReasoningEffort)
            && Meets(requirements.SupportsSystemMessages, caps.SupportsSystemMessages)
            && Meets(requirements.SupportsImageInput, caps.SupportsImageInput)
            && Meets(requirements.SupportsAudioInput, caps.SupportsAudioInput)
            && (!requirements.MinContextTokens.HasValue || caps.MaxContextTokens >= requirements.MinContextTokens.Value)
            && (!requirements.MinOutputTokens.HasValue || caps.MaxOutputTokens >= requirements.MinOutputTokens.Value);
    }

    private static bool Meets(bool? required, bool actual)
        => required is not true || actual;

    private static string DescribeMissing(ModelProfile profile, ModelSelectionRequirements requirements)
    {
        var missing = new List<string>();
        if (requirements.SupportsTools == true && !profile.Capabilities.SupportsTools)
            missing.Add("tool calling was required");
        if (requirements.SupportsVision == true && !profile.Capabilities.SupportsVision)
            missing.Add("vision was required");
        if (requirements.SupportsJsonSchema == true && !profile.Capabilities.SupportsJsonSchema)
            missing.Add("JSON schema output was required");
        if (requirements.SupportsStructuredOutputs == true && !profile.Capabilities.SupportsStructuredOutputs)
            missing.Add("structured output was required");
        if (requirements.SupportsStreaming == true && !profile.Capabilities.SupportsStreaming)
            missing.Add("streaming was required");
        if (requirements.SupportsReasoningEffort == true && !profile.Capabilities.SupportsReasoningEffort)
            missing.Add("reasoning effort was required");
        if (requirements.SupportsImageInput == true && !profile.Capabilities.SupportsImageInput)
            missing.Add("image input was required");
        if (requirements.SupportsAudioInput == true && !profile.Capabilities.SupportsAudioInput)
            missing.Add("audio input was required");
        return missing.Count == 0 ? "required capabilities were not satisfied" : string.Join(", ", missing);
    }

    private static string DescribeRequirementSummary(ModelSelectionRequirements requirements)
    {
        var items = new List<string>();
        if (requirements.SupportsTools == true)
            items.Add("tool calling");
        if (requirements.SupportsVision == true)
            items.Add("vision");
        if (requirements.SupportsJsonSchema == true)
            items.Add("JSON schema");
        if (requirements.SupportsStructuredOutputs == true)
            items.Add("structured outputs");
        if (requirements.SupportsStreaming == true)
            items.Add("streaming");
        if (requirements.SupportsReasoningEffort == true)
            items.Add("reasoning effort");
        if (requirements.SupportsImageInput == true)
            items.Add("image input");
        if (requirements.SupportsAudioInput == true)
            items.Add("audio input");
        return items.Count == 0 ? "the requested capabilities" : string.Join("+", items);
    }

    private static string[] CollectPreferredTags(Session session)
        => session.PreferredModelTags
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] CollectFallbackProfileIds(Session session)
        => session.FallbackModelProfileIds
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
