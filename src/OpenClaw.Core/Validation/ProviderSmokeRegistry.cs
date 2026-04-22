using System.Collections.Concurrent;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

public sealed class ProviderSmokeRegistration
{
    public required string ProviderId { get; init; }
    public Func<LlmProviderConfig, CancellationToken, Task<ProviderSmokeProbeResult>>? ProbeAsync { get; init; }
    public bool TreatAsConfigured { get; init; } = true;
    public string? SkipReason { get; init; }
}

public sealed class ProviderSmokeRegistry
{
    private readonly ConcurrentDictionary<string, ProviderSmokeRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterHandler(
        string providerId,
        Func<LlmProviderConfig, CancellationToken, Task<ProviderSmokeProbeResult>> probeAsync,
        bool treatAsConfigured = true)
    {
        var normalized = Normalize(providerId);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _registrations[normalized] = new ProviderSmokeRegistration
        {
            ProviderId = normalized,
            ProbeAsync = probeAsync,
            TreatAsConfigured = treatAsConfigured
        };
    }

    public void RegisterMetadata(
        string providerId,
        bool treatAsConfigured = true,
        string? skipReason = null)
    {
        var normalized = Normalize(providerId);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _registrations[normalized] = new ProviderSmokeRegistration
        {
            ProviderId = normalized,
            TreatAsConfigured = treatAsConfigured,
            SkipReason = skipReason
        };
    }

    public bool TryGet(string providerId, out ProviderSmokeRegistration? registration)
        => _registrations.TryGetValue(Normalize(providerId), out registration);

    public IReadOnlyList<ProviderSmokeRegistration> Snapshot()
        => _registrations.Values
            .OrderBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Normalize(string? providerId)
        => string.IsNullOrWhiteSpace(providerId) ? string.Empty : providerId.Trim().ToLowerInvariant();
}
