using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;
namespace OpenClaw.Core.Skills.Meta;

[JsonConverter(typeof(JsonStringEnumConverter<ResolveCapabilitySelectionPolicy>))]
public enum ResolveCapabilitySelectionPolicy { First, ExactName }

public sealed record ResolveCapabilityRequest(
    [property: JsonPropertyName("task_description")] string TaskDescription,
    [property: JsonPropertyName("key_words")] string? KeyWords,
    [property: JsonPropertyName("selection_policy")] ResolveCapabilitySelectionPolicy SelectionPolicy)
{
    public string Provider { get; init; } = "";
    public string? CapabilityType { get; init; }
}

public sealed record CapabilityCandidate(string Name, string Description, int Rank)
{
    public string? Version { get; init; }
    public double? Score { get; init; }
}
public sealed record ResolveCapabilityBinding(string Server, string Tool, string Schema, [property: JsonPropertyName("tried")] IReadOnlyList<CapabilityCandidate> TriedCandidates)
{
    public string Provider { get; init; } = "";
    public string SchemaFingerprint { get; init; } = "";
    public long Revision { get; init; }
}
public sealed record ResolveCapabilityFailure(string FailureCode, [property: JsonPropertyName("tried")] IReadOnlyList<CapabilityCandidate> TriedCandidates);
public static class ResolveCapabilityFailureCodes
{
    public const string NoCandidates = "no_candidates";
    public const string AllAddsFailed = "all_bindings_failed";
    public const string SelectionPolicyNoMatch = "selection_policy_no_match";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ToolPolicyDenied = "tool_policy_denied";
}
public static class CapabilitySlotFailureCodes
{
    public const string NotConfigured = "capability_not_configured";
    public const string ProviderUnavailable = "capability_provider_unavailable";
    public const string BindingFailed = "capability_binding_failed";
    public const string ExecutionFailed = "capability_execution_failed";
    public const string ResolveFailed = "capability_resolve_failed";
}

/// <summary>Vendor-specific discovery and binding. Selection and execution policy belong to the runtime.</summary>
public interface ICapabilityProvider
{
    string Id { get; }
    Task<IReadOnlyList<CapabilityCandidate>> DiscoverAsync(ResolveCapabilityRequest request, CancellationToken ct);
    Task<CapabilityTarget?> BindAsync(string target, string? tool, CancellationToken ct);
}

/// <summary>A tool handle, never permission to execute. The runtime applies its tool policies on every invocation.</summary>
public sealed record CapabilityTarget(string Server, ITool Tool, bool RetrySafe = false)
{
    public string ToolId { get; init; } = Tool.Name;
}

public sealed record CapabilityChange(string Provider, string Scope, string Revision);
public interface ICapabilityInvalidationSink { void Invalidate(CapabilityChange change); }

/// <summary>Optional adapter lifecycle; no SDK-specific types cross this boundary.</summary>
public interface ICapabilityChangeSource : IAsyncDisposable
{
    string ProviderId { get; }
    string Status { get; }
    Task StartAsync(CancellationToken ct);
}

public sealed record CapabilityProviderStatus(string Id, string Events);
public sealed record CapabilityRuntimeStatus(string DefaultProvider, CapabilityProviderStatus[] Providers, long CacheGeneration, int CachedBindings);
