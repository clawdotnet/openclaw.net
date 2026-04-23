using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IModelProfileRegistry
{
    string? DefaultProfileId { get; }
    bool TryGet(string profileId, out ModelProfile? profile);
    IReadOnlyList<ModelProfileStatus> ListStatuses();
}

public sealed class ModelSelectionRequest
{
    public string? ExplicitProfileId { get; init; }
    public required Session Session { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public ChatOptions? Options { get; init; }
    public bool Streaming { get; init; }
    public long? EstimatedInputTokens { get; init; }
    public int? ReservedOutputTokens { get; init; }
}

public sealed class ModelSelectionCandidate
{
    public required ModelProfile Profile { get; init; }
    public string[] FallbackModels { get; init; } = [];
}

public sealed class ModelSelectionResult
{
    public string? RequestedProfileId { get; init; }
    public string? SelectedProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required ModelSelectionRequirements Requirements { get; init; }
    public IReadOnlyList<ModelSelectionCandidate> Candidates { get; init; } = [];
    public string[] PreferredTags { get; init; } = [];
    public string? Explanation { get; init; }
}

public interface IModelSelectionPolicy
{
    ModelSelectionResult Resolve(ModelSelectionRequest request);
}
