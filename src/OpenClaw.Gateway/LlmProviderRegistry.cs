using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class LlmProviderRegistry
{
    internal sealed class Registration
    {
        public required string ProviderId { get; init; }
        public required string OwnerId { get; init; }
        public required IChatClient Client { get; init; }
        public required string[] Models { get; init; }
        public bool IsDynamic { get; init; }
        public bool IsDefault { get; set; }
    }

    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterDefault(LlmProviderConfig config, IChatClient client)
    {
        var models = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Model))
            models.Add(config.Model);
        if (config.FallbackModels is { Length: > 0 })
            models.AddRange(config.FallbackModels.Where(static item => !string.IsNullOrWhiteSpace(item)));

        var registration = new Registration
        {
            ProviderId = config.Provider,
            OwnerId = "builtin",
            Client = client,
            Models = models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            IsDynamic = false,
            IsDefault = true
        };

        _registrations[config.Provider] = registration;
        _registrations["default"] = registration;
    }

    public bool TryRegisterDynamic(string providerId, IChatClient client, string ownerId, string[] models)
    {
        var registration = new Registration
        {
            ProviderId = providerId,
            OwnerId = ownerId,
            Client = client,
            Models = models,
            IsDynamic = true,
            IsDefault = false
        };

        return _registrations.TryAdd(providerId, registration);
    }

    public bool MarkDefault(string providerId)
    {
        if (!_registrations.TryGetValue(providerId, out var registration))
            return false;

        registration.IsDefault = true;
        _registrations["default"] = registration;
        return true;
    }

    public void UnregisterOwnedBy(string ownerId)
    {
        foreach (var entry in _registrations)
        {
            if (string.Equals(entry.Key, "default", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(entry.Value.OwnerId, ownerId, StringComparison.Ordinal))
                _registrations.TryRemove(entry.Key, out _);
        }
    }

    public bool TryGet(string providerId, out Registration? registration)
        => _registrations.TryGetValue(providerId, out registration);

    public IReadOnlyList<Registration> Snapshot()
        => _registrations
            .Where(static item => !string.Equals(item.Key, "default", StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Value)
            .DistinctBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
