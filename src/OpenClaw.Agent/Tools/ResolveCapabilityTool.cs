using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;
namespace OpenClaw.Agent.Tools;

public sealed class ResolveCapabilityTool(CapabilityProviderRegistry providers) : ITool
{
    public string Name => "resolve_capability";
    public string Description => "Resolve a capability through a configured provider using deterministic selection.";
    public string ParameterSchema => """{"type":"object","required":["task_description"],"properties":{"task_description":{"type":"string"},"provider":{"type":"string"},"keywords":{"type":"array","items":{"type":"string"}},"key_words":{"type":"string"},"selection_policy":{"type":"string","enum":["first","exact_name"]}}}""";
    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var r = doc.RootElement;
            var task = r.GetProperty("task_description").GetString();
            if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("task_description is required");
            var policy = r.TryGetProperty("selection_policy", out var p) ? p.GetString() : "first";
            if (policy is not ("first" or "exact_name")) throw new ArgumentException("Unsupported selection policy");
            if (r.TryGetProperty("prefer_version", out _) || r.TryGetProperty("top_k", out _)) throw new ArgumentException("Unsupported constraint");
            var keywords = r.TryGetProperty("keywords", out var k) ? string.Join(",", k.EnumerateArray().Select(x => x.GetString()))
                : r.TryGetProperty("key_words", out k) ? k.GetString() : null;
            var request = new ResolveCapabilityRequest(task, keywords, policy == "exact_name" ? ResolveCapabilitySelectionPolicy.ExactName : ResolveCapabilitySelectionPolicy.First)
            { Provider = r.TryGetProperty("provider", out var id) ? id.GetString() ?? "" : "" };
            var result = await providers.ResolveAsync(request, ct);
            return result.Binding is not null ? JsonSerializer.Serialize(result.Binding, ResolveCapabilitySerializerContext.Default.ResolveCapabilityBinding)
                : JsonSerializer.Serialize(result.Failure, ResolveCapabilitySerializerContext.Default.ResolveCapabilityFailure);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new ToolOutcomeException("Invalid capability request", "failed", "invalid_capability_request", ex.Message); }
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ResolveCapabilityBinding))]
[JsonSerializable(typeof(ResolveCapabilityFailure))]
internal sealed partial class ResolveCapabilitySerializerContext : JsonSerializerContext;
