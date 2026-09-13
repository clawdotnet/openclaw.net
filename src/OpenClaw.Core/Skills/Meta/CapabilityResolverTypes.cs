using System.Text.Json.Serialization;

namespace OpenClaw.Core.Skills.Meta;

[JsonConverter(typeof(JsonStringEnumConverter<ResolveCapabilitySelectionPolicy>))]
public enum ResolveCapabilitySelectionPolicy
{
    First = 0,
    ExactName = 1,
}

public sealed record ResolveCapabilityRequest(
    [property: JsonPropertyName("task_description")] string TaskDescription,
    [property: JsonPropertyName("key_words")] string? KeyWords,
    [property: JsonPropertyName("selection_policy")] ResolveCapabilitySelectionPolicy SelectionPolicy);

/// <summary>
/// A single candidate server emitted by the upstream Nacos MCP Router. The
/// <see cref="Rank"/> is the candidate's position in the upstream's deterministic
/// top-N ordering. Upstream supplies no scores; none are fabricated here.
/// </summary>
public sealed record RouterCandidate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("rank")] int Rank);

public sealed record ResolveCapabilityBinding(
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("tried")] IReadOnlyList<RouterCandidate> TriedCandidates);

public sealed record ResolveCapabilityFailure(
    [property: JsonPropertyName("failure_code")] string FailureCode,
    [property: JsonPropertyName("tried")] IReadOnlyList<RouterCandidate> TriedCandidates);

public static class ResolveCapabilityFailureCodes
{
    public const string NoCandidates = "no_candidates";
    public const string AllAddsFailed = "all_adds_failed";
    public const string SelectionPolicyNoMatch = "selection_policy_no_match";
    public const string RouterUnavailable = "router_unavailable";
}
