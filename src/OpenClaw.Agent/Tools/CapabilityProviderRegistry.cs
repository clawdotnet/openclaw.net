using System.Security.Cryptography;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Skills.Meta;
namespace OpenClaw.Agent.Tools;

public sealed class CapabilityProviderRegistry(IEnumerable<ICapabilityProvider> providers, string defaultProvider = "local")
{
    private readonly Dictionary<string, ICapabilityProvider> _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
    public IReadOnlyList<string> ProviderIds => _providers.Keys.Order(StringComparer.Ordinal).ToArray();
    public string DefaultProvider { get; } = defaultProvider;
    public ICapabilityProvider? Get(string? id) => _providers.GetValueOrDefault(string.IsNullOrWhiteSpace(id) ? DefaultProvider : id);
    public async Task<(ResolveCapabilityBinding? Binding, ResolveCapabilityFailure? Failure, IReadOnlyList<CapabilityCandidate> Candidates, CapabilityTarget? Target)> ResolveAsync(ResolveCapabilityRequest request, CancellationToken ct)
    {
        var provider = Get(request.Provider);
        var tried = new List<CapabilityCandidate>();
        IReadOnlyList<CapabilityCandidate> candidates = [];
        if (provider is null) return (null, new(ResolveCapabilityFailureCodes.ProviderUnavailable, []), [], null);
        try
        {
            candidates = (await provider.DiscoverAsync(request, ct)).OrderBy(c => c.Rank).ThenBy(c => c.Name, StringComparer.Ordinal).Take(5).ToArray();
            if (candidates.Count == 0) return (null, new(ResolveCapabilityFailureCodes.NoCandidates, []), [], null);
            var selected = request.SelectionPolicy == ResolveCapabilitySelectionPolicy.ExactName
                ? candidates.Where(c => string.Equals(c.Name, request.TaskDescription, StringComparison.OrdinalIgnoreCase)) : candidates;
            foreach (var candidate in selected)
            {
                tried.Add(candidate);
                var target = await provider.BindAsync(candidate.Name, null, ct);
                if (target is null) continue;
                var binding = new ResolveCapabilityBinding(target.Server, target.ToolId, target.Tool.ParameterSchema, tried.ToArray())
                {
                    Provider = provider.Id,
                    SchemaFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Tool.ParameterSchema)))
                };
                return (binding, null, candidates, target);
            }
            return (null, new(tried.Count == 0 ? ResolveCapabilityFailureCodes.SelectionPolicyNoMatch : ResolveCapabilityFailureCodes.AllAddsFailed, tried), candidates, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ToolOutcomeException or HttpRequestException or TimeoutException or OperationCanceledException)
        { return (null, new(ResolveCapabilityFailureCodes.ProviderUnavailable, tried), candidates, null); }
    }
}

/// <summary>Local discovery is deterministic and requires no external registry.</summary>
public sealed class LocalCapabilityProvider(Func<IReadOnlyList<ITool>> tools) : ICapabilityProvider
{
    private IReadOnlyList<ITool>? _runtimeTools;
    public void SetTools(IReadOnlyList<ITool> available) => _runtimeTools = available.Where(t => t is not OpenClaw.Agent.Tools.McpNativeTool).ToArray();
    private IReadOnlyList<ITool> Available => _runtimeTools ?? tools();
    public string Id => "local";
    public Task<IReadOnlyList<CapabilityCandidate>> DiscoverAsync(ResolveCapabilityRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var terms = (request.KeyWords ?? request.TaskDescription).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IReadOnlyList<CapabilityCandidate> matches = Available.Where(t => t.Name != "resolve_capability" &&
            (t.Name.Equals(request.TaskDescription, StringComparison.OrdinalIgnoreCase) || terms.Any(k => (t.Name + " " + t.Description).Contains(k, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(t => t.Name, StringComparer.Ordinal).Select((t, i) => new CapabilityCandidate(t.Name, t.Description, i + 1)).ToArray();
        return Task.FromResult(matches);
    }
    public Task<CapabilityTarget?> BindAsync(string target, string? tool, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var match = Available.FirstOrDefault(t => t.Name == (tool ?? target) && t.Name != "resolve_capability");
        return Task.FromResult(match is null ? null : new CapabilityTarget(target, match, match is IRetrySafeCapabilityTool));
    }
}
/// <summary>Explicit permission to retry a failed execution. Unknown tools are never assumed idempotent.</summary>
public interface IRetrySafeCapabilityTool { }
